using System.Data;
using System.Data.Common;
using System.ComponentModel.DataAnnotations;
using System.Text;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Logistica.Stock;
using OrionERP.Infrastructure.Features.Logistica.Support;

namespace OrionERP.Infrastructure.Features.Logistica.Stock;

public sealed class StockService : IStockService
{
  private readonly IDbConnectionFactory _connectionFactory;

  public StockService(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  }

  public async Task<IReadOnlyList<StockListItemDto>> GetStockAsync(StockFilter filter, CancellationToken ct = default)
  {
    filter ??= new StockFilter();
    var skip = Math.Max(filter.Skip, 0);
    var take = Math.Max(filter.Take, 0);

    var sql = new StringBuilder(
      """
      WITH AttachmentCounts AS (
          SELECT
              a.LocationId,
              a.MaterialId,
              COUNT(*) AS AttachmentCount
          FROM logistica.LocationMaterialAttachment a
          WHERE ISNULL(a.IsDeleted, 0) = 0
          GROUP BY a.LocationId, a.MaterialId
      )
      SELECT
          sb.Id AS StockBalanceId,
          l.Id AS LocationId,
          l.LocationCode,
          l.LocationName,
          l.LocationType,
          room.ROOM_NAME AS RoomName,
          m.Id AS MaterialId,
          m.MaterialCode,
          m.[Description] AS MaterialDescription,
          m.MaterialClass,
          m.Barcode,
          u.UnitName AS BaseUnitName,
          bp.PartnerName AS VendorName,
          CAST(sb.Quantity AS decimal(18,4)) AS Quantity,
          CAST(sb.MinQuantity AS decimal(18,4)) AS MinQuantity,
          CAST(sb.MaxQuantity AS decimal(18,4)) AS MaxQuantity,
          CAST(CASE WHEN sb.MinQuantity IS NOT NULL AND sb.Quantity <= sb.MinQuantity THEN 1 ELSE 0 END AS bit) AS IsLowStock,
          CAST(CASE
              WHEN sb.CountFrequencyDays IS NULL THEN 0
              WHEN sb.LastCountedAt IS NULL THEN 1
              WHEN DATEADD(day, sb.CountFrequencyDays, sb.LastCountedAt) <= SYSUTCDATETIME() THEN 1
              ELSE 0
          END AS bit) AS IsCountDue,
          sb.LastCountedAt,
          sb.CountFrequencyDays,
          ISNULL(ac.AttachmentCount, 0) AS AttachmentCount,
          CAST(ISNULL(sb.IsRemoved, 0) AS bit) AS IsRemoved,
          sb.RemovedAt,
          sb.RemovedBy
      FROM logistica.StockBalance sb
      JOIN logistica.Location l
        ON l.Id = sb.LocationId
      LEFT JOIN dbo.ROOM room
        ON room.ID = l.RoomId
      JOIN logistica.Material m
        ON m.Id = sb.MaterialId
      LEFT JOIN logistica.UnitOfMeasure u
        ON u.Id = m.BaseUnitId
      LEFT JOIN logistica.MaterialVendor primaryVendor
        ON primaryVendor.Rfc = m.Rfc AND primaryVendor.MaterialId = m.Id AND primaryVendor.IsPrimary = 1
      LEFT JOIN dbo.BusinessPartner bp
        ON bp.Id = primaryVendor.BusinessPartnerId
      LEFT JOIN AttachmentCounts ac
        ON ac.LocationId = sb.LocationId
       AND ac.MaterialId = sb.MaterialId
      WHERE 1 = 1
      """);

    var parameters = new DynamicParameters();

    if (!filter.IncludeZeroBalances)
    {
      sql.AppendLine(" AND (sb.Quantity <> 0 OR ISNULL(sb.IsRemoved, 0) = 1)");
    }

    if (!filter.IncludeRemoved)
    {
      sql.AppendLine(" AND ISNULL(sb.IsRemoved, 0) = 0");
    }

    if (filter.RoomId.HasValue)
    {
      sql.AppendLine(" AND l.RoomId = @RoomId");
      parameters.Add("@RoomId", filter.RoomId.Value, DbType.Int32);
    }

    if (filter.LocationId.HasValue)
    {
      sql.AppendLine(" AND (l.Id = @LocationId OR l.ParentLocationId = @LocationId)");
      parameters.Add("@LocationId", filter.LocationId.Value, DbType.Int32);
    }

    if (!string.IsNullOrWhiteSpace(filter.SearchText))
    {
      sql.AppendLine(
        """
         AND (
             m.MaterialCode LIKE @Search
             OR m.[Description] LIKE @Search
             OR m.Barcode LIKE @Search
             OR l.LocationCode LIKE @Search
             OR l.LocationName LIKE @Search
             OR room.ROOM_NAME LIKE @Search
         )
        """);
      parameters.Add("@Search", $"%{filter.SearchText.Trim()}%", DbType.String);
    }

    if (filter.LowStockOnly)
    {
      sql.AppendLine(" AND sb.MinQuantity IS NOT NULL AND sb.Quantity <= sb.MinQuantity");
    }

    if (filter.CountDueOnly)
    {
      sql.AppendLine(
        """
         AND sb.CountFrequencyDays IS NOT NULL
         AND (
             sb.LastCountedAt IS NULL
             OR DATEADD(day, sb.CountFrequencyDays, sb.LastCountedAt) <= SYSUTCDATETIME()
         )
        """);
    }

    sql.AppendLine($"ORDER BY ISNULL(sb.IsRemoved, 0), room.ROOM_NAME, l.LocationName, {MaterialSortOrder.SqlKeys("m")}, sb.Id");

    if (take > 0)
    {
      sql.AppendLine("OFFSET @Skip ROWS");
      sql.AppendLine("FETCH NEXT @Take ROWS ONLY;");
      parameters.Add("@Skip", skip, DbType.Int32);
      parameters.Add("@Take", take, DbType.Int32);
    }
    else
    {
      sql.AppendLine(";");
    }

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<StockListItemDto>(
      new CommandDefinition(sql.ToString(), parameters, cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<LogisticsCommandResult> AddMaterialToLocationAsync(LocationMaterialAddRequest request, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    if (request.LocationId <= 0)
    {
      return LogisticsCommandResult.Fail("Selecciona una ubicación válida.");
    }

    if (request.MaterialId <= 0)
    {
      return LogisticsCommandResult.Fail("Selecciona un material válido.");
    }

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      var location = await GetLocationStateAsync(conn, request.LocationId, tx, ct);
      if (location is null)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("La ubicación seleccionada ya no existe.");
      }

      if (!location.IsActive)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("No puedes agregar materiales a una ubicación inactiva.");
      }

      if (!location.IsInventoryEnabled)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("La ubicación seleccionada no está habilitada para inventario.");
      }

      var material = await GetMaterialStateAsync(conn, request.MaterialId, tx, ct);
      if (material is null)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("El material seleccionado ya no existe.");
      }

      if (!material.IsActive || !string.Equals(material.MaterialStatus, "ACTIVO", StringComparison.OrdinalIgnoreCase))
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Solo puedes agregar materiales activos a una ubicación.");
      }

      var actor = NormalizeActor(request.AddedBy);
      var stockBalance = await GetStockBalanceStateAsync(conn, request.LocationId, request.MaterialId, tx, ct);

      if (stockBalance is not null)
      {
        if (!stockBalance.IsRemoved)
        {
          await tx.RollbackAsync(ct);
          return LogisticsCommandResult.Fail("El material ya está activo en la ubicación seleccionada.");
        }

        var affected = await conn.ExecuteAsync(
          new CommandDefinition(
            """
            UPDATE logistica.StockBalance
            SET IsRemoved = 0,
                RemovedAt = NULL,
                RemovedBy = NULL,
                UpdatedAt = SYSUTCDATETIME()
            WHERE Id = @StockBalanceId
              AND ISNULL(IsRemoved, 0) = 1;
            """,
            new { StockBalanceId = stockBalance.Id },
            tx,
            cancellationToken: ct));

        if (affected == 0)
        {
          await tx.RollbackAsync(ct);
          return LogisticsCommandResult.Fail("No se pudo agregar el material porque cambió mientras se procesaba la solicitud.");
        }

        await conn.ExecuteAsync(
          new CommandDefinition(
            """
            UPDATE logistica.LocationMaterialAttachment
            SET IsDeleted = 0,
                DeletedAt = NULL,
                DeletedBy = NULL
            WHERE LocationId = @LocationId
              AND MaterialId = @MaterialId
              AND ISNULL(IsDeleted, 0) = 1;
            """,
            new
            {
              stockBalance.LocationId,
              stockBalance.MaterialId
            },
            tx,
            cancellationToken: ct));

        await InsertStockAuditAsync(
          conn,
          tx,
          stockBalance,
          transactionType: "Reactivated",
          performedBy: actor,
          notes: "Material agregado nuevamente a la ubicación.",
          ct);

        await tx.CommitAsync(ct);
        return LogisticsCommandResult.Ok("Material agregado a la ubicación correctamente.", stockBalance.Id);
      }

      var stockBalanceId = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(
          """
          INSERT INTO logistica.StockBalance
          (
              LocationId,
              MaterialId,
              Quantity,
              CreatedAt,
              UpdatedAt
          )
          VALUES
          (
              @LocationId,
              @MaterialId,
              0,
              SYSUTCDATETIME(),
              SYSUTCDATETIME()
          );

          SELECT CAST(SCOPE_IDENTITY() AS int);
          """,
          new
          {
            request.LocationId,
            request.MaterialId
          },
          tx,
          cancellationToken: ct));

      var addedStockBalance = new StockBalanceStateRow
      {
        Id = stockBalanceId,
        LocationId = request.LocationId,
        MaterialId = request.MaterialId,
        Quantity = 0,
        IsRemoved = false
      };

      await InsertStockAuditAsync(
        conn,
        tx,
        addedStockBalance,
        transactionType: "Added",
        performedBy: actor,
        notes: "Material agregado a la ubicación.",
        ct);

      await tx.CommitAsync(ct);
      return LogisticsCommandResult.Ok("Material agregado a la ubicación correctamente.", stockBalanceId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<LogisticsCommandResult> SaveStockThresholdsAsync(StockThresholdUpdateRequest request, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    var validationResults = new List<ValidationResult>();
    if (!Validator.TryValidateObject(request, new ValidationContext(request), validationResults, validateAllProperties: true))
    {
      return LogisticsCommandResult.Fail(validationResults[0].ErrorMessage ?? "Los parámetros de inventario no son válidos.");
    }

    using var conn = CreateConnection();
    var stockBalance = await GetStockBalanceStateAsync(conn, request.StockBalanceId, tx: null, ct);
    if (stockBalance is null)
    {
      return LogisticsCommandResult.Fail("El registro de inventario ya no existe.");
    }

    if (stockBalance.IsRemoved)
    {
      return LogisticsCommandResult.Fail("Reactiva el material antes de modificar sus parámetros.");
    }

    const string sql =
      """
      UPDATE logistica.StockBalance
      SET MinQuantity = @MinQuantity,
          MaxQuantity = @MaxQuantity,
          UpdatedAt = SYSUTCDATETIME()
      WHERE Id = @StockBalanceId
        AND ISNULL(IsRemoved, 0) = 0;
      """;

    var affected = await conn.ExecuteAsync(
      new CommandDefinition(
        sql,
        new
        {
          request.StockBalanceId,
          request.MinQuantity,
          request.MaxQuantity
        },
        cancellationToken: ct));

    return affected == 0
      ? LogisticsCommandResult.Fail("El registro de inventario ya no existe.")
      : LogisticsCommandResult.Ok("Parámetros de inventario guardados correctamente.", request.StockBalanceId);
  }

  public async Task<IReadOnlyList<StockTransactionDto>> GetStockTransactionsAsync(int stockBalanceId, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          st.Id,
          st.OccurredAt,
          st.TransactionType,
          CAST(st.QuantityDelta AS decimal(18,4)) AS QuantityDelta,
          CAST(st.QuantityAfter AS decimal(18,4)) AS QuantityAfter,
          st.ReferenceType,
          st.ReferenceId,
          st.Notes,
          st.PerformedBy
      FROM logistica.StockTransaction st
      WHERE st.StockBalanceId = @StockBalanceId
      ORDER BY st.OccurredAt DESC, st.Id DESC;
      """;

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<StockTransactionDto>(
      new CommandDefinition(sql, new { StockBalanceId = stockBalanceId }, cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<IReadOnlyList<LocationMaterialAttachmentDto>> GetLocationMaterialAttachmentsAsync(int locationId, int materialId, bool includeDeleted = false, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          a.Id,
          a.FileName,
          a.FileExtension,
          a.[Description],
          DATALENGTH(a.Attachment) AS [Length],
          a.CreatedAt,
          a.CreatedBy,
          CAST(ISNULL(a.IsDeleted, 0) AS bit) AS IsDeleted,
          a.DeletedAt,
          a.DeletedBy
      FROM logistica.LocationMaterialAttachment a
      WHERE a.LocationId = @LocationId
        AND a.MaterialId = @MaterialId
        AND (@IncludeDeleted = 1 OR ISNULL(a.IsDeleted, 0) = 0)
      ORDER BY ISNULL(a.IsDeleted, 0), a.CreatedAt DESC, a.Id DESC;
      """;

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<LocationMaterialAttachmentDto>(
      new CommandDefinition(
        sql,
        new
        {
          LocationId = locationId,
          MaterialId = materialId,
          IncludeDeleted = includeDeleted
        },
        cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<LogisticsBinaryContent?> GetLocationMaterialAttachmentContentAsync(int attachmentId, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          a.Id,
          a.FileName,
          a.ContentType,
          a.Attachment AS Bytes
      FROM logistica.LocationMaterialAttachment a
      WHERE a.Id = @AttachmentId;
      """;

    using var conn = CreateConnection();
    var row = await conn.QueryFirstOrDefaultAsync<LogisticsBinaryContent>(
      new CommandDefinition(sql, new { AttachmentId = attachmentId }, cancellationToken: ct));

    if (row is null)
    {
      return null;
    }

    row.ContentType = LogisticsContentTypes.Normalize(row.ContentType, row.FileName, row.Bytes);
    return row;
  }

  public async Task<LogisticsCommandResult> SaveLocationMaterialAttachmentAsync(LocationMaterialAttachmentCreateRequest request, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    if (request.Bytes.Length == 0)
    {
      return LogisticsCommandResult.Fail("Debes adjuntar un archivo para guardar evidencia.");
    }

    using var conn = CreateConnection();
    var stockBalance = await GetStockBalanceStateAsync(conn, request.LocationId, request.MaterialId, tx: null, ct);
    if (stockBalance is null)
    {
      return LogisticsCommandResult.Fail("No existe un registro de inventario para ese material en la ubicación seleccionada.");
    }

    if (stockBalance.IsRemoved)
    {
      return LogisticsCommandResult.Fail("Reactiva el material antes de guardar adjuntos.");
    }

    const string sql =
      """
      INSERT INTO logistica.LocationMaterialAttachment
      (
          LocationId,
          MaterialId,
          FileName,
          FileExtension,
          ContentType,
          [Description],
          Attachment,
          CreatedBy
      )
      VALUES
      (
          @LocationId,
          @MaterialId,
          @FileName,
          @FileExtension,
          @ContentType,
          @Description,
          @Attachment,
          @CreatedBy
      );

      SELECT CAST(SCOPE_IDENTITY() AS int);
      """;

    var id = await conn.ExecuteScalarAsync<int>(
      new CommandDefinition(
        sql,
        new
        {
          request.LocationId,
          request.MaterialId,
          FileName = request.FileName.Trim(),
          FileExtension = request.FileExtension.Trim().TrimStart('.'),
          ContentType = LogisticsContentTypes.Normalize(request.ContentType, request.FileName, request.Bytes),
          Description = NullIfWhiteSpace(request.Description),
          Attachment = request.Bytes,
          CreatedBy = "OrionERP"
        },
        cancellationToken: ct));

    return LogisticsCommandResult.Ok("Adjunto de inventario guardado correctamente.", id);
  }

  public async Task<LogisticsCommandResult> RemoveLocationMaterialAsync(int stockBalanceId, string? removedBy, CancellationToken ct = default)
  {
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      var stockBalance = await GetStockBalanceStateAsync(conn, stockBalanceId, tx, ct);
      if (stockBalance is null)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("El registro de inventario ya no existe.");
      }

      if (stockBalance.IsRemoved)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("El material ya está eliminado de esta ubicación.");
      }

      if (stockBalance.Quantity != 0)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Solo puedes quitar materiales con cantidad 0. Ajusta el inventario antes de eliminarlo.");
      }

      var actor = NormalizeActor(removedBy);

      var affected = await conn.ExecuteAsync(
        new CommandDefinition(
          """
          UPDATE logistica.StockBalance
          SET IsRemoved = 1,
              RemovedAt = SYSUTCDATETIME(),
              RemovedBy = @RemovedBy,
              UpdatedAt = SYSUTCDATETIME()
          WHERE Id = @StockBalanceId
            AND ISNULL(IsRemoved, 0) = 0;
          """,
          new
          {
            StockBalanceId = stockBalance.Id,
            RemovedBy = actor
          },
          tx,
          cancellationToken: ct));

      if (affected == 0)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("No se pudo eliminar el material porque cambió mientras se procesaba la solicitud.");
      }

      await conn.ExecuteAsync(
        new CommandDefinition(
          """
          UPDATE logistica.LocationMaterialAttachment
          SET IsDeleted = 1,
              DeletedAt = SYSUTCDATETIME(),
              DeletedBy = @DeletedBy
          WHERE LocationId = @LocationId
            AND MaterialId = @MaterialId
            AND ISNULL(IsDeleted, 0) = 0;
          """,
          new
          {
            stockBalance.LocationId,
            stockBalance.MaterialId,
            DeletedBy = actor
          },
          tx,
          cancellationToken: ct));

      await InsertStockAuditAsync(
        conn,
        tx,
        stockBalance,
        transactionType: "Removed",
        performedBy: actor,
        notes: "Material eliminado de la ubicación.",
        ct);

      await tx.CommitAsync(ct);
      return LogisticsCommandResult.Ok("Material eliminado de la ubicación correctamente.", stockBalance.Id);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<LogisticsCommandResult> ReactivateLocationMaterialAsync(int stockBalanceId, string? reactivatedBy, CancellationToken ct = default)
  {
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      var stockBalance = await GetStockBalanceStateAsync(conn, stockBalanceId, tx, ct);
      if (stockBalance is null)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("El registro de inventario ya no existe.");
      }

      if (!stockBalance.IsRemoved)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("El material ya está activo en esta ubicación.");
      }

      var actor = NormalizeActor(reactivatedBy);

      var affected = await conn.ExecuteAsync(
        new CommandDefinition(
          """
          UPDATE logistica.StockBalance
          SET IsRemoved = 0,
              RemovedAt = NULL,
              RemovedBy = NULL,
              UpdatedAt = SYSUTCDATETIME()
          WHERE Id = @StockBalanceId
            AND ISNULL(IsRemoved, 0) = 1;
          """,
          new { StockBalanceId = stockBalance.Id },
          tx,
          cancellationToken: ct));

      if (affected == 0)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("No se pudo reactivar el material porque cambió mientras se procesaba la solicitud.");
      }

      await conn.ExecuteAsync(
        new CommandDefinition(
          """
          UPDATE logistica.LocationMaterialAttachment
          SET IsDeleted = 0,
              DeletedAt = NULL,
              DeletedBy = NULL
          WHERE LocationId = @LocationId
            AND MaterialId = @MaterialId
            AND ISNULL(IsDeleted, 0) = 1;
          """,
          new
          {
            stockBalance.LocationId,
            stockBalance.MaterialId
          },
          tx,
          cancellationToken: ct));

      await InsertStockAuditAsync(
        conn,
        tx,
        stockBalance,
        transactionType: "Reactivated",
        performedBy: actor,
        notes: "Material reactivado en la ubicación.",
        ct);

      await tx.CommitAsync(ct);
      return LogisticsCommandResult.Ok("Material reactivado correctamente.", stockBalance.Id);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica de conexiones no devolvió una DbConnection.");

  private static async Task InsertStockAuditAsync(
    DbConnection conn,
    DbTransaction tx,
    StockBalanceStateRow stockBalance,
    string transactionType,
    string performedBy,
    string notes,
    CancellationToken ct)
  {
    await conn.ExecuteAsync(
      new CommandDefinition(
        """
        INSERT INTO logistica.StockTransaction
        (
            StockBalanceId,
            LocationId,
            MaterialId,
            TransactionType,
            QuantityDelta,
            QuantityAfter,
            ReferenceType,
            ReferenceId,
            Notes,
            PerformedBy,
            OccurredAt
        )
        VALUES
        (
            @StockBalanceId,
            @LocationId,
            @MaterialId,
            @TransactionType,
            0,
            @QuantityAfter,
            'StockBalance',
            @ReferenceId,
            @Notes,
            @PerformedBy,
            SYSUTCDATETIME()
        );
        """,
        new
        {
          StockBalanceId = stockBalance.Id,
          stockBalance.LocationId,
          stockBalance.MaterialId,
          TransactionType = transactionType,
          QuantityAfter = stockBalance.Quantity,
          ReferenceId = stockBalance.Id,
          Notes = notes,
          PerformedBy = performedBy
        },
        tx,
        cancellationToken: ct));
  }

  private static string NormalizeActor(string? actor)
    => NullIfWhiteSpace(actor) ?? "OrionERP";

  private static async Task<LocationStateRow?> GetLocationStateAsync(
    DbConnection conn,
    int locationId,
    DbTransaction? tx,
    CancellationToken ct)
  {
    const string sql =
      """
      SELECT
          l.Id,
          CAST(l.IsInventoryEnabled AS bit) AS IsInventoryEnabled,
          CAST(l.IsActive AS bit) AS IsActive
      FROM logistica.Location l
      WHERE l.Id = @LocationId;
      """;

    return await conn.QueryFirstOrDefaultAsync<LocationStateRow>(
      new CommandDefinition(sql, new { LocationId = locationId }, tx, cancellationToken: ct));
  }

  private static async Task<MaterialStateRow?> GetMaterialStateAsync(
    DbConnection conn,
    int materialId,
    DbTransaction? tx,
    CancellationToken ct)
  {
    const string sql =
      """
      SELECT
          m.Id,
          m.MaterialStatus,
          CAST(m.IsActive AS bit) AS IsActive
      FROM logistica.Material m
      WHERE m.Id = @MaterialId;
      """;

    return await conn.QueryFirstOrDefaultAsync<MaterialStateRow>(
      new CommandDefinition(sql, new { MaterialId = materialId }, tx, cancellationToken: ct));
  }

  private static async Task<StockBalanceStateRow?> GetStockBalanceStateAsync(
    DbConnection conn,
    int stockBalanceId,
    DbTransaction? tx,
    CancellationToken ct)
  {
    const string sql =
      """
      SELECT
          sb.Id,
          sb.LocationId,
          sb.MaterialId,
          CAST(sb.Quantity AS decimal(18,4)) AS Quantity,
          CAST(ISNULL(sb.IsRemoved, 0) AS bit) AS IsRemoved
      FROM logistica.StockBalance sb
      WHERE sb.Id = @StockBalanceId;
      """;

    return await conn.QueryFirstOrDefaultAsync<StockBalanceStateRow>(
      new CommandDefinition(sql, new { StockBalanceId = stockBalanceId }, tx, cancellationToken: ct));
  }

  private static async Task<StockBalanceStateRow?> GetStockBalanceStateAsync(
    DbConnection conn,
    int locationId,
    int materialId,
    DbTransaction? tx,
    CancellationToken ct)
  {
    const string sql =
      """
      SELECT
          TOP (1)
          sb.Id,
          sb.LocationId,
          sb.MaterialId,
          CAST(sb.Quantity AS decimal(18,4)) AS Quantity,
          CAST(ISNULL(sb.IsRemoved, 0) AS bit) AS IsRemoved
      FROM logistica.StockBalance sb
      WHERE sb.LocationId = @LocationId
        AND sb.MaterialId = @MaterialId
      ORDER BY sb.Id;
      """;

    return await conn.QueryFirstOrDefaultAsync<StockBalanceStateRow>(
      new CommandDefinition(
        sql,
        new
        {
          LocationId = locationId,
          MaterialId = materialId
        },
        tx,
        cancellationToken: ct));
  }

  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private sealed class LocationStateRow
  {
    public int Id { get; set; }
    public bool IsInventoryEnabled { get; set; }
    public bool IsActive { get; set; }
  }

  private sealed class MaterialStateRow
  {
    public int Id { get; set; }
    public string MaterialStatus { get; set; } = string.Empty;
    public bool IsActive { get; set; }
  }

  private sealed class StockBalanceStateRow
  {
    public int Id { get; set; }
    public int LocationId { get; set; }
    public int MaterialId { get; set; }
    public decimal Quantity { get; set; }
    public bool IsRemoved { get; set; }
  }
}
