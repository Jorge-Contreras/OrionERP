using System.Data;
using System.Data.Common;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Infrastructure.Features.Logistica.Support;

namespace OrionERP.Infrastructure.Features.Logistica.Materials;

public sealed class MaterialService : IMaterialService
{
  private static readonly string[] DefaultClasses = ["Consumable", "Reusable", "Installed", "AssetLike"];
  private static readonly string[] DefaultStatuses = ["ACTIVO", "OBSOLETO", "INACTIVO"];

  private readonly IDbConnectionFactory _connectionFactory;

  public MaterialService(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
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

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica de conexiones no devolvió una DbConnection.");

  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
