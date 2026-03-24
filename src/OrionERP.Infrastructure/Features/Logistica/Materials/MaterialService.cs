using System.Data;
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

    var sql = new StringBuilder(
      """
      WITH StockTotals AS (
          SELECT
              sb.MaterialId,
              SUM(sb.Quantity) AS TotalQuantity,
              COUNT(*) AS LocationCount
          FROM logistica.StockBalance sb
          GROUP BY sb.MaterialId
      )
      SELECT
          m.Id,
          m.MaterialCode,
          m.[Description],
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
        ON mc.Id = m.CategoryId
      LEFT JOIN logistica.UnitOfMeasure u
        ON u.Id = m.BaseUnitId
      LEFT JOIN dbo.BusinessPartner bp
        ON bp.Id = m.BusinessPartnerId
      LEFT JOIN StockTotals st
        ON st.MaterialId = m.Id
      WHERE 1 = 1
      """);

    var parameters = new DynamicParameters();

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

    sql.AppendLine("ORDER BY m.MaterialCode, m.[Description], m.Id;");

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<MaterialListItemDto>(
      new CommandDefinition(sql.ToString(), parameters, cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<MaterialDetailDto?> GetMaterialAsync(int materialId, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          m.Id,
          m.MaterialCode,
          m.LegacyMaterialId,
          m.[Description],
          m.BaseUnitId,
          CAST(m.PurchaseQuantity AS decimal(18,4)) AS PurchaseQuantity,
          m.PurchaseUnitId,
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
      WHERE m.Id = @MaterialId;
      """;

    using var conn = CreateConnection();
    return await conn.QueryFirstOrDefaultAsync<MaterialDetailDto>(
      new CommandDefinition(sql, new { MaterialId = materialId }, cancellationToken: ct));
  }

  public async Task<MaterialCatalogDto> GetCatalogAsync(CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT Id, CategoryName AS Name, CategoryName AS Code
      FROM logistica.MaterialCategory
      WHERE IsActive = 1
      ORDER BY CategoryName, Id;

      SELECT Id, UnitName AS Name, Abbreviation AS Code
      FROM logistica.UnitOfMeasure
      WHERE IsActive = 1
      ORDER BY UnitName, Id;

      SELECT
          bp.Id,
          bp.PartnerName AS Name,
          bp.Rfc AS Code
      FROM dbo.BusinessPartner bp
      WHERE bp.IsActive = 1
        AND (
            EXISTS (SELECT 1 FROM dbo.BusinessPartnerRole r WHERE r.BusinessPartnerId = bp.Id AND r.RoleCode = 'Vendor')
            OR EXISTS (SELECT 1 FROM logistica.VendorProfile vp WHERE vp.BusinessPartnerId = bp.Id)
        )
      ORDER BY bp.PartnerName, bp.Id;
      """;

    using var conn = CreateConnection();
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, cancellationToken: ct));

    return new MaterialCatalogDto
    {
      Categories = (await multi.ReadAsync<LookupOptionDto>()).AsList(),
      Units = (await multi.ReadAsync<LookupOptionDto>()).AsList(),
      Vendors = (await multi.ReadAsync<LookupOptionDto>()).AsList(),
      MaterialClasses = DefaultClasses,
      Statuses = DefaultStatuses
    };
  }

  public async Task<LogisticsBinaryContent?> GetMaterialImageAsync(int materialId, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          m.Id,
          COALESCE(m.PrimaryImageFileName, CONCAT(m.MaterialCode, '.bin')) AS FileName,
          m.PrimaryImageContentType AS ContentType,
          m.PrimaryImage AS Bytes
      FROM logistica.Material m
      WHERE m.Id = @MaterialId
        AND m.PrimaryImage IS NOT NULL;
      """;

    using var conn = CreateConnection();
    var row = await conn.QueryFirstOrDefaultAsync<LogisticsBinaryContent>(
      new CommandDefinition(sql, new { MaterialId = materialId }, cancellationToken: ct));

    if (row is null)
    {
      return null;
    }

    row.ContentType = LogisticsContentTypes.Normalize(row.ContentType, row.FileName, row.Bytes);
    return row;
  }

  public async Task<LogisticsCommandResult> SaveMaterialAsync(MaterialUpsertRequest request, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    var description = request.Description?.Trim();
    if (string.IsNullOrWhiteSpace(description))
    {
      return LogisticsCommandResult.Fail("La descripción del material es obligatoria.");
    }

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

    try
    {
      var materialId = request.Id ?? 0;
      var hasNewImage = request.PrimaryImageBytes is { Length: > 0 };
      var imageContentType = LogisticsContentTypes.Normalize(
        request.PrimaryImageContentType,
        request.PrimaryImageFileName,
        request.PrimaryImageBytes);

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

        if (hasNewImage)
        {
          sql.AppendLine(
            """
            , PrimaryImage = @PrimaryImage,
              PrimaryImageFileName = @PrimaryImageFileName,
              PrimaryImageContentType = @PrimaryImageContentType
            """);
        }

        sql.AppendLine("WHERE Id = @Id;");

        await conn.ExecuteAsync(
          new CommandDefinition(
            sql.ToString(),
            new
            {
              Id = request.Id.Value,
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
              PrimaryImage = request.PrimaryImageBytes,
              PrimaryImageFileName = NullIfWhiteSpace(request.PrimaryImageFileName),
              PrimaryImageContentType = imageContentType
            },
            tx,
            cancellationToken: ct));

        materialId = request.Id.Value;
      }
      else
      {
        const string insertSql =
          """
          INSERT INTO logistica.Material
          (
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
              PurchaseLink,
              MaterialClass,
              IsActive
          )
          VALUES
          (
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
            WHERE Id = @MaterialId;
            """,
            new { MaterialId = materialId },
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

  private SqlConnection CreateConnection()
    => _connectionFactory.Create() as SqlConnection
      ?? throw new InvalidOperationException("La fábrica de conexiones no devolvió una SqlConnection.");

  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
