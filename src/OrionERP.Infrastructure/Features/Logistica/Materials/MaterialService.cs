using System.Data;
using System.Data.Common;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Infrastructure.Features.Logistica.Support;

namespace OrionERP.Infrastructure.Features.Logistica.Materials;

public sealed class MaterialService : IMaterialService
{
  private static readonly string[] DefaultClasses = ["Consumable", "Reusable", "Installed", "AssetLike"];
  private static readonly string[] DefaultStatuses = ["ACTIVO", "OBSOLETO", "INACTIVO"];
  private const string DeleteConfirmationText = "Delete";
  private const int DeletionExampleLimit = 5;

  private static readonly IReadOnlyDictionary<string, MaterialDeletionBlockerDefinition> DeletionBlockerDefinitions =
    new MaterialDeletionBlockerDefinition[]
    {
      new("StockBalance", "Inventario por ubicación", "El material todavía está asignado a una o más ubicaciones, incluso si la existencia es cero o el registro fue retirado.", "Revisar ubicaciones", "/logistica/ubicaciones"),
      new("StockTransaction", "Historial de inventario", "Existen movimientos que deben conservar su referencia al material.", "Revisar inventario", "/restaurante/inventario"),
      new("LocationMaterialAttachment", "Adjuntos de ubicaciones", "Hay archivos activos o archivados asociados al material dentro de una ubicación.", "Revisar ubicaciones", "/logistica/ubicaciones"),
      new("PhysicalCountLine", "Conteos físicos", "El material forma parte del historial de uno o más conteos físicos.", "Revisar conteos", "/logistica/conteos"),
      new("MaterialLot", "Lotes del material", "Existen lotes registrados para el material, sin importar su estado o existencia actual.", "Revisar inventario", "/restaurante/inventario"),
      new("LotBalance", "Existencias por lote", "Hay saldos de lote vinculados al material en ubicaciones.", "Revisar inventario", "/restaurante/inventario"),
      new("InventoryReservationLine", "Reservas de inventario", "El material aparece en reservas activas o históricas.", "Revisar inventario", "/restaurante/inventario"),
      new("InventoryTransferLine", "Transferencias de inventario", "El material aparece en transferencias activas o históricas.", "Revisar inventario", "/restaurante/inventario"),
      new("InventoryAdjustmentLine", "Ajustes de inventario", "El material aparece en ajustes activos o históricos.", "Revisar inventario", "/restaurante/inventario"),
      new("PurchaseOrderLine", "Órdenes de compra", "El material está incluido en órdenes de compra, aunque estén canceladas o terminadas.", "Revisar compras", "/logistica/compras"),
      new("PurchaseReceiptLine", "Recepciones de compra", "El material forma parte del historial de recepciones de compra.", "Revisar compras", "/logistica/compras"),
      new("BomHeader", "BOM o receta del producto", "El material es el producto terminado de una lista de materiales o receta.", "Revisar recetas", "/restaurante/recetas"),
      new("BomComponent", "Ingrediente de BOM o receta", "El material se usa como componente o ingrediente de una lista de materiales o receta.", "Revisar recetas", "/restaurante/recetas"),
      new("ProductionOrder", "Órdenes de producción", "El material aparece como producto de una orden de producción.", "Revisar producción", "/restaurante/produccion"),
      new("RestaurantProduct", "Productos de restaurante", "El material está publicado como variante de un producto del restaurante.", "Revisar menús", "/restaurante/menus"),
      new("ModifierIngredientDelta", "Modificadores de restaurante", "El material se agrega o retira mediante opciones de modificadores.", "Revisar menús", "/restaurante/menus"),
      new("MaterialAllergen", "Asignaciones de alérgenos", "El material conserva asignaciones de alérgenos, incluso si el alérgeno está inactivo.", "Revisar recetas", "/restaurante/recetas"),
      new("MaterialUnitConversion", "Conversiones de unidad", "El material conserva conversiones especiales de unidad, incluso si están inactivas.", "Revisar recetas", "/restaurante/recetas")
    }.ToDictionary(definition => definition.Code, StringComparer.Ordinal);

  private readonly IDbConnectionFactory _connectionFactory;
  private readonly ILogger<MaterialService>? _logger;

  public MaterialService(IDbConnectionFactory connectionFactory, ILogger<MaterialService>? logger = null)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    _logger = logger;
  }

  public async Task<IReadOnlyList<MaterialListItemDto>> GetMaterialsAsync(MaterialFilter filter, CancellationToken ct = default)
  {
    filter ??= new MaterialFilter();
    var rfc = LogisticsRfc.Require(filter.Rfc);
    var skip = Math.Max(filter.Skip, 0);
    var take = Math.Max(filter.Take, 0);

    var sql = new StringBuilder(
      """
      WITH StockTotals AS (
          SELECT
              sb.MaterialId,
              SUM(sb.Quantity) AS TotalQuantity,
              COUNT(*) AS LocationCount
          FROM logistica.StockBalance sb
          WHERE sb.Rfc = @Rfc
            AND ISNULL(sb.IsRemoved, 0) = 0
          GROUP BY sb.MaterialId
      )
      SELECT
          m.Id,
          m.MaterialCode,
          m.[Description],
          m.BaseUnitId,
          m.MaterialClass,
          m.MaterialStatus AS [Status],
          mc.CategoryName,
          u.UnitName AS BaseUnitName,
          bp.PartnerName AS VendorName,
          CAST(m.Price AS decimal(18,4)) AS Price,
          CAST(CASE WHEN m.PrimaryImage IS NULL THEN 0 ELSE 1 END AS bit) AS HasImage,
          m.Barcode,
          CAST(ISNULL(st.TotalQuantity, 0) AS decimal(18,4)) AS TotalQuantity,
          ISNULL(st.LocationCount, 0) AS LocationCount
      FROM logistica.Material m
      LEFT JOIN logistica.MaterialCategory mc
        ON mc.Rfc = m.Rfc AND mc.Id = m.CategoryId
      LEFT JOIN logistica.UnitOfMeasure u
        ON u.Id = m.BaseUnitId
      LEFT JOIN dbo.BusinessPartner bp
        ON bp.Id = m.BusinessPartnerId
      LEFT JOIN StockTotals st
        ON st.MaterialId = m.Id
      WHERE m.Rfc = @Rfc
      """);

    var parameters = new DynamicParameters();
    parameters.Add("@Rfc", rfc, DbType.String);

    if (!string.IsNullOrWhiteSpace(filter.SearchText))
    {
      sql.AppendLine(" AND (m.MaterialCode LIKE @Search OR m.[Description] LIKE @Search OR m.Barcode LIKE @Search OR m.VendorCode LIKE @Search)");
      parameters.Add("@Search", $"%{filter.SearchText.Trim()}%", DbType.String);
    }

    if (filter.CategoryId.HasValue)
    {
      sql.AppendLine(" AND m.CategoryId = @CategoryId");
      parameters.Add("@CategoryId", filter.CategoryId.Value, DbType.Int32);
    }

    if (filter.VendorId.HasValue)
    {
      sql.AppendLine(" AND m.BusinessPartnerId = @VendorId");
      parameters.Add("@VendorId", filter.VendorId.Value, DbType.Int32);
    }

    if (!string.IsNullOrWhiteSpace(filter.MaterialClass))
    {
      sql.AppendLine(" AND m.MaterialClass = @MaterialClass");
      parameters.Add("@MaterialClass", filter.MaterialClass.Trim(), DbType.String);
    }

    if (!string.IsNullOrWhiteSpace(filter.Status))
    {
      sql.AppendLine(" AND m.MaterialStatus = @Status");
      parameters.Add("@Status", filter.Status.Trim(), DbType.String);
    }

    if (filter.HasImage.HasValue)
    {
      sql.AppendLine(filter.HasImage.Value ? " AND m.PrimaryImage IS NOT NULL" : " AND m.PrimaryImage IS NULL");
    }

    if (filter.HasStock.HasValue)
    {
      sql.AppendLine(filter.HasStock.Value
        ? " AND ISNULL(st.TotalQuantity, 0) > 0"
        : " AND ISNULL(st.TotalQuantity, 0) <= 0");
    }

    if (filter.NeedsAttention)
    {
      sql.AppendLine(
        """
         AND (
              m.BusinessPartnerId IS NULL
              OR m.CategoryId IS NULL
              OR NULLIF(LTRIM(RTRIM(m.Barcode)), '') IS NULL
              OR m.PrimaryImage IS NULL
         )
        """);
    }

    sql.AppendLine();
    sql.AppendLine("ORDER BY m.MaterialCode, m.[Description], m.Id");

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
    var rows = await conn.QueryAsync<MaterialListItemDto>(
      new CommandDefinition(sql.ToString(), parameters, cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<MaterialDetailDto?> GetMaterialAsync(string rfc, int materialId, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          m.Id,
          m.MaterialCode,
          m.LegacyMaterialId,
          m.[Description],
          m.BaseUnitId,
          baseU.UnitName AS BaseUnitName,
          CAST(m.PurchaseQuantity AS decimal(18,4)) AS PurchaseQuantity,
          m.PurchaseUnitId,
          purchaseU.UnitName AS PurchaseUnitName,
          m.BusinessPartnerId,
          CAST(m.Price AS decimal(18,4)) AS Price,
          m.CreatedDate,
          m.UpdatedDate,
          m.Brand,
          m.Model,
          m.IsPerishable,
          m.ShelfLifeDays,
          m.RequiresRefrigeration,
          m.MaterialStatus AS [Status],
          m.CategoryId,
          m.Barcode,
          m.VendorCode,
          m.PurchaseLink,
          m.MaterialClass,
          m.IsActive,
          CAST(CASE WHEN m.PrimaryImage IS NULL THEN 0 ELSE 1 END AS bit) AS HasImage,
          m.PrimaryImageFileName,
          m.PrimaryImageContentType
      FROM logistica.Material m
      LEFT JOIN logistica.UnitOfMeasure baseU
        ON baseU.Id = m.BaseUnitId
      LEFT JOIN logistica.UnitOfMeasure purchaseU
        ON purchaseU.Id = m.PurchaseUnitId
      WHERE m.Rfc = @Rfc
        AND m.Id = @MaterialId;
      """;

    using var conn = CreateConnection();
    return await conn.QueryFirstOrDefaultAsync<MaterialDetailDto>(
      new CommandDefinition(sql, new { Rfc = LogisticsRfc.Require(rfc), MaterialId = materialId }, cancellationToken: ct));
  }

  public async Task<MaterialCatalogDto> GetCatalogAsync(string rfc, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT Id, CategoryName AS Name, CategoryName AS Code
      FROM logistica.MaterialCategory category
      WHERE category.Rfc = @Rfc
        AND
        (
            category.IsActive = 1
            OR EXISTS
            (
                SELECT 1
                FROM logistica.Material material
                WHERE material.Rfc = @Rfc
                  AND material.CategoryId = category.Id
            )
        )
      ORDER BY category.CategoryName, category.Id;

      SELECT Id, UnitName AS Name, Abbreviation AS Code
      FROM logistica.UnitOfMeasure unit
      WHERE unit.IsActive = 1
         OR EXISTS
         (
             SELECT 1
             FROM logistica.Material material
             WHERE material.Rfc = @Rfc
               AND (material.BaseUnitId = unit.Id OR material.PurchaseUnitId = unit.Id)
         )
      ORDER BY unit.UnitName, unit.Id;

      SELECT
          bp.Id,
          bp.PartnerName AS Name,
          bp.Rfc AS Code
      FROM dbo.BusinessPartner bp
      WHERE EXISTS
        (
            SELECT 1
            FROM dbo.BusinessPartnerRfcScope scope
            WHERE scope.Rfc = @Rfc
              AND scope.BusinessPartnerId = bp.Id
              AND scope.IsActive = 1
        )
        AND
        (
            (
                bp.IsActive = 1
                AND
                (
                    EXISTS (SELECT 1 FROM dbo.BusinessPartnerRole r WHERE r.BusinessPartnerId = bp.Id AND r.RoleCode = 'Vendor')
                    OR EXISTS (SELECT 1 FROM logistica.VendorProfile vp WHERE vp.Rfc = @Rfc AND vp.BusinessPartnerId = bp.Id)
                )
            )
            OR EXISTS
            (
                SELECT 1
                FROM logistica.Material material
                WHERE material.Rfc = @Rfc
                  AND material.BusinessPartnerId = bp.Id
            )
        )
      ORDER BY bp.PartnerName, bp.Id;
      """;

    using var conn = CreateConnection();
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, new { Rfc = LogisticsRfc.Require(rfc) }, cancellationToken: ct));

    return new MaterialCatalogDto
    {
      Categories = (await multi.ReadAsync<LookupOptionDto>()).AsList(),
      Units = (await multi.ReadAsync<LookupOptionDto>()).AsList(),
      Vendors = (await multi.ReadAsync<LookupOptionDto>()).AsList(),
      MaterialClasses = DefaultClasses,
      Statuses = DefaultStatuses
    };
  }

  public async Task<LogisticsBinaryContent?> GetMaterialImageAsync(string rfc, int materialId, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          m.Id,
          COALESCE(m.PrimaryImageFileName, CONCAT(m.MaterialCode, '.bin')) AS FileName,
          m.PrimaryImageContentType AS ContentType,
          m.PrimaryImage AS Bytes
      FROM logistica.Material m
      WHERE m.Rfc = @Rfc
        AND m.Id = @MaterialId
        AND m.PrimaryImage IS NOT NULL;
      """;

    using var conn = CreateConnection();
    var row = await conn.QueryFirstOrDefaultAsync<LogisticsBinaryContent>(
      new CommandDefinition(sql, new { Rfc = LogisticsRfc.Require(rfc), MaterialId = materialId }, cancellationToken: ct));

    if (row is null)
    {
      return null;
    }

    row.ContentType = LogisticsContentTypes.Normalize(row.ContentType, row.FileName, row.Bytes);
    return row;
  }

  public async Task<LogisticsBinaryContent?> GetMaterialThumbnailAsync(string rfc, int materialId, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          m.Id,
          COALESCE(NULLIF(m.PrimaryImageFileName, ''), CONCAT(m.MaterialCode, '-thumbnail.jpg')) AS FileName,
          COALESCE(m.PrimaryImageThumbnailContentType, 'image/jpeg') AS ContentType,
          m.PrimaryImageThumbnail AS Bytes
      FROM logistica.Material m
      WHERE m.Rfc = @Rfc
        AND m.Id = @MaterialId
        AND m.PrimaryImageThumbnail IS NOT NULL;
      """;

    using var conn = CreateConnection();
    var row = await conn.QueryFirstOrDefaultAsync<LogisticsBinaryContent>(
      new CommandDefinition(sql, new { Rfc = LogisticsRfc.Require(rfc), MaterialId = materialId }, cancellationToken: ct));

    if (row is null)
    {
      return null;
    }

    row.ContentType = LogisticsContentTypes.Normalize(row.ContentType, row.FileName, row.Bytes);
    return row;
  }

  public async Task<IReadOnlyList<LogisticsBinaryContent>> GetMaterialThumbnailsAsync(string rfc, IEnumerable<int> materialIds, CancellationToken ct = default)
  {
    var ids = materialIds?
      .Where(id => id > 0)
      .Distinct()
      .ToArray() ?? [];

    if (ids.Length == 0)
    {
      return Array.Empty<LogisticsBinaryContent>();
    }

    const string sql =
      """
      SELECT
          m.Id,
          COALESCE(NULLIF(m.PrimaryImageFileName, ''), CONCAT(m.MaterialCode, '-thumbnail.jpg')) AS FileName,
          COALESCE(m.PrimaryImageThumbnailContentType, 'image/jpeg') AS ContentType,
          m.PrimaryImageThumbnail AS Bytes
      FROM logistica.Material m
      WHERE m.Rfc = @Rfc
        AND m.Id IN @MaterialIds
        AND m.PrimaryImageThumbnail IS NOT NULL;
      """;

    using var conn = CreateConnection();
    var rows = (await conn.QueryAsync<LogisticsBinaryContent>(
      new CommandDefinition(sql, new { Rfc = LogisticsRfc.Require(rfc), MaterialIds = ids }, cancellationToken: ct))).AsList();

    foreach (var row in rows)
    {
      row.ContentType = LogisticsContentTypes.Normalize(row.ContentType, row.FileName, row.Bytes);
    }

    return rows;
  }

  public async Task<MaterialDeletionAssessmentDto> GetMaterialDeletionAssessmentAsync(
    string rfc,
    int materialId,
    CancellationToken ct = default)
  {
    if (materialId <= 0)
    {
      return new MaterialDeletionAssessmentDto();
    }

    using var conn = CreateConnection();
    return await LoadMaterialDeletionAssessmentAsync(
      conn,
      transaction: null,
      LogisticsRfc.Require(rfc),
      materialId,
      lockMaterial: false,
      ct);
  }

  public async Task<LogisticsCommandResult> DeleteMaterialAsync(MaterialDeleteRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);

    if (!string.Equals(request.ConfirmationText, DeleteConfirmationText, StringComparison.Ordinal))
    {
      return LogisticsCommandResult.Fail($"Escribe exactamente {DeleteConfirmationText} para confirmar la eliminación permanente.");
    }

    if (request.MaterialId <= 0)
    {
      return LogisticsCommandResult.Fail("Selecciona un material válido para eliminar.");
    }

    var rfc = LogisticsRfc.Require(request.Rfc);
    var deletedBy = NullIfWhiteSpace(request.DeletedBy) ?? "OrionERP";

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);

    try
    {
      var lockedMaterialId = await conn.QueryFirstOrDefaultAsync<int?>(
        new CommandDefinition(
          "SELECT Id FROM logistica.Material WITH (UPDLOCK, HOLDLOCK) WHERE Rfc = @Rfc AND Id = @MaterialId;",
          new { Rfc = rfc, MaterialId = request.MaterialId },
          tx,
          cancellationToken: ct));

      if (!lockedMaterialId.HasValue)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("El material ya no existe o no pertenece al RFC seleccionado.");
      }

      var assessment = await LoadMaterialDeletionAssessmentAsync(
        conn,
        tx,
        rfc,
        request.MaterialId,
        lockMaterial: false,
        ct);

      if (!assessment.Exists)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("El material ya no existe o no pertenece al RFC seleccionado.");
      }

      if (!assessment.CanDelete)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail(
          $"El material no se puede eliminar porque conserva {assessment.TotalReferences:N0} referencia(s) en {assessment.Blockers.Count:N0} grupo(s). Revisa el reporte actualizado.");
      }

      var affected = await conn.ExecuteAsync(
        new CommandDefinition(
          "DELETE FROM logistica.Material WHERE Rfc = @Rfc AND Id = @MaterialId;",
          new { Rfc = rfc, MaterialId = request.MaterialId },
          tx,
          cancellationToken: ct));

      if (affected != 1)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("El material cambió mientras se procesaba la solicitud. Vuelve a revisar el reporte.");
      }

      await tx.CommitAsync(ct);
      _logger?.LogInformation(
        "Material {MaterialCode} ({MaterialId}) deleted permanently for RFC {Rfc} by {DeletedBy}.",
        assessment.MaterialCode,
        assessment.MaterialId,
        rfc,
        deletedBy);

      return LogisticsCommandResult.Ok($"Material {assessment.MaterialCode} eliminado permanentemente.", assessment.MaterialId);
    }
    catch (SqlException ex) when (ex.Number == 547)
    {
      await tx.RollbackAsync(ct);
      return LogisticsCommandResult.Fail("Se creó o detectó una referencia nueva mientras se eliminaba el material. Revisa el reporte actualizado.");
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<LogisticsCommandResult> SaveMaterialAsync(MaterialUpsertRequest request, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    var rfc = LogisticsRfc.Require(request.Rfc);
    var description = request.Description?.Trim();
    if (string.IsNullOrWhiteSpace(description))
    {
      return LogisticsCommandResult.Fail("La descripción del material es obligatoria.");
    }

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      var materialId = request.Id ?? 0;
      var hasNewImage = request.PrimaryImageBytes is { Length: > 0 };
      var updateImage = hasNewImage || request.RemovePrimaryImage;
      var imageContentType = LogisticsContentTypes.Normalize(
        request.PrimaryImageContentType,
        request.PrimaryImageFileName,
        request.PrimaryImageBytes);
      var thumbnailContentType = hasNewImage && request.PrimaryImageThumbnailBytes is { Length: > 0 }
        ? LogisticsContentTypes.Normalize(
          request.PrimaryImageThumbnailContentType,
          fileName: null,
          bytes: request.PrimaryImageThumbnailBytes)
        : null;

      if (request.Id.HasValue && request.Id.Value > 0)
      {
        var sql = new StringBuilder(
          """
          UPDATE logistica.Material
          SET [Description] = @Description,
              BaseUnitId = @BaseUnitId,
              PurchaseQuantity = @PurchaseQuantity,
              PurchaseUnitId = @PurchaseUnitId,
              BusinessPartnerId = @BusinessPartnerId,
              Price = @Price,
              UpdatedDate = CONVERT(date, SYSUTCDATETIME()),
              Brand = @Brand,
              Model = @Model,
              IsPerishable = @IsPerishable,
              ShelfLifeDays = @ShelfLifeDays,
              RequiresRefrigeration = @RequiresRefrigeration,
              MaterialStatus = @Status,
              CategoryId = @CategoryId,
              Barcode = @Barcode,
              VendorCode = @VendorCode,
              PurchaseLink = @PurchaseLink,
              MaterialClass = @MaterialClass,
              IsActive = @IsActive
          """);

        if (updateImage)
        {
          sql.AppendLine(
            """
            , PrimaryImage = @PrimaryImage,
              PrimaryImageFileName = @PrimaryImageFileName,
              PrimaryImageContentType = @PrimaryImageContentType,
              PrimaryImageThumbnail = @PrimaryImageThumbnail,
              PrimaryImageThumbnailContentType = @PrimaryImageThumbnailContentType
            """);
        }

        sql.AppendLine();
        sql.AppendLine("WHERE Rfc = @Rfc AND Id = @Id;");

        var affected = await conn.ExecuteAsync(
          new CommandDefinition(
            sql.ToString(),
            new
            {
              Id = request.Id.Value,
              Rfc = rfc,
              Description = description,
              request.BaseUnitId,
              request.PurchaseQuantity,
              request.PurchaseUnitId,
              request.BusinessPartnerId,
              request.Price,
              Brand = NullIfWhiteSpace(request.Brand),
              Model = NullIfWhiteSpace(request.Model),
              request.IsPerishable,
              request.ShelfLifeDays,
              request.RequiresRefrigeration,
              request.Status,
              request.CategoryId,
              Barcode = NullIfWhiteSpace(request.Barcode),
              VendorCode = NullIfWhiteSpace(request.VendorCode),
              PurchaseLink = NullIfWhiteSpace(request.PurchaseLink),
              request.MaterialClass,
              request.IsActive,
              PrimaryImage = hasNewImage ? request.PrimaryImageBytes : null,
              PrimaryImageFileName = hasNewImage ? NullIfWhiteSpace(request.PrimaryImageFileName) : null,
              PrimaryImageContentType = hasNewImage ? imageContentType : null,
              PrimaryImageThumbnail = hasNewImage ? request.PrimaryImageThumbnailBytes : null,
              PrimaryImageThumbnailContentType = thumbnailContentType
            },
            tx,
            cancellationToken: ct));

        if (affected != 1)
        {
          await tx.RollbackAsync(ct);
          return LogisticsCommandResult.Fail("El material no pertenece al RFC seleccionado.");
        }

        materialId = request.Id.Value;
      }
      else
      {
        const string insertSql =
          """
          INSERT INTO logistica.Material
          (
              Rfc,
              MaterialCode,
              [Description],
              BaseUnitId,
              PurchaseQuantity,
              PurchaseUnitId,
              BusinessPartnerId,
              Price,
              CreatedDate,
              UpdatedDate,
              Brand,
              Model,
              IsPerishable,
              ShelfLifeDays,
              RequiresRefrigeration,
              MaterialStatus,
              CategoryId,
              Barcode,
              VendorCode,
              PrimaryImage,
              PrimaryImageFileName,
              PrimaryImageContentType,
              PrimaryImageThumbnail,
              PrimaryImageThumbnailContentType,
              PurchaseLink,
              MaterialClass,
              IsActive
          )
          VALUES
          (
              @Rfc,
              CONCAT('TMP-', LEFT(REPLACE(CONVERT(varchar(36), NEWID()), '-', ''), 16)),
              @Description,
              @BaseUnitId,
              @PurchaseQuantity,
              @PurchaseUnitId,
              @BusinessPartnerId,
              @Price,
              CONVERT(date, SYSUTCDATETIME()),
              CONVERT(date, SYSUTCDATETIME()),
              @Brand,
              @Model,
              @IsPerishable,
              @ShelfLifeDays,
              @RequiresRefrigeration,
              @Status,
              @CategoryId,
              @Barcode,
              @VendorCode,
              @PrimaryImage,
              @PrimaryImageFileName,
              @PrimaryImageContentType,
              @PrimaryImageThumbnail,
              @PrimaryImageThumbnailContentType,
              @PurchaseLink,
              @MaterialClass,
              @IsActive
          );

          SELECT CAST(SCOPE_IDENTITY() AS int);
          """;

        materialId = await conn.ExecuteScalarAsync<int>(
          new CommandDefinition(
            insertSql,
            new
            {
              Description = description,
              Rfc = rfc,
              request.BaseUnitId,
              request.PurchaseQuantity,
              request.PurchaseUnitId,
              request.BusinessPartnerId,
              request.Price,
              Brand = NullIfWhiteSpace(request.Brand),
              Model = NullIfWhiteSpace(request.Model),
              request.IsPerishable,
              request.ShelfLifeDays,
              request.RequiresRefrigeration,
              request.Status,
              request.CategoryId,
              Barcode = NullIfWhiteSpace(request.Barcode),
              VendorCode = NullIfWhiteSpace(request.VendorCode),
              PrimaryImage = request.PrimaryImageBytes,
              PrimaryImageFileName = NullIfWhiteSpace(request.PrimaryImageFileName),
              PrimaryImageContentType = hasNewImage ? imageContentType : null,
              PrimaryImageThumbnail = hasNewImage ? request.PrimaryImageThumbnailBytes : null,
              PrimaryImageThumbnailContentType = thumbnailContentType,
              PurchaseLink = NullIfWhiteSpace(request.PurchaseLink),
              request.MaterialClass,
              request.IsActive
            },
            tx,
            cancellationToken: ct));

        await conn.ExecuteAsync(
          new CommandDefinition(
            """
            UPDATE logistica.Material
            SET MaterialCode = CONCAT('MAT-', RIGHT(REPLICATE('0', 6) + CAST(@MaterialId AS varchar(20)), 6))
            WHERE Rfc = @Rfc AND Id = @MaterialId;
            """,
            new { Rfc = rfc, MaterialId = materialId },
            tx,
            cancellationToken: ct));
      }

      await tx.CommitAsync(ct);
      return LogisticsCommandResult.Ok($"Material {description} guardado correctamente.", materialId);
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
      await tx.RollbackAsync(ct);
      return LogisticsCommandResult.Fail("Ya existe un material con la misma clave interna, código de barras o relación heredada.");
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<LogisticsCommandResult> CreateCategoryAsync(MaterialCategoryCreateRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);

    var rfc = LogisticsRfc.Require(request.Rfc);
    var name = request.Name?.Trim();
    if (string.IsNullOrWhiteSpace(name))
    {
      return LogisticsCommandResult.Fail("Escribe el nombre de la categoría.");
    }

    const string sql =
      """
      DECLARE @ExistingId int =
      (
          SELECT TOP (1) Id
          FROM logistica.MaterialCategory
          WHERE Rfc = @Rfc
            AND CategoryName = @Name
      );

      IF @ExistingId IS NOT NULL
      BEGIN
          UPDATE logistica.MaterialCategory
          SET IsActive = 1,
              [Description] = COALESCE(@Description, [Description])
          WHERE Rfc = @Rfc
            AND Id = @ExistingId;

          SELECT @ExistingId;
          RETURN;
      END;

      INSERT INTO logistica.MaterialCategory (Rfc, CategoryName, [Description], IsActive)
      VALUES (@Rfc, @Name, @Description, 1);

      SELECT CAST(SCOPE_IDENTITY() AS int);
      """;

    try
    {
      using var conn = CreateConnection();
      var categoryId = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(
          sql,
          new { Rfc = rfc, Name = name, Description = NullIfWhiteSpace(request.Description) },
          cancellationToken: ct));

      return LogisticsCommandResult.Ok($"Categoría {name} lista para usar.", categoryId);
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
      return LogisticsCommandResult.Fail("Ya existe una categoría con ese nombre.");
    }
  }

  public async Task<LogisticsCommandResult> CreateUnitAsync(UnitOfMeasureCreateRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);

    var name = request.Name?.Trim();
    if (string.IsNullOrWhiteSpace(name))
    {
      return LogisticsCommandResult.Fail("Escribe el nombre de la unidad.");
    }

    const string sql =
      """
      DECLARE @ExistingId int =
      (
          SELECT TOP (1) Id
          FROM logistica.UnitOfMeasure
          WHERE UnitName = @Name
      );

      IF @ExistingId IS NOT NULL
      BEGIN
          UPDATE logistica.UnitOfMeasure
          SET IsActive = 1,
              Abbreviation = COALESCE(@Abbreviation, Abbreviation),
              [Description] = COALESCE(@Description, [Description])
          WHERE Id = @ExistingId;

          SELECT @ExistingId;
          RETURN;
      END;

      INSERT INTO logistica.UnitOfMeasure (UnitName, Abbreviation, [Description], IsActive)
      VALUES (@Name, @Abbreviation, @Description, 1);

      SELECT CAST(SCOPE_IDENTITY() AS int);
      """;

    try
    {
      using var conn = CreateConnection();
      var unitId = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(
          sql,
          new
          {
            Name = name,
            Abbreviation = NullIfWhiteSpace(request.Abbreviation),
            Description = NullIfWhiteSpace(request.Description)
          },
          cancellationToken: ct));

      return LogisticsCommandResult.Ok($"Unidad {name} lista para usar.", unitId);
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
      return LogisticsCommandResult.Fail("Ya existe una unidad con ese nombre.");
    }
  }

  private async Task<MaterialDeletionAssessmentDto> LoadMaterialDeletionAssessmentAsync(
    DbConnection connection,
    DbTransaction? transaction,
    string rfc,
    int materialId,
    bool lockMaterial,
    CancellationToken ct)
  {
    var sql = MaterialDeletionAssessmentSql.Replace(
      "/*MATERIAL_LOCK*/",
      lockMaterial ? "WITH (UPDLOCK, HOLDLOCK)" : string.Empty,
      StringComparison.Ordinal);

    var rows = (await connection.QueryAsync<MaterialDeletionAssessmentRow>(
      new CommandDefinition(
        sql,
        new { Rfc = rfc, MaterialId = materialId, ExampleLimit = DeletionExampleLimit },
        transaction,
        cancellationToken: ct))).AsList();

    if (rows.Count == 0)
    {
      return new MaterialDeletionAssessmentDto();
    }

    var material = rows[0];
    var blockers = rows
      .Where(row => !string.IsNullOrWhiteSpace(row.BlockerCode))
      .GroupBy(row => row.BlockerCode!, StringComparer.Ordinal)
      .OrderBy(group => group.Min(row => row.BlockerSortOrder))
      .Select(group =>
      {
        var definition = DeletionBlockerDefinitions[group.Key];
        return new MaterialDeletionBlockerDto
        {
          Code = definition.Code,
          Title = definition.Title,
          Explanation = definition.Explanation,
          ReferenceCount = group.Max(row => row.ReferenceCount),
          Examples = group
            .Select(row => row.Example)
            .Where(example => !string.IsNullOrWhiteSpace(example))
            .Select(example => example!)
            .Distinct(StringComparer.Ordinal)
            .Take(DeletionExampleLimit)
            .ToArray(),
          ResolutionLabel = definition.ResolutionLabel,
          ResolutionUrl = definition.ResolutionUrl
        };
      })
      .ToArray();

    return new MaterialDeletionAssessmentDto
    {
      Exists = true,
      MaterialId = material.MaterialId,
      MaterialCode = material.MaterialCode,
      Description = material.Description,
      Blockers = blockers
    };
  }

  private const string MaterialDeletionAssessmentSql =
    """
    ;WITH DependencyRows AS
    (
      SELECT
        N'StockBalance' AS BlockerCode,
        10 AS BlockerSortOrder,
        CONVERT(nvarchar(100), balance.Id) AS ReferenceKey,
        balance.UpdatedAt AS SortDate,
        CAST(CONCAT(
          COALESCE(NULLIF(locationInfo.LocationCode, ''), CONCAT('Ubicación #', balance.LocationId)),
          CASE WHEN NULLIF(locationInfo.LocationName, '') IS NULL THEN '' ELSE CONCAT(' · ', locationInfo.LocationName) END,
          ' · Existencia: ', CONVERT(varchar(40), CAST(balance.Quantity AS decimal(18,4))),
          ' · Reservada: ', CONVERT(varchar(40), CAST(ISNULL(balance.ReservedQuantity, 0) AS decimal(18,4))),
          CASE WHEN ISNULL(balance.IsRemoved, 0) = 1 THEN ' · Retirado' ELSE ' · Activo' END
        ) AS nvarchar(1000)) AS Example
      FROM logistica.StockBalance balance
      LEFT JOIN logistica.Location locationInfo
        ON locationInfo.Rfc = balance.Rfc AND locationInfo.Id = balance.LocationId
      WHERE balance.Rfc = @Rfc AND balance.MaterialId = @MaterialId

      UNION ALL

      SELECT
        N'StockTransaction', 20, CONVERT(nvarchar(100), movement.Id), movement.OccurredAt,
        CAST(CONCAT(
          'Movimiento #', movement.Id, ' · ', movement.TransactionType,
          ' · Cambio: ', CONVERT(varchar(40), CAST(movement.QuantityDelta AS decimal(18,4))),
          ' · Saldo: ', CONVERT(varchar(40), CAST(movement.QuantityAfter AS decimal(18,4))),
          CASE WHEN NULLIF(locationInfo.LocationName, '') IS NULL THEN '' ELSE CONCAT(' · ', locationInfo.LocationName) END,
          ' · ', CONVERT(varchar(19), movement.OccurredAt, 120)
        ) AS nvarchar(1000))
      FROM logistica.StockTransaction movement
      LEFT JOIN logistica.Location locationInfo
        ON locationInfo.Rfc = movement.Rfc AND locationInfo.Id = movement.LocationId
      WHERE movement.Rfc = @Rfc AND movement.MaterialId = @MaterialId

      UNION ALL

      SELECT
        N'LocationMaterialAttachment', 30, CONVERT(nvarchar(100), attachmentInfo.Id), attachmentInfo.CreatedAt,
        CAST(CONCAT(
          COALESCE(NULLIF(attachmentInfo.FileName, ''), CONCAT('Adjunto #', attachmentInfo.Id)),
          CASE WHEN NULLIF(locationInfo.LocationName, '') IS NULL THEN '' ELSE CONCAT(' · ', locationInfo.LocationName) END,
          CASE WHEN ISNULL(attachmentInfo.IsDeleted, 0) = 1 THEN ' · Archivado' ELSE ' · Activo' END
        ) AS nvarchar(1000))
      FROM logistica.LocationMaterialAttachment attachmentInfo
      LEFT JOIN logistica.Location locationInfo
        ON locationInfo.Rfc = attachmentInfo.Rfc AND locationInfo.Id = attachmentInfo.LocationId
      WHERE attachmentInfo.Rfc = @Rfc AND attachmentInfo.MaterialId = @MaterialId

      UNION ALL

      SELECT
        N'PhysicalCountLine', 40, CONVERT(nvarchar(100), countLine.Id), countLine.CapturedAt,
        CAST(CONCAT(
          COALESCE(NULLIF(countSession.SessionCode, ''), CONCAT('Conteo #', countLine.SessionId)),
          CASE WHEN NULLIF(countSession.Status, '') IS NULL THEN '' ELSE CONCAT(' · ', countSession.Status) END,
          CASE WHEN NULLIF(locationInfo.LocationName, '') IS NULL THEN '' ELSE CONCAT(' · ', locationInfo.LocationName) END,
          ' · Esperado: ', CONVERT(varchar(40), CAST(countLine.ExpectedQuantity AS decimal(18,4))),
          ' · Contado: ', COALESCE(CONVERT(varchar(40), CAST(countLine.CountedQuantity AS decimal(18,4))), 'Pendiente')
        ) AS nvarchar(1000))
      FROM logistica.PhysicalCountLine countLine
      JOIN logistica.PhysicalCountSession countSession
        ON countSession.Rfc = countLine.Rfc AND countSession.Id = countLine.SessionId
      LEFT JOIN logistica.Location locationInfo
        ON locationInfo.Rfc = countLine.Rfc AND locationInfo.Id = countLine.LocationId
      WHERE countLine.Rfc = @Rfc AND countLine.MaterialId = @MaterialId

      UNION ALL

      SELECT
        N'MaterialLot', 50, CONVERT(nvarchar(100), materialLot.Id), materialLot.CreatedAt,
        CAST(CONCAT(
          'Lote ', materialLot.LotCode,
          CASE WHEN materialLot.ExpiresAt IS NULL THEN '' ELSE CONCAT(' · Vence: ', CONVERT(varchar(10), materialLot.ExpiresAt, 23)) END,
          CASE WHEN materialLot.IsBlocked = 1 THEN ' · Bloqueado' ELSE ' · Disponible' END
        ) AS nvarchar(1000))
      FROM logistica.MaterialLot materialLot
      WHERE materialLot.Rfc = @Rfc AND materialLot.MaterialId = @MaterialId

      UNION ALL

      SELECT
        N'LotBalance', 60, CONVERT(nvarchar(100), lotBalance.Id), lotBalance.UpdatedAt,
        CAST(CONCAT(
          COALESCE(CONCAT('Lote ', materialLot.LotCode), CONCAT('Saldo de lote #', lotBalance.Id)),
          CASE WHEN NULLIF(locationInfo.LocationName, '') IS NULL THEN '' ELSE CONCAT(' · ', locationInfo.LocationName) END,
          ' · Existencia: ', CONVERT(varchar(40), CAST(lotBalance.Quantity AS decimal(18,4))),
          ' · Reservada: ', CONVERT(varchar(40), CAST(lotBalance.ReservedQuantity AS decimal(18,4)))
        ) AS nvarchar(1000))
      FROM logistica.LotBalance lotBalance
      LEFT JOIN logistica.MaterialLot materialLot
        ON materialLot.Rfc = lotBalance.Rfc AND materialLot.Id = lotBalance.MaterialLotId
      LEFT JOIN logistica.Location locationInfo
        ON locationInfo.Rfc = lotBalance.Rfc AND locationInfo.Id = lotBalance.LocationId
      WHERE lotBalance.Rfc = @Rfc AND lotBalance.MaterialId = @MaterialId

      UNION ALL

      SELECT
        N'InventoryReservationLine', 70, CONVERT(nvarchar(100), reservationLine.Id), reservationInfo.CreatedAt,
        CAST(CONCAT(
          'Reserva #', reservationInfo.Id,
          CASE WHEN NULLIF(reservationInfo.ReferenceType, '') IS NULL THEN '' ELSE CONCAT(' · ', reservationInfo.ReferenceType, ' ', reservationInfo.ReferenceId) END,
          ' · ', reservationInfo.Status,
          CASE WHEN NULLIF(locationInfo.LocationName, '') IS NULL THEN '' ELSE CONCAT(' · ', locationInfo.LocationName) END,
          ' · Requerida: ', CONVERT(varchar(40), CAST(reservationLine.RequiredQuantity AS decimal(18,4))),
          ' · Reservada: ', CONVERT(varchar(40), CAST(reservationLine.ReservedQuantity AS decimal(18,4))),
          ' · Consumida: ', CONVERT(varchar(40), CAST(reservationLine.ConsumedQuantity AS decimal(18,4)))
        ) AS nvarchar(1000))
      FROM logistica.InventoryReservationLine reservationLine
      JOIN logistica.InventoryReservation reservationInfo
        ON reservationInfo.Rfc = reservationLine.Rfc AND reservationInfo.Id = reservationLine.ReservationId
      LEFT JOIN logistica.Location locationInfo
        ON locationInfo.Rfc = reservationLine.Rfc AND locationInfo.Id = reservationLine.LocationId
      WHERE reservationLine.Rfc = @Rfc AND reservationLine.MaterialId = @MaterialId

      UNION ALL

      SELECT
        N'InventoryTransferLine', 80, CONVERT(nvarchar(100), transferLine.Id), transferInfo.CreatedAt,
        CAST(CONCAT(
          COALESCE(NULLIF(transferInfo.TransferCode, ''), CONCAT('Transferencia #', transferInfo.Id)),
          ' · ', transferInfo.Status,
          ' · ', COALESCE(NULLIF(fromLocation.LocationName, ''), CONCAT('Ubicación #', transferInfo.FromLocationId)),
          ' → ', COALESCE(NULLIF(toLocation.LocationName, ''), CONCAT('Ubicación #', transferInfo.ToLocationId)),
          ' · Cantidad: ', CONVERT(varchar(40), CAST(transferLine.Quantity AS decimal(18,4)))
        ) AS nvarchar(1000))
      FROM logistica.InventoryTransferLine transferLine
      JOIN logistica.InventoryTransfer transferInfo
        ON transferInfo.Rfc = transferLine.Rfc AND transferInfo.Id = transferLine.TransferId
      LEFT JOIN logistica.Location fromLocation
        ON fromLocation.Rfc = transferInfo.Rfc AND fromLocation.Id = transferInfo.FromLocationId
      LEFT JOIN logistica.Location toLocation
        ON toLocation.Rfc = transferInfo.Rfc AND toLocation.Id = transferInfo.ToLocationId
      WHERE transferLine.Rfc = @Rfc AND transferLine.MaterialId = @MaterialId

      UNION ALL

      SELECT
        N'InventoryAdjustmentLine', 90, CONVERT(nvarchar(100), adjustmentLine.Id), adjustmentInfo.CreatedAt,
        CAST(CONCAT(
          COALESCE(NULLIF(adjustmentInfo.AdjustmentCode, ''), CONCAT('Ajuste #', adjustmentInfo.Id)),
          ' · ', adjustmentInfo.Status,
          CASE WHEN NULLIF(adjustmentInfo.AdjustmentType, '') IS NULL THEN '' ELSE CONCAT(' · ', adjustmentInfo.AdjustmentType) END,
          CASE WHEN NULLIF(locationInfo.LocationName, '') IS NULL THEN '' ELSE CONCAT(' · ', locationInfo.LocationName) END,
          ' · Cambio: ', CONVERT(varchar(40), CAST(adjustmentLine.QuantityDelta AS decimal(18,4)))
        ) AS nvarchar(1000))
      FROM logistica.InventoryAdjustmentLine adjustmentLine
      JOIN logistica.InventoryAdjustment adjustmentInfo
        ON adjustmentInfo.Rfc = adjustmentLine.Rfc AND adjustmentInfo.Id = adjustmentLine.AdjustmentId
      LEFT JOIN logistica.Location locationInfo
        ON locationInfo.Rfc = adjustmentLine.Rfc AND locationInfo.Id = adjustmentLine.LocationId
      WHERE adjustmentLine.Rfc = @Rfc AND adjustmentLine.MaterialId = @MaterialId

      UNION ALL

      SELECT
        N'PurchaseOrderLine', 100, CONVERT(nvarchar(100), purchaseLine.Id), purchaseOrder.UpdatedAt,
        CAST(CONCAT(
          COALESCE(NULLIF(purchaseOrder.PurchaseOrderCode, ''), CONCAT('Orden #', purchaseOrder.Id)),
          ' · ', purchaseOrder.Status,
          ' · Pedido: ', CONVERT(varchar(40), CAST(purchaseLine.OrderedQuantity AS decimal(18,4))),
          ' · Recibido: ', CONVERT(varchar(40), CAST(purchaseLine.ReceivedQuantity AS decimal(18,4))),
          ' · ', CONVERT(varchar(10), purchaseOrder.OrderDate, 23)
        ) AS nvarchar(1000))
      FROM logistica.PurchaseOrderLine purchaseLine
      JOIN logistica.PurchaseOrder purchaseOrder
        ON purchaseOrder.Rfc = purchaseLine.Rfc AND purchaseOrder.Id = purchaseLine.PurchaseOrderId
      WHERE purchaseLine.Rfc = @Rfc AND purchaseLine.MaterialId = @MaterialId

      UNION ALL

      SELECT
        N'PurchaseReceiptLine', 110, CONVERT(nvarchar(100), receiptLine.Id), receiptLine.CreatedAt,
        CAST(CONCAT(
          COALESCE(NULLIF(receiptInfo.ReceiptCode, ''), CONCAT('Recepción #', receiptInfo.Id)),
          CASE WHEN NULLIF(purchaseOrder.PurchaseOrderCode, '') IS NULL THEN '' ELSE CONCAT(' · ', purchaseOrder.PurchaseOrderCode) END,
          CASE WHEN NULLIF(locationInfo.LocationName, '') IS NULL THEN '' ELSE CONCAT(' · ', locationInfo.LocationName) END,
          ' · Cantidad: ', CONVERT(varchar(40), CAST(receiptLine.Quantity AS decimal(18,4))),
          ' · ', CONVERT(varchar(10), receiptInfo.ReceiptDate, 23)
        ) AS nvarchar(1000))
      FROM logistica.PurchaseReceiptLine receiptLine
      JOIN logistica.PurchaseReceipt receiptInfo
        ON receiptInfo.Rfc = receiptLine.Rfc AND receiptInfo.Id = receiptLine.PurchaseReceiptId
      JOIN logistica.PurchaseOrder purchaseOrder
        ON purchaseOrder.Rfc = receiptInfo.Rfc AND purchaseOrder.Id = receiptInfo.PurchaseOrderId
      LEFT JOIN logistica.Location locationInfo
        ON locationInfo.Rfc = receiptLine.Rfc AND locationInfo.Id = receiptLine.LocationId
      WHERE receiptLine.Rfc = @Rfc AND receiptLine.MaterialId = @MaterialId

      UNION ALL

      SELECT
        N'BomHeader', 120, CONVERT(nvarchar(100), bomHeader.Id), bomHeader.CreatedAt,
        CAST(CONCAT(
          COALESCE(NULLIF(bomHeader.BomCode, ''), CONCAT('BOM #', bomHeader.Id)),
          CASE WHEN NULLIF(bomHeader.Name, '') IS NULL THEN '' ELSE CONCAT(' · ', bomHeader.Name) END,
          CASE WHEN bomHeader.IsActive = 1 THEN ' · Activo' ELSE ' · Inactivo' END,
          CASE WHEN NULLIF(versionInfo.VersionSummary, '') IS NULL THEN '' ELSE CONCAT(' · ', versionInfo.VersionSummary) END
        ) AS nvarchar(1000))
      FROM logistica.BomHeader bomHeader
      OUTER APPLY
      (
        SELECT TOP (1)
          CONCAT(
            'Versión ', bomVersion.VersionNumber, ' (', bomVersion.Status, ')',
            CASE WHEN NULLIF(recipeInfo.Name, '') IS NULL THEN '' ELSE CONCAT(' · Receta: ', recipeInfo.Name) END
          ) AS VersionSummary
        FROM logistica.BomVersion bomVersion
        LEFT JOIN logistica.Recipe recipeInfo
          ON recipeInfo.Rfc = bomVersion.Rfc AND recipeInfo.BomVersionId = bomVersion.Id
        WHERE bomVersion.Rfc = bomHeader.Rfc AND bomVersion.BomHeaderId = bomHeader.Id
        ORDER BY CASE WHEN bomVersion.Status = 'Active' THEN 0 ELSE 1 END, bomVersion.VersionNumber DESC, recipeInfo.Id
      ) versionInfo
      WHERE bomHeader.Rfc = @Rfc AND bomHeader.ProductMaterialId = @MaterialId

      UNION ALL

      SELECT
        N'BomComponent', 130, CONVERT(nvarchar(100), bomComponent.Id), bomVersion.CreatedAt,
        CAST(CONCAT(
          COALESCE(NULLIF(bomHeader.BomCode, ''), CONCAT('BOM #', bomHeader.Id)),
          ' · Versión ', bomVersion.VersionNumber, ' (', bomVersion.Status, ')',
          CASE WHEN NULLIF(recipeInfo.Name, '') IS NULL THEN '' ELSE CONCAT(' · Receta: ', recipeInfo.Name) END,
          ' · Cantidad: ', CONVERT(varchar(40), CAST(bomComponent.Quantity AS decimal(18,4))),
          CASE WHEN NULLIF(unitInfo.Abbreviation, '') IS NULL THEN '' ELSE CONCAT(' ', unitInfo.Abbreviation) END
        ) AS nvarchar(1000))
      FROM logistica.BomComponent bomComponent
      JOIN logistica.BomVersion bomVersion
        ON bomVersion.Rfc = bomComponent.Rfc AND bomVersion.Id = bomComponent.BomVersionId
      JOIN logistica.BomHeader bomHeader
        ON bomHeader.Rfc = bomVersion.Rfc AND bomHeader.Id = bomVersion.BomHeaderId
      LEFT JOIN logistica.UnitOfMeasure unitInfo
        ON unitInfo.Id = bomComponent.UnitId
      OUTER APPLY
      (
        SELECT TOP (1) recipe.Name
        FROM logistica.Recipe recipe
        WHERE recipe.Rfc = bomVersion.Rfc AND recipe.BomVersionId = bomVersion.Id
        ORDER BY recipe.IsActive DESC, recipe.Id
      ) recipeInfo
      WHERE bomComponent.Rfc = @Rfc AND bomComponent.ComponentMaterialId = @MaterialId

      UNION ALL

      SELECT
        N'ProductionOrder', 140, CONVERT(nvarchar(100), productionOrder.Id), productionOrder.PlannedAt,
        CAST(CONCAT(
          COALESCE(NULLIF(productionOrder.ProductionCode, ''), CONCAT('Producción ', productionOrder.Id)),
          ' · ', productionOrder.Status,
          ' · Planeado: ', CONVERT(varchar(40), CAST(productionOrder.PlannedQuantity AS decimal(18,4))),
          ' · Real: ', COALESCE(CONVERT(varchar(40), CAST(productionOrder.ActualQuantity AS decimal(18,4))), 'Pendiente')
        ) AS nvarchar(1000))
      FROM logistica.ProductionOrder productionOrder
      WHERE productionOrder.Rfc = @Rfc AND productionOrder.ProductMaterialId = @MaterialId

      UNION ALL

      SELECT
        N'RestaurantProduct', 150, CONVERT(nvarchar(100), restaurantProduct.Id), CAST(NULL AS datetime2),
        CAST(CONCAT(
          productCard.Name,
          CASE WHEN NULLIF(restaurantProduct.VariantName, '') IS NULL THEN '' ELSE CONCAT(' · ', restaurantProduct.VariantName) END,
          ' · SKU ', restaurantProduct.Sku,
          CASE WHEN restaurantProduct.IsActive = 1 THEN ' · Activo' ELSE ' · Inactivo' END
        ) AS nvarchar(1000))
      FROM restaurante.Product restaurantProduct
      JOIN restaurante.ProductCard productCard
        ON productCard.Rfc = restaurantProduct.Rfc AND productCard.Id = restaurantProduct.ProductCardId
      WHERE restaurantProduct.Rfc = @Rfc AND restaurantProduct.MaterialId = @MaterialId

      UNION ALL

      SELECT
        N'ModifierIngredientDelta', 160, CONVERT(nvarchar(100), ingredientDelta.Id), CAST(NULL AS datetime2),
        CAST(CONCAT(
          modifierGroup.Name, ' · ', modifierOption.Name,
          ' · Cambio: ', CONVERT(varchar(40), CAST(ingredientDelta.QuantityDelta AS decimal(18,4))),
          CASE WHEN NULLIF(unitInfo.Abbreviation, '') IS NULL THEN '' ELSE CONCAT(' ', unitInfo.Abbreviation) END,
          CASE WHEN modifierOption.IsActive = 1 AND modifierGroup.IsActive = 1 THEN ' · Activo' ELSE ' · Inactivo' END
        ) AS nvarchar(1000))
      FROM restaurante.ModifierIngredientDelta ingredientDelta
      JOIN restaurante.ModifierOption modifierOption
        ON modifierOption.Rfc = ingredientDelta.Rfc AND modifierOption.Id = ingredientDelta.ModifierOptionId
      JOIN restaurante.ModifierGroup modifierGroup
        ON modifierGroup.Rfc = modifierOption.Rfc AND modifierGroup.Id = modifierOption.ModifierGroupId
      LEFT JOIN logistica.UnitOfMeasure unitInfo
        ON unitInfo.Id = ingredientDelta.UnitId
      WHERE ingredientDelta.Rfc = @Rfc AND ingredientDelta.MaterialId = @MaterialId

      UNION ALL

      SELECT
        N'MaterialAllergen', 170, CONVERT(nvarchar(100), allergenAssignment.AllergenId), CAST(NULL AS datetime2),
        CAST(CONCAT(
          allergenInfo.Name, ' · ', allergenInfo.Code,
          CASE WHEN allergenInfo.IsActive = 1 THEN ' · Activo' ELSE ' · Inactivo' END
        ) AS nvarchar(1000))
      FROM logistica.MaterialAllergen allergenAssignment
      JOIN logistica.Allergen allergenInfo
        ON allergenInfo.Id = allergenAssignment.AllergenId
      WHERE allergenAssignment.Rfc = @Rfc AND allergenAssignment.MaterialId = @MaterialId

      UNION ALL

      SELECT
        N'MaterialUnitConversion', 180, CONVERT(nvarchar(100), conversionInfo.Id), CAST(NULL AS datetime2),
        CAST(CONCAT(
          COALESCE(NULLIF(fromUnit.Abbreviation, ''), fromUnit.UnitName),
          ' → ', COALESCE(NULLIF(toUnit.Abbreviation, ''), toUnit.UnitName),
          ' · Factor: ', CONVERT(varchar(40), CAST(conversionInfo.Factor AS decimal(18,6))),
          CASE WHEN conversionInfo.IsActive = 1 THEN ' · Activa' ELSE ' · Inactiva' END
        ) AS nvarchar(1000))
      FROM logistica.MaterialUnitConversion conversionInfo
      JOIN logistica.UnitOfMeasure fromUnit ON fromUnit.Id = conversionInfo.FromUnitId
      JOIN logistica.UnitOfMeasure toUnit ON toUnit.Id = conversionInfo.ToUnitId
      WHERE conversionInfo.Rfc = @Rfc AND conversionInfo.MaterialId = @MaterialId
    ),
    RankedDependencies AS
    (
      SELECT
        BlockerCode,
        BlockerSortOrder,
        Example,
        COUNT_BIG(*) OVER (PARTITION BY BlockerCode) AS ReferenceCount,
        ROW_NUMBER() OVER
        (
          PARTITION BY BlockerCode
          ORDER BY CASE WHEN SortDate IS NULL THEN 1 ELSE 0 END, SortDate DESC, ReferenceKey DESC
        ) AS ExampleOrdinal
      FROM DependencyRows
    )
    SELECT
      material.Id AS MaterialId,
      material.MaterialCode,
      material.[Description],
      dependency.BlockerCode,
      dependency.BlockerSortOrder,
      dependency.ReferenceCount,
      dependency.Example
    FROM logistica.Material material /*MATERIAL_LOCK*/
    LEFT JOIN RankedDependencies dependency
      ON dependency.ExampleOrdinal <= @ExampleLimit
    WHERE material.Rfc = @Rfc
      AND material.Id = @MaterialId
    ORDER BY dependency.BlockerSortOrder, dependency.ExampleOrdinal;
    """;

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica de conexiones no devolvió una DbConnection.");

  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private sealed record MaterialDeletionBlockerDefinition(
    string Code,
    string Title,
    string Explanation,
    string? ResolutionLabel,
    string? ResolutionUrl);

  private sealed class MaterialDeletionAssessmentRow
  {
    public int MaterialId { get; set; }
    public string MaterialCode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? BlockerCode { get; set; }
    public int BlockerSortOrder { get; set; }
    public long ReferenceCount { get; set; }
    public string? Example { get; set; }
  }
}
