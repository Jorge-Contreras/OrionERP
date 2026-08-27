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
  private const int LifecycleExampleLimit = 5;

  private static readonly IReadOnlyDictionary<string, MaterialDependencyDefinition> DependencyDefinitions =
    new MaterialDependencyDefinition[]
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
      new("MaterialAllergen", "Asignaciones de alérgenos", "El material conserva asignaciones de alérgenos, incluso si el alérgeno está inactivo.", "Revisar seguridad de recetas", "/restaurante/recetas/configuracion"),
      new("MaterialUnitConversion", "Conversiones de unidad", "El material conserva conversiones especiales de unidad, incluso si están inactivas.", "Revisar unidades de recetas", "/restaurante/recetas/configuracion")
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
          m.ProductType,
          m.FulfillmentMode,
          m.MaterialStatus AS [Status],
          m.IsActive,
          mc.CategoryName,
          u.UnitName AS BaseUnitName,
          bp.PartnerName AS VendorName,
          CAST(m.BaseUnitPrice AS decimal(18,6)) AS BaseUnitPrice,
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

    if (!filter.IncludeInactive)
    {
      sql.AppendLine(" AND m.IsActive = 1");
    }

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
          CAST(m.BaseUnitPrice AS decimal(18,6)) AS BaseUnitPrice,
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

  public async Task<MaterialInventorySnapshotDto> GetMaterialInventoryAsync(
    string rfc,
    int materialId,
    CancellationToken ct = default)
  {
    if (materialId <= 0)
    {
      return new MaterialInventorySnapshotDto();
    }

    const string sql =
      """
      SELECT
          balance.Id AS StockBalanceId,
          balance.LocationId,
          locationInfo.LocationCode,
          locationInfo.LocationName,
          locationInfo.LocationType,
          parentInfo.LocationName AS ParentLocationName,
          room.ROOM_NAME AS RoomName,
          CAST(locationInfo.IsActive AS bit) AS IsLocationActive,
          CAST(locationInfo.IsInventoryEnabled AS bit) AS IsInventoryEnabled,
          CAST(balance.Quantity AS decimal(18,4)) AS Quantity,
          CAST(ISNULL(balance.ReservedQuantity, 0) AS decimal(18,4)) AS ReservedQuantity,
          CAST(balance.MinQuantity AS decimal(18,4)) AS MinQuantity,
          CAST(balance.MaxQuantity AS decimal(18,4)) AS MaxQuantity,
          CAST(ISNULL(balance.AverageUnitCost, 0) AS decimal(18,6)) AS AverageUnitCost,
          CAST(CASE
              WHEN balance.MinQuantity IS NOT NULL AND balance.Quantity <= balance.MinQuantity THEN 1
              ELSE 0
          END AS bit) AS IsLowStock,
          CAST(CASE
              WHEN balance.MaxQuantity IS NOT NULL AND balance.Quantity > balance.MaxQuantity THEN 1
              ELSE 0
          END AS bit) AS IsOverStock,
          CAST(CASE
              WHEN balance.CountFrequencyDays IS NULL THEN 0
              WHEN balance.LastCountedAt IS NULL THEN 1
              WHEN DATEADD(day, balance.CountFrequencyDays, balance.LastCountedAt) <= SYSUTCDATETIME() THEN 1
              ELSE 0
          END AS bit) AS IsCountDue,
          balance.LastCountedAt,
          balance.CountFrequencyDays,
          balance.LastPurchaseDate,
          balance.Notes,
          CAST(ISNULL(balance.IsRemoved, 0) AS bit) AS IsRemoved,
          balance.RemovedAt,
          balance.RemovedBy,
          balance.UpdatedAt,
          ISNULL(movementInfo.MovementCount, 0) AS MovementCount,
          movementInfo.LastMovementAt,
          ISNULL(attachmentInfo.AttachmentCount, 0) AS AttachmentCount
      FROM logistica.StockBalance balance
      JOIN logistica.Location locationInfo
        ON locationInfo.Rfc = balance.Rfc AND locationInfo.Id = balance.LocationId
      LEFT JOIN logistica.Location parentInfo
        ON parentInfo.Rfc = locationInfo.Rfc AND parentInfo.Id = locationInfo.ParentLocationId
      LEFT JOIN dbo.ROOM room
        ON room.ID = locationInfo.RoomId
      OUTER APPLY
      (
          SELECT
              COUNT(*) AS MovementCount,
              MAX(movement.OccurredAt) AS LastMovementAt
          FROM logistica.StockTransaction movement
          WHERE movement.Rfc = balance.Rfc
            AND movement.StockBalanceId = balance.Id
      ) movementInfo
      OUTER APPLY
      (
          SELECT COUNT(*) AS AttachmentCount
          FROM logistica.LocationMaterialAttachment attachmentRow
          WHERE attachmentRow.Rfc = balance.Rfc
            AND attachmentRow.LocationId = balance.LocationId
            AND attachmentRow.MaterialId = balance.MaterialId
            AND ISNULL(attachmentRow.IsDeleted, 0) = 0
      ) attachmentInfo
      WHERE balance.Rfc = @Rfc
        AND balance.MaterialId = @MaterialId
      ORDER BY
          ISNULL(balance.IsRemoved, 0),
          room.ROOM_NAME,
          locationInfo.LocationName,
          locationInfo.LocationCode,
          balance.Id;

      SELECT
          movement.TransactionType,
          COUNT(*) AS MovementCount,
          MAX(movement.OccurredAt) AS LastOccurredAt
      FROM logistica.StockTransaction movement
      WHERE movement.Rfc = @Rfc
        AND movement.MaterialId = @MaterialId
      GROUP BY movement.TransactionType
      ORDER BY COUNT(*) DESC, movement.TransactionType;
      """;

    using var conn = CreateConnection();
    using var reader = await conn.QueryMultipleAsync(
      new CommandDefinition(
        sql,
        new { Rfc = LogisticsRfc.Require(rfc), MaterialId = materialId },
        cancellationToken: ct));

    var locations = (await reader.ReadAsync<MaterialStockLocationDto>()).AsList();
    var movementTypes = (await reader.ReadAsync<MaterialMovementTypeOptionDto>()).AsList();

    return new MaterialInventorySnapshotDto
    {
      MaterialId = materialId,
      Locations = locations,
      MovementTypes = movementTypes
    };
  }

  public async Task<IReadOnlyList<MaterialMovementDto>> GetMaterialMovementsAsync(
    MaterialMovementFilter filter,
    CancellationToken ct = default)
  {
    filter ??= new MaterialMovementFilter();
    if (filter.MaterialId <= 0)
    {
      return Array.Empty<MaterialMovementDto>();
    }

    var skip = Math.Max(filter.Skip, 0);
    var take = Math.Max(filter.Take, 0);

    var sql = new StringBuilder(
      """
      SELECT
          movement.Id,
          movement.OccurredAt,
          movement.TransactionType,
          CAST(movement.QuantityDelta AS decimal(18,4)) AS QuantityDelta,
          CAST(movement.QuantityAfter AS decimal(18,4)) AS QuantityAfter,
          movement.LocationId,
          locationInfo.LocationCode,
          locationInfo.LocationName,
          room.ROOM_NAME AS RoomName,
          movement.ReferenceType,
          movement.ReferenceId,
          movement.Notes,
          movement.PerformedBy
      FROM logistica.StockTransaction movement
      LEFT JOIN logistica.Location locationInfo
        ON locationInfo.Rfc = movement.Rfc AND locationInfo.Id = movement.LocationId
      LEFT JOIN dbo.ROOM room
        ON room.ID = locationInfo.RoomId
      WHERE movement.Rfc = @Rfc
        AND movement.MaterialId = @MaterialId
      """);

    var parameters = new DynamicParameters();
    parameters.Add("@Rfc", LogisticsRfc.Require(filter.Rfc), DbType.String);
    parameters.Add("@MaterialId", filter.MaterialId, DbType.Int32);

    if (filter.LocationId.HasValue)
    {
      sql.AppendLine(" AND movement.LocationId = @LocationId");
      parameters.Add("@LocationId", filter.LocationId.Value, DbType.Int32);
    }

    if (!string.IsNullOrWhiteSpace(filter.TransactionType))
    {
      sql.AppendLine(" AND movement.TransactionType = @TransactionType");
      parameters.Add("@TransactionType", filter.TransactionType.Trim(), DbType.String);
    }

    if (filter.OccurredFromUtc.HasValue)
    {
      sql.AppendLine(" AND movement.OccurredAt >= @OccurredFromUtc");
      parameters.Add("@OccurredFromUtc", filter.OccurredFromUtc.Value, DbType.DateTime2);
    }

    if (filter.OccurredToUtc.HasValue)
    {
      sql.AppendLine(" AND movement.OccurredAt < @OccurredToUtc");
      parameters.Add("@OccurredToUtc", filter.OccurredToUtc.Value, DbType.DateTime2);
    }

    if (!string.IsNullOrWhiteSpace(filter.SearchText))
    {
      sql.AppendLine(
        """
         AND (
             movement.TransactionType LIKE @Search
             OR movement.Notes LIKE @Search
             OR movement.PerformedBy LIKE @Search
             OR movement.ReferenceType LIKE @Search
             OR locationInfo.LocationCode LIKE @Search
             OR locationInfo.LocationName LIKE @Search
             OR room.ROOM_NAME LIKE @Search
         )
        """);
      parameters.Add("@Search", $"%{filter.SearchText.Trim()}%", DbType.String);
    }

    sql.AppendLine();
    sql.AppendLine("ORDER BY movement.OccurredAt DESC, movement.Id DESC");

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
    var rows = await conn.QueryAsync<MaterialMovementDto>(
      new CommandDefinition(sql.ToString(), parameters, cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<MaterialLifecycleAssessmentDto> GetMaterialLifecycleAssessmentAsync(
    string rfc,
    int materialId,
    CancellationToken ct = default)
  {
    if (materialId <= 0)
    {
      return new MaterialLifecycleAssessmentDto();
    }

    using var conn = CreateConnection();
    return await LoadMaterialLifecycleAssessmentAsync(
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

      var assessment = await LoadMaterialLifecycleAssessmentAsync(
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
          $"El material no se puede eliminar porque conserva {assessment.TotalReferences:N0} referencia(s) en {assessment.Dependencies.Count:N0} grupo(s). Revisa el reporte actualizado.");
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

  public async Task<LogisticsCommandResult> DeactivateMaterialAsync(MaterialDeactivateRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    if (request.MaterialId <= 0)
    {
      return LogisticsCommandResult.Fail("Selecciona un material válido para desactivar.");
    }

    var rfc = LogisticsRfc.Require(request.Rfc);
    var deactivatedBy = NullIfWhiteSpace(request.DeactivatedBy) ?? "OrionERP";
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);

    try
    {
      var assessment = await LoadMaterialLifecycleAssessmentAsync(conn, tx, rfc, request.MaterialId, lockMaterial: true, ct);
      if (!assessment.Exists)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("El material ya no existe o no pertenece al RFC seleccionado.");
      }

      if (!assessment.IsActive)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("El material ya está desactivado.");
      }

      if (assessment.OperationalBlockers.Count > 0)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail(
          $"El material conserva {assessment.OperationalReferenceCount:N0} vínculo(s) operativo(s). Resuélvelos y vuelve a revisar el reporte.");
      }

      if (!assessment.HasHistory)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Este material no tiene historial conservado. Libera toda su configuración y elimínalo permanentemente.");
      }

      var affected = await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE logistica.Material
        SET IsActive = 0,
            MaterialStatus = 'INACTIVO',
            UpdatedDate = CONVERT(date, SYSUTCDATETIME())
        WHERE Rfc = @Rfc AND Id = @MaterialId AND IsActive = 1;
        """,
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
        "Material {MaterialCode} ({MaterialId}) deactivated for RFC {Rfc} by {DeactivatedBy}; {HistoricalReferences} historical references retained.",
        assessment.MaterialCode,
        assessment.MaterialId,
        rfc,
        deactivatedBy,
        assessment.HistoricalReferenceCount);
      return LogisticsCommandResult.Ok($"Material {assessment.MaterialCode} desactivado. Su historial permanece disponible.", assessment.MaterialId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<LogisticsCommandResult> ReactivateMaterialAsync(MaterialReactivateRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    if (request.MaterialId <= 0)
    {
      return LogisticsCommandResult.Fail("Selecciona un material válido para reactivar.");
    }

    var rfc = LogisticsRfc.Require(request.Rfc);
    var reactivatedBy = NullIfWhiteSpace(request.ReactivatedBy) ?? "OrionERP";
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);

    try
    {
      var material = await conn.QuerySingleOrDefaultAsync<MaterialLifecycleStateRow>(new CommandDefinition(
        """
        SELECT Id, MaterialCode, [Description], IsActive
        FROM logistica.Material WITH (UPDLOCK, HOLDLOCK)
        WHERE Rfc = @Rfc AND Id = @MaterialId;
        """,
        new { Rfc = rfc, MaterialId = request.MaterialId },
        tx,
        cancellationToken: ct));

      if (material is null)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("El material ya no existe o no pertenece al RFC seleccionado.");
      }

      if (material.IsActive)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("El material ya está activo.");
      }

      var affected = await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE logistica.Material
        SET IsActive = 1,
            MaterialStatus = 'ACTIVO',
            UpdatedDate = CONVERT(date, SYSUTCDATETIME())
        WHERE Rfc = @Rfc AND Id = @MaterialId AND IsActive = 0;
        """,
        new { Rfc = rfc, MaterialId = request.MaterialId },
        tx,
        cancellationToken: ct));

      if (affected != 1)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("El material cambió mientras se procesaba la solicitud. Actualiza el catálogo.");
      }

      await tx.CommitAsync(ct);
      _logger?.LogInformation(
        "Material {MaterialCode} ({MaterialId}) reactivated for RFC {Rfc} by {ReactivatedBy}.",
        material.MaterialCode,
        material.Id,
        rfc,
        reactivatedBy);
      return LogisticsCommandResult.Ok($"Material {material.MaterialCode} reactivado.", material.Id);
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

    if (request.BaseUnitPrice < 0m)
    {
      return LogisticsCommandResult.Fail("El precio por unidad base no puede ser negativo.");
    }

    var baseUnitPrice = MaterialPriceCalculator.NormalizeBaseUnitPrice(request.BaseUnitPrice);

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
        var lifecycleState = await conn.QuerySingleOrDefaultAsync<MaterialLifecycleStateRow>(new CommandDefinition(
          "SELECT Id, MaterialCode, [Description], IsActive FROM logistica.Material WITH (UPDLOCK, HOLDLOCK) WHERE Rfc = @Rfc AND Id = @Id;",
          new { Rfc = rfc, Id = request.Id.Value },
          tx,
          cancellationToken: ct));
        if (lifecycleState is null)
        {
          await tx.RollbackAsync(ct);
          return LogisticsCommandResult.Fail("El material no pertenece al RFC seleccionado.");
        }

        var materialStatus = lifecycleState.IsActive ? request.Status?.Trim() : "INACTIVO";
        if (lifecycleState.IsActive && string.Equals(materialStatus, "INACTIVO", StringComparison.OrdinalIgnoreCase))
        {
          await tx.RollbackAsync(ct);
          return LogisticsCommandResult.Fail("Usa la revisión de retiro para desactivar un material.");
        }

        var sql = new StringBuilder(
          """
          UPDATE logistica.Material
          SET [Description] = @Description,
              BaseUnitId = @BaseUnitId,
              PurchaseQuantity = @PurchaseQuantity,
              PurchaseUnitId = @PurchaseUnitId,
              BusinessPartnerId = @BusinessPartnerId,
              BaseUnitPrice = @BaseUnitPrice,
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
              MaterialClass = @MaterialClass
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
              BaseUnitPrice = baseUnitPrice,
              Brand = NullIfWhiteSpace(request.Brand),
              Model = NullIfWhiteSpace(request.Model),
              request.IsPerishable,
              request.ShelfLifeDays,
              request.RequiresRefrigeration,
              Status = materialStatus,
              request.CategoryId,
              Barcode = NullIfWhiteSpace(request.Barcode),
              VendorCode = NullIfWhiteSpace(request.VendorCode),
              PurchaseLink = NullIfWhiteSpace(request.PurchaseLink),
              request.MaterialClass,
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
        if (string.Equals(request.Status?.Trim(), "INACTIVO", StringComparison.OrdinalIgnoreCase))
        {
          await tx.RollbackAsync(ct);
          return LogisticsCommandResult.Fail("Los materiales nuevos deben crearse activos.");
        }

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
              BaseUnitPrice,
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
              @BaseUnitPrice,
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
              1
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
              BaseUnitPrice = baseUnitPrice,
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
              request.MaterialClass
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

  private async Task<MaterialLifecycleAssessmentDto> LoadMaterialLifecycleAssessmentAsync(
    DbConnection connection,
    DbTransaction? transaction,
    string rfc,
    int materialId,
    bool lockMaterial,
    CancellationToken ct)
  {
    var sql = MaterialLifecycleAssessmentSql.Replace(
      "/*MATERIAL_LOCK*/",
      lockMaterial ? "WITH (UPDLOCK, HOLDLOCK)" : string.Empty,
      StringComparison.Ordinal);

    var rows = (await connection.QueryAsync<MaterialLifecycleAssessmentRow>(
      new CommandDefinition(
        sql,
        new { Rfc = rfc, MaterialId = materialId, ExampleLimit = LifecycleExampleLimit },
        transaction,
        cancellationToken: ct))).AsList();

    if (rows.Count == 0)
    {
      return new MaterialLifecycleAssessmentDto();
    }

    var material = rows[0];
    var dependencies = rows
      .Where(row => !string.IsNullOrWhiteSpace(row.BlockerCode))
      .GroupBy(row => (Code: row.BlockerCode!, row.DependencyKind))
      .OrderBy(group => DependencyKindSortOrder(group.Key.DependencyKind))
      .ThenBy(group => group.Min(row => row.BlockerSortOrder))
      .Select(group =>
      {
        var definition = DependencyDefinitions[group.Key.Code];
        return new MaterialDependencyDto
        {
          Code = definition.Code,
          Kind = group.Key.DependencyKind,
          Title = definition.Title,
          Explanation = GetDependencyExplanation(definition, group.Key.DependencyKind),
          ReferenceCount = group.Max(row => row.ReferenceCount),
          Examples = group
            .Select(row => row.Example)
            .Where(example => !string.IsNullOrWhiteSpace(example))
            .Select(example => example!)
            .Distinct(StringComparer.Ordinal)
            .Take(LifecycleExampleLimit)
            .ToArray(),
          ResolutionLabel = definition.ResolutionLabel,
          ResolutionUrl = definition.ResolutionUrl
        };
      })
      .ToArray();

    return new MaterialLifecycleAssessmentDto
    {
      Exists = true,
      MaterialId = material.MaterialId,
      MaterialCode = material.MaterialCode,
      Description = material.Description,
      IsActive = material.IsActive,
      Dependencies = dependencies
    };
  }

  private const string MaterialLifecycleAssessmentSql =
    """
    ;WITH DependencyRows AS
    (
      SELECT
        N'StockBalance' AS BlockerCode,
        CASE
          WHEN ISNULL(balance.IsRemoved, 0) = 1
            AND balance.Quantity = 0
            AND ISNULL(balance.ReservedQuantity, 0) = 0 THEN N'Historical'
          ELSE N'Operational'
        END AS DependencyKind,
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
        N'StockTransaction', N'Historical', 20, CONVERT(nvarchar(100), movement.Id), movement.OccurredAt,
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
        N'LocationMaterialAttachment',
        CASE WHEN ISNULL(attachmentInfo.IsDeleted, 0) = 1 THEN N'Historical' ELSE N'Operational' END,
        30, CONVERT(nvarchar(100), attachmentInfo.Id), attachmentInfo.CreatedAt,
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
        N'PhysicalCountLine',
        CASE WHEN countSession.Status IN ('Posted', 'Canceled') THEN N'Historical' ELSE N'Operational' END,
        40, CONVERT(nvarchar(100), countLine.Id), countLine.CapturedAt,
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
        N'MaterialLot', N'Historical', 50, CONVERT(nvarchar(100), materialLot.Id), materialLot.CreatedAt,
        CAST(CONCAT(
          'Lote ', materialLot.LotCode,
          CASE WHEN materialLot.ExpiresAt IS NULL THEN '' ELSE CONCAT(' · Vence: ', CONVERT(varchar(10), materialLot.ExpiresAt, 23)) END,
          CASE WHEN materialLot.IsBlocked = 1 THEN ' · Bloqueado' ELSE ' · Disponible' END
        ) AS nvarchar(1000))
      FROM logistica.MaterialLot materialLot
      WHERE materialLot.Rfc = @Rfc AND materialLot.MaterialId = @MaterialId

      UNION ALL

      SELECT
        N'LotBalance',
        CASE WHEN lotBalance.Quantity = 0 AND lotBalance.ReservedQuantity = 0 THEN N'Historical' ELSE N'Operational' END,
        60, CONVERT(nvarchar(100), lotBalance.Id), lotBalance.UpdatedAt,
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
        N'InventoryReservationLine',
        CASE WHEN reservationInfo.Status IN ('Released', 'Consumed') THEN N'Historical' ELSE N'Operational' END,
        70, CONVERT(nvarchar(100), reservationLine.Id), reservationInfo.CreatedAt,
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
        N'InventoryTransferLine',
        CASE WHEN transferInfo.Status = 'Posted' THEN N'Historical' ELSE N'Operational' END,
        80, CONVERT(nvarchar(100), transferLine.Id), transferInfo.CreatedAt,
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
        N'InventoryAdjustmentLine',
        CASE WHEN adjustmentInfo.Status = 'Approved' THEN N'Historical' ELSE N'Operational' END,
        90, CONVERT(nvarchar(100), adjustmentLine.Id), adjustmentInfo.CreatedAt,
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
        N'PurchaseOrderLine',
        CASE WHEN purchaseOrder.Status IN ('Completed', 'Cancelled') THEN N'Historical' ELSE N'Operational' END,
        100, CONVERT(nvarchar(100), purchaseLine.Id), purchaseOrder.UpdatedAt,
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
        N'PurchaseReceiptLine', N'Historical', 110, CONVERT(nvarchar(100), receiptLine.Id), receiptLine.CreatedAt,
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
        N'BomHeader',
        CASE
          WHEN EXISTS
          (
            SELECT 1 FROM logistica.BomVersion unknownVersion
            WHERE unknownVersion.Rfc = bomHeader.Rfc
              AND unknownVersion.BomHeaderId = bomHeader.Id
              AND (unknownVersion.Status IS NULL OR unknownVersion.Status NOT IN ('Draft', 'Active', 'Retired'))
          ) THEN N'Operational'
          WHEN EXISTS
          (
            SELECT 1 FROM logistica.BomVersion currentVersion
            WHERE currentVersion.Rfc = bomHeader.Rfc
              AND currentVersion.BomHeaderId = bomHeader.Id
              AND currentVersion.Status IN ('Draft', 'Active')
          ) THEN N'Operational'
          WHEN EXISTS
          (
            SELECT 1 FROM logistica.BomVersion retiredVersion
            WHERE retiredVersion.Rfc = bomHeader.Rfc
              AND retiredVersion.BomHeaderId = bomHeader.Id
              AND retiredVersion.Status = 'Retired'
          ) THEN N'Historical'
          ELSE N'Configuration'
        END,
        120, CONVERT(nvarchar(100), bomHeader.Id), bomHeader.CreatedAt,
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
        N'BomComponent',
        CASE WHEN bomVersion.Status = 'Retired' THEN N'Historical' ELSE N'Operational' END,
        130, CONVERT(nvarchar(100), bomComponent.Id), bomVersion.CreatedAt,
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
        N'ProductionOrder',
        CASE WHEN productionOrder.Status IN ('Completed', 'Cancelled') THEN N'Historical' ELSE N'Operational' END,
        140, CONVERT(nvarchar(100), productionOrder.Id), productionOrder.PlannedAt,
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
        N'RestaurantProduct',
        CASE WHEN restaurantProduct.IsActive = 1 THEN N'Operational' ELSE N'Historical' END,
        150, CONVERT(nvarchar(100), restaurantProduct.Id), CAST(NULL AS datetime2),
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
        N'ModifierIngredientDelta',
        CASE WHEN modifierOption.IsActive = 1 AND modifierGroup.IsActive = 1 THEN N'Operational' ELSE N'Configuration' END,
        160, CONVERT(nvarchar(100), ingredientDelta.Id), CAST(NULL AS datetime2),
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
        N'MaterialAllergen',
        CASE WHEN allergenInfo.IsActive = 1 THEN N'Operational' ELSE N'Configuration' END,
        170, CONVERT(nvarchar(100), allergenAssignment.AllergenId), CAST(NULL AS datetime2),
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
        N'MaterialUnitConversion',
        CASE WHEN conversionInfo.IsActive = 1 THEN N'Operational' ELSE N'Configuration' END,
        180, CONVERT(nvarchar(100), conversionInfo.Id), CAST(NULL AS datetime2),
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
        DependencyKind,
        BlockerSortOrder,
        Example,
        COUNT_BIG(*) OVER (PARTITION BY BlockerCode, DependencyKind) AS ReferenceCount,
        ROW_NUMBER() OVER
        (
          PARTITION BY BlockerCode, DependencyKind
          ORDER BY CASE WHEN SortDate IS NULL THEN 1 ELSE 0 END, SortDate DESC, ReferenceKey DESC
        ) AS ExampleOrdinal
      FROM DependencyRows
    )
    SELECT
      material.Id AS MaterialId,
      material.MaterialCode,
      material.[Description],
      material.IsActive,
      dependency.BlockerCode,
      dependency.DependencyKind,
      dependency.BlockerSortOrder,
      dependency.ReferenceCount,
      dependency.Example
    FROM logistica.Material material /*MATERIAL_LOCK*/
    LEFT JOIN RankedDependencies dependency
      ON dependency.ExampleOrdinal <= @ExampleLimit
    WHERE material.Rfc = @Rfc
      AND material.Id = @MaterialId
    ORDER BY
      CASE dependency.DependencyKind WHEN 'Operational' THEN 0 WHEN 'Historical' THEN 1 ELSE 2 END,
      dependency.BlockerSortOrder,
      dependency.ExampleOrdinal;
    """;

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica de conexiones no devolvió una DbConnection.");

  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private static int DependencyKindSortOrder(string kind)
    => kind switch
    {
      MaterialDependencyKinds.Operational => 0,
      MaterialDependencyKinds.Historical => 1,
      _ => 2
    };

  private static string GetDependencyExplanation(MaterialDependencyDefinition definition, string kind)
    => (definition.Code, kind) switch
    {
      ("StockBalance", MaterialDependencyKinds.Operational) => "La ubicación sigue activa para este material o conserva existencia/reserva. Retira la asignación y deja ambos saldos en cero.",
      ("StockBalance", MaterialDependencyKinds.Historical) => "La asignación ya fue retirada con saldos en cero y se conserva como evidencia de inventario.",
      ("LocationMaterialAttachment", MaterialDependencyKinds.Operational) => "Hay evidencia activa del material en una ubicación. Archívala o resuelve la asignación desde Ubicaciones.",
      ("LocationMaterialAttachment", MaterialDependencyKinds.Historical) => "El archivo ya está archivado y debe permanecer vinculado como evidencia.",
      ("PhysicalCountLine", MaterialDependencyKinds.Operational) => "El conteo todavía no está publicado ni cancelado. Termina su flujo antes de retirar el material.",
      ("PhysicalCountLine", MaterialDependencyKinds.Historical) => "El conteo fue publicado o cancelado y debe conservar la identidad del material auditado.",
      ("LotBalance", MaterialDependencyKinds.Operational) => "Existe un saldo o reserva de lote distinto de cero que debe consumirse, liberarse o ajustarse.",
      ("LotBalance", MaterialDependencyKinds.Historical) => "El saldo de lote llegó a cero y se conserva como trazabilidad.",
      ("InventoryReservationLine", MaterialDependencyKinds.Operational) => "La reserva no está liberada ni consumida. Completa o libera la operación que la originó.",
      ("InventoryReservationLine", MaterialDependencyKinds.Historical) => "La reserva fue liberada o consumida y permanece como historial.",
      ("InventoryTransferLine", MaterialDependencyKinds.Operational) => "La transferencia aún no está publicada o usa un estado no reconocido. Complétala antes de continuar.",
      ("InventoryTransferLine", MaterialDependencyKinds.Historical) => "La transferencia publicada debe conservar el material movilizado.",
      ("InventoryAdjustmentLine", MaterialDependencyKinds.Operational) => "El ajuste aún no está aprobado o usa un estado no reconocido. Resuelve su autorización.",
      ("InventoryAdjustmentLine", MaterialDependencyKinds.Historical) => "El ajuste aprobado forma parte del historial de inventario.",
      ("PurchaseOrderLine", MaterialDependencyKinds.Operational) => "La orden de compra aún no está completada o cancelada. Termina su flujo antes de retirar el material.",
      ("PurchaseOrderLine", MaterialDependencyKinds.Historical) => "La orden completada o cancelada debe conservar la línea del material.",
      ("BomHeader", MaterialDependencyKinds.Operational) => "El material tiene una versión de BOM activa, en borrador o con estado no reconocido. Elimina el borrador o retira la versión activa.",
      ("BomHeader", MaterialDependencyKinds.Historical) => "El BOM retirado debe conservar el producto terminado al que perteneció.",
      ("BomHeader", MaterialDependencyKinds.Configuration) => "La cabecera de BOM no tiene versiones operativas ni retiradas y puede limpiarse desde Recetas.",
      ("BomComponent", MaterialDependencyKinds.Operational) => "El material se usa en una versión de BOM activa, en borrador o con estado no reconocido. Retira o elimina esa versión.",
      ("BomComponent", MaterialDependencyKinds.Historical) => "Una versión de BOM retirada conserva este material como ingrediente histórico.",
      ("ProductionOrder", MaterialDependencyKinds.Operational) => "La producción aún no está completada o cancelada. Termina su flujo antes de retirar el material.",
      ("ProductionOrder", MaterialDependencyKinds.Historical) => "La producción completada o cancelada debe conservar el material producido.",
      ("RestaurantProduct", MaterialDependencyKinds.Operational) => "El material está ligado a un producto activo del restaurante. Desactiva o desvincula el producto.",
      ("RestaurantProduct", MaterialDependencyKinds.Historical) => "El producto del restaurante está inactivo y conserva el material que utilizó.",
      ("ModifierIngredientDelta", MaterialDependencyKinds.Operational) => "Un grupo y opción activos agregan o retiran este ingrediente. Desvincula el ajuste desde Menús.",
      ("ModifierIngredientDelta", MaterialDependencyKinds.Configuration) => "El ajuste pertenece a un modificador inactivo y puede eliminarse desde Menús.",
      ("MaterialAllergen", MaterialDependencyKinds.Operational) => "El material conserva un alérgeno activo. Quita la asignación desde Recetas.",
      ("MaterialAllergen", MaterialDependencyKinds.Configuration) => "La asignación apunta a un alérgeno inactivo y puede quitarse desde Recetas.",
      ("MaterialUnitConversion", MaterialDependencyKinds.Operational) => "La conversión especial está activa. Elimínala cuando ningún BOM actual dependa de ella.",
      ("MaterialUnitConversion", MaterialDependencyKinds.Configuration) => "La conversión especial está inactiva, pero todavía debe eliminarse para borrar físicamente el material.",
      _ => definition.Explanation
    };

  private sealed record MaterialDependencyDefinition(
    string Code,
    string Title,
    string Explanation,
    string? ResolutionLabel,
    string? ResolutionUrl);

  private sealed class MaterialLifecycleAssessmentRow
  {
    public int MaterialId { get; set; }
    public string MaterialCode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string? BlockerCode { get; set; }
    public string DependencyKind { get; set; } = string.Empty;
    public int BlockerSortOrder { get; set; }
    public long ReferenceCount { get; set; }
    public string? Example { get; set; }
  }

  private sealed class MaterialLifecycleStateRow
  {
    public int Id { get; set; }
    public string MaterialCode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsActive { get; set; }
  }
}
