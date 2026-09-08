using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Data.SqlClient;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

public sealed class BomRecipeService : IBomRecipeService
{
  private readonly IDbConnectionFactory _connectionFactory;

  public BomRecipeService(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  }

  public async Task<IReadOnlyList<BomVersionDto>> GetBomVersionsAsync(string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    const string sql =
      """
      SELECT versionInfo.Id, headerInfo.ProductMaterialId, material.[Description] AS ProductName,
             versionInfo.VersionNumber, versionInfo.[Status], versionInfo.YieldQuantity, versionInfo.YieldUnitId,
             versionInfo.ExpectedWastePercent,
             CAST(ISNULL(versionInfo.FrozenTheoreticalCost, 0) AS decimal(18,6)) AS TheoreticalCost,
             recipe.SafetyNotes, versionInfo.CreatedAt, versionInfo.CreatedBy,
             versionInfo.EffectiveFrom, versionInfo.RetiredAt,
             (SELECT COUNT(*) FROM logistica.BomComponent component WHERE component.Rfc=versionInfo.Rfc AND component.BomVersionId=versionInfo.Id) AS ComponentCount,
             (SELECT COUNT(*) FROM logistica.RecipeStep stepInfo JOIN logistica.Recipe recipeCount ON recipeCount.Rfc=stepInfo.Rfc AND recipeCount.Id=stepInfo.RecipeId WHERE recipeCount.Rfc=versionInfo.Rfc AND recipeCount.BomVersionId=versionInfo.Id) AS StepCount
      FROM logistica.BomVersion versionInfo
      JOIN logistica.BomHeader headerInfo ON headerInfo.Rfc = versionInfo.Rfc AND headerInfo.Id = versionInfo.BomHeaderId
      JOIN logistica.Material material ON material.Rfc = headerInfo.Rfc AND material.Id = headerInfo.ProductMaterialId
      LEFT JOIN logistica.Recipe recipe ON recipe.Rfc=versionInfo.Rfc AND recipe.BomVersionId=versionInfo.Id
      WHERE versionInfo.Rfc = @Rfc
      ORDER BY material.[Description], versionInfo.VersionNumber DESC;
      """;
    using var conn = CreateConnection();
    return (await conn.QueryAsync<BomVersionDto>(new CommandDefinition(sql, new { Rfc = normalizedRfc }, cancellationToken: ct))).AsList();
  }

  public async Task<BomVersionDto?> GetBomVersionAsync(string rfc, long bomVersionId, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    const string sql =
      """
      SELECT versionInfo.Id, headerInfo.ProductMaterialId, material.[Description] AS ProductName,
             versionInfo.VersionNumber, versionInfo.[Status], versionInfo.YieldQuantity, versionInfo.YieldUnitId,
             versionInfo.ExpectedWastePercent,
             CAST(ISNULL(versionInfo.FrozenTheoreticalCost, 0) AS decimal(18,6)) AS TheoreticalCost,
             recipe.SafetyNotes, versionInfo.CreatedAt, versionInfo.CreatedBy,
             versionInfo.EffectiveFrom, versionInfo.RetiredAt,
             (SELECT COUNT(*) FROM logistica.BomComponent componentCount WHERE componentCount.Rfc=versionInfo.Rfc AND componentCount.BomVersionId=versionInfo.Id) AS ComponentCount,
             (SELECT COUNT(*) FROM logistica.RecipeStep stepCount JOIN logistica.Recipe recipeCount ON recipeCount.Rfc=stepCount.Rfc AND recipeCount.Id=stepCount.RecipeId WHERE recipeCount.Rfc=versionInfo.Rfc AND recipeCount.BomVersionId=versionInfo.Id) AS StepCount
      FROM logistica.BomVersion versionInfo
      JOIN logistica.BomHeader headerInfo ON headerInfo.Rfc = versionInfo.Rfc AND headerInfo.Id = versionInfo.BomHeaderId
      JOIN logistica.Material material ON material.Rfc = headerInfo.Rfc AND material.Id = headerInfo.ProductMaterialId
      LEFT JOIN logistica.Recipe recipe ON recipe.Rfc=versionInfo.Rfc AND recipe.BomVersionId=versionInfo.Id
      WHERE versionInfo.Rfc = @Rfc AND versionInfo.Id = @BomVersionId;

      SELECT component.Id, component.ComponentMaterialId AS MaterialId, material.[Description] AS MaterialName,
             component.Quantity, component.UnitId, unitInfo.UnitName, component.ExpectedWastePercent
      FROM logistica.BomComponent component
      JOIN logistica.Material material ON material.Rfc = component.Rfc AND material.Id = component.ComponentMaterialId
      JOIN logistica.UnitOfMeasure unitInfo ON unitInfo.Id = component.UnitId
      WHERE component.Rfc = @Rfc AND component.BomVersionId = @BomVersionId
      ORDER BY component.SortOrder, component.Id;

      SELECT stepInfo.Id, stepInfo.StepNumber, stepInfo.Instruction, stepInfo.DurationMinutes,
             stepInfo.TemperatureC, stepInfo.Equipment, stepInfo.Image, stepInfo.ImageFileName, stepInfo.ImageContentType
      FROM logistica.Recipe recipe
      JOIN logistica.RecipeStep stepInfo ON stepInfo.Rfc = recipe.Rfc AND stepInfo.RecipeId = recipe.Id
      WHERE recipe.Rfc = @Rfc AND recipe.BomVersionId = @BomVersionId
      ORDER BY stepInfo.StepNumber;

      ;WITH RecipeTree AS
      (
        SELECT headerInfo.ProductMaterialId AS MaterialId, versionInfo.Id AS BomVersionId,
               CAST(CONCAT('/', headerInfo.ProductMaterialId, '/') AS varchar(max)) AS MaterialPath, 0 AS Depth
        FROM logistica.BomVersion versionInfo
        JOIN logistica.BomHeader headerInfo ON headerInfo.Rfc=versionInfo.Rfc AND headerInfo.Id=versionInfo.BomHeaderId
        WHERE versionInfo.Rfc=@Rfc AND versionInfo.Id=@BomVersionId

        UNION ALL

        SELECT component.ComponentMaterialId, childVersion.Id,
               CAST(CONCAT(tree.MaterialPath, component.ComponentMaterialId, '/') AS varchar(max)), tree.Depth + 1
        FROM RecipeTree tree
        JOIN logistica.BomComponent component ON component.Rfc=@Rfc AND component.BomVersionId=tree.BomVersionId
        JOIN logistica.BomHeader childHeader ON childHeader.Rfc=@Rfc AND childHeader.ProductMaterialId=component.ComponentMaterialId
        JOIN logistica.BomVersion childVersion ON childVersion.Rfc=childHeader.Rfc AND childVersion.BomHeaderId=childHeader.Id AND childVersion.[Status]='Active'
        WHERE tree.Depth < 31
          AND tree.MaterialPath NOT LIKE CONCAT('%/', component.ComponentMaterialId, '/%')
      ), AllergenMaterials AS
      (
        SELECT tree.MaterialId
        FROM RecipeTree tree

        UNION

        SELECT component.ComponentMaterialId
        FROM RecipeTree tree
        JOIN logistica.BomComponent component ON component.Rfc=@Rfc AND component.BomVersionId=tree.BomVersionId
      )
      SELECT DISTINCT allergen.[Name]
      FROM AllergenMaterials materialInfo
      JOIN logistica.MaterialAllergen assignment ON assignment.Rfc=@Rfc AND assignment.MaterialId=materialInfo.MaterialId
      JOIN logistica.Allergen allergen ON allergen.Id=assignment.AllergenId AND allergen.IsActive=1
      ORDER BY allergen.[Name]
      OPTION (MAXRECURSION 32);
      """;
    using var conn = CreateConnection();
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, new { Rfc = normalizedRfc, BomVersionId = bomVersionId }, cancellationToken: ct));
    var version = await multi.ReadSingleOrDefaultAsync<BomVersionDto>();
    var components = (await multi.ReadAsync<BomComponentDto>()).AsList();
    var steps = (await multi.ReadAsync<RecipeStepDto>()).AsList();
    var allergens = (await multi.ReadAsync<string>()).AsList();
    if (version is null)
    {
      return null;
    }
    version.Components = components;
    version.Steps = steps;
    version.Allergens = allergens;
    return version;
  }

  public async Task<BomCostBreakdownDto?> GetCostBreakdownAsync(string rfc, long bomVersionId, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    const string sql =
      """
      SELECT versionInfo.Id AS BomVersionId, versionInfo.YieldQuantity, versionInfo.YieldUnitId,
             yieldUnit.UnitName AS YieldUnitName,
             CAST(ISNULL(versionInfo.FrozenTheoreticalCost, 0) AS decimal(18,6)) AS StoredUnitCost
      FROM logistica.BomVersion versionInfo
      JOIN logistica.UnitOfMeasure yieldUnit ON yieldUnit.Id=versionInfo.YieldUnitId
      WHERE versionInfo.Rfc=@Rfc AND versionInfo.Id=@BomVersionId;

      SELECT component.ComponentMaterialId AS MaterialId, material.[Description] AS MaterialName,
             component.Quantity AS RecipeQuantity, recipeUnit.UnitName AS RecipeUnitName,
             component.ExpectedWastePercent AS WastePercent,
             CAST(COALESCE(materialConversion.Factor, globalConversion.Factor,
                  CASE WHEN component.UnitId=material.BaseUnitId THEN 1 END) AS decimal(24,10)) AS ConversionFactor,
             baseUnit.UnitName AS BaseUnitName,
             CAST(COALESCE(subBom.FrozenTheoreticalCost / NULLIF(subBom.YieldQuantity, 0),
                  material.BaseUnitPrice, 0) AS decimal(24,10)) AS UnitCost,
             CASE
               WHEN subBom.FrozenTheoreticalCost / NULLIF(subBom.YieldQuantity, 0) IS NOT NULL THEN 'Costo de subreceta activa'
               WHEN material.BaseUnitPrice IS NOT NULL THEN 'Precio de Materiales'
               ELSE 'Sin costo configurado'
             END AS CostSource,
             CAST(CASE WHEN material.FulfillmentMode = 'StockItem' AND EXISTS
                  (
                    SELECT 1
                    FROM logistica.BomHeader ignoredHeader
                    JOIN logistica.BomVersion ignoredVersion
                      ON ignoredVersion.Rfc = ignoredHeader.Rfc AND ignoredVersion.BomHeaderId = ignoredHeader.Id
                     AND ignoredVersion.[Status] = 'Active'
                    WHERE ignoredHeader.Rfc = material.Rfc AND ignoredHeader.ProductMaterialId = material.Id
                  ) THEN 1 ELSE 0 END AS bit) AS RecipeCostIgnored
      FROM logistica.BomComponent component
      JOIN logistica.Material material ON material.Rfc=component.Rfc AND material.Id=component.ComponentMaterialId
      JOIN logistica.UnitOfMeasure recipeUnit ON recipeUnit.Id=component.UnitId
      JOIN logistica.UnitOfMeasure baseUnit ON baseUnit.Id=material.BaseUnitId
      OUTER APPLY
      (
        SELECT TOP (1) conversionInfo.Factor
        FROM logistica.MaterialUnitConversion conversionInfo
        WHERE conversionInfo.Rfc=material.Rfc AND conversionInfo.MaterialId=material.Id
          AND conversionInfo.FromUnitId=component.UnitId AND conversionInfo.ToUnitId=material.BaseUnitId
          AND conversionInfo.IsActive=1
      ) materialConversion
      OUTER APPLY
      (
        SELECT TOP (1) conversionInfo.Factor
        FROM logistica.UnitConversion conversionInfo
        WHERE conversionInfo.FromUnitId=component.UnitId AND conversionInfo.ToUnitId=material.BaseUnitId
          AND conversionInfo.IsActive=1
      ) globalConversion
      OUTER APPLY
      (
        SELECT TOP (1) childVersion.Id AS BomVersionId, childVersion.FrozenTheoreticalCost, childVersion.YieldQuantity
        FROM logistica.BomHeader childHeader
        JOIN logistica.BomVersion childVersion ON childVersion.Rfc=childHeader.Rfc AND childVersion.BomHeaderId=childHeader.Id
        WHERE childHeader.Rfc=material.Rfc AND childHeader.ProductMaterialId=material.Id
          AND childVersion.[Status]='Active'
          AND material.FulfillmentMode IN ('MakeToStock', 'MakeToOrder')
      ) subBom
      WHERE component.Rfc=@Rfc AND component.BomVersionId=@BomVersionId
      ORDER BY component.SortOrder, component.Id;
      """;

    using var conn = CreateConnection();
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(
      sql,
      new { Rfc = normalizedRfc, BomVersionId = bomVersionId },
      cancellationToken: ct));
    var version = await multi.ReadSingleOrDefaultAsync<CostBreakdownVersionRow>();
    var rows = (await multi.ReadAsync<CostBreakdownLineRow>()).AsList();
    if (version is null)
    {
      return null;
    }

    var lines = rows.Select(row =>
    {
      var baseQuantity = row.RecipeQuantity * row.ConversionFactor;
      var quantityWithWaste = baseQuantity * (1 + (row.WastePercent / 100m));
      var batchCost = quantityWithWaste * row.UnitCost;
      return new BomCostLineDto
      {
        MaterialId = row.MaterialId,
        MaterialName = row.MaterialName,
        RecipeQuantity = row.RecipeQuantity,
        RecipeUnitName = row.RecipeUnitName,
        WastePercent = row.WastePercent,
        ConversionFactor = row.ConversionFactor,
        BaseQuantity = decimal.Round(baseQuantity, 8),
        QuantityWithWaste = decimal.Round(quantityWithWaste, 8),
        BaseUnitName = row.BaseUnitName,
        UnitCost = decimal.Round(row.UnitCost, 6),
        CostSource = row.CostSource,
        BatchCost = decimal.Round(batchCost, 6),
        UnitContribution = version.YieldQuantity > 0
          ? decimal.Round(batchCost / version.YieldQuantity, 6)
          : 0
      };
    }).ToList();
    var currentBatchCost = rows.Sum(row =>
      row.RecipeQuantity * row.ConversionFactor * (1 + (row.WastePercent / 100m)) * row.UnitCost);

    return new BomCostBreakdownDto
    {
      BomVersionId = version.BomVersionId,
      YieldQuantity = version.YieldQuantity,
      YieldUnitId = version.YieldUnitId,
      YieldUnitName = version.YieldUnitName,
      StoredUnitCost = version.StoredUnitCost,
      CurrentBatchCost = decimal.Round(currentBatchCost, 6),
      CurrentUnitCost = version.YieldQuantity > 0
        ? decimal.Round(currentBatchCost / version.YieldQuantity, 6)
        : 0,
      Lines = lines
    };
  }

  public async Task<IReadOnlyList<RecipeUnitOptionDto>> GetRecipeUnitOptionsAsync(string rfc, CancellationToken ct = default)
  {
    const string sql =
      """
      WITH UnitOptions AS
      (
        SELECT material.Id AS MaterialId, material.BaseUnitId AS UnitId, unitInfo.Abbreviation AS UnitCode, unitInfo.UnitName,
               CAST(1 AS decimal(24,10)) AS FactorToBase, CAST(1 AS bit) AS IsBase, 0 AS Priority
        FROM logistica.Material material
        JOIN logistica.UnitOfMeasure unitInfo ON unitInfo.Id=material.BaseUnitId AND unitInfo.IsActive=1
        WHERE material.Rfc=@Rfc AND material.IsActive=1

        UNION ALL

        SELECT conversionInfo.MaterialId, conversionInfo.FromUnitId, unitInfo.Abbreviation AS UnitCode, unitInfo.UnitName,
               conversionInfo.Factor, CAST(0 AS bit), 1
        FROM logistica.MaterialUnitConversion conversionInfo
        JOIN logistica.Material material ON material.Rfc=conversionInfo.Rfc AND material.Id=conversionInfo.MaterialId AND material.BaseUnitId=conversionInfo.ToUnitId AND material.IsActive=1
        JOIN logistica.UnitOfMeasure unitInfo ON unitInfo.Id=conversionInfo.FromUnitId AND unitInfo.IsActive=1
        WHERE conversionInfo.Rfc=@Rfc AND conversionInfo.IsActive=1

        UNION ALL

        SELECT material.Id, conversionInfo.FromUnitId, unitInfo.Abbreviation AS UnitCode, unitInfo.UnitName,
               conversionInfo.Factor, CAST(0 AS bit), 2
        FROM logistica.Material material
        JOIN logistica.UnitConversion conversionInfo ON conversionInfo.ToUnitId=material.BaseUnitId AND conversionInfo.IsActive=1
        JOIN logistica.UnitOfMeasure unitInfo ON unitInfo.Id=conversionInfo.FromUnitId AND unitInfo.IsActive=1
        WHERE material.Rfc=@Rfc AND material.IsActive=1
      ), Ranked AS
      (
        SELECT *, ROW_NUMBER() OVER (PARTITION BY MaterialId, UnitId ORDER BY Priority) AS RowNumber
        FROM UnitOptions
      )
      SELECT MaterialId, UnitId, UnitCode, UnitName, FactorToBase, IsBase
      FROM Ranked
      WHERE RowNumber=1
      ORDER BY MaterialId, IsBase DESC, UnitName;
      """;
    using var conn = CreateConnection();
    return (await conn.QueryAsync<RecipeUnitOptionDto>(new CommandDefinition(
      sql,
      new { Rfc = LogisticsRfc.Require(rfc) },
      cancellationToken: ct))).AsList();
  }

  public async Task<RecipeActivationReadinessDto> GetActivationReadinessAsync(string rfc, long bomVersionId, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    var issues = new List<RecipeValidationIssueDto>();
    var warnings = new List<string>();
    var detail = await GetBomVersionAsync(normalizedRfc, bomVersionId, ct);
    if (detail is null)
    {
      issues.Add(new() { Section = "version", Code = "not_found", Message = "La receta seleccionada ya no existe." });
      return new() { Issues = issues };
    }
    if (!string.Equals(detail.Status, "Draft", StringComparison.OrdinalIgnoreCase))
      issues.Add(new() { Section = "version", Code = "not_draft", Message = "Sólo se puede activar una receta en borrador." });
    if (detail.Components.Count == 0)
      issues.Add(new() { Section = "ingredients", Code = "ingredients_required", Message = "Agrega al menos un ingrediente." });
    if (!detail.Steps.Any(step => !string.IsNullOrWhiteSpace(step.Instruction)))
      issues.Add(new() { Section = "steps", Code = "steps_required", Message = "Agrega al menos un paso con instrucciones." });
    if (string.IsNullOrWhiteSpace(detail.SafetyNotes))
      warnings.Add("La receta no incluye notas de seguridad; confirma que no sean necesarias.");

    using var conn = CreateConnection();
    var invalidUnit = await FindInvalidComponentUnitAsync(conn, null, normalizedRfc, bomVersionId, ct);
    if (invalidUnit is not null)
      issues.Add(new() { Section = "ingredients", Code = "unit_conversion_missing", Message = $"La unidad de {invalidUnit.Description} ya no tiene una conversión activa hacia su unidad base." });
    var yieldMismatch = await FindYieldUnitMismatchAsync(conn, null, normalizedRfc, bomVersionId, ct);
    if (yieldMismatch is not null)
      issues.Add(new() { Section = "basics", Code = "yield_unit_mismatch", Message = $"El rendimiento está en {yieldMismatch.YieldUnit} pero {yieldMismatch.Description} se inventaría en {yieldMismatch.BaseUnit}. Corrige el rendimiento: si no, el costo del lote completo se cargaría a una sola {yieldMismatch.BaseUnit}." });
    var incompleteSubassembly = await FindIncompleteSubassemblyAsync(conn, null, normalizedRfc, bomVersionId, detail.ProductMaterialId, ct);
    if (incompleteSubassembly is not null)
      issues.Add(new() { Section = "ingredients", Code = "subrecipe_missing", Message = $"{incompleteSubassembly.Description} está configurado como subreceta, pero no tiene una receta activa completa." });
    foreach (var subProduct in await FindUnproducibleBatchSubProductsAsync(conn, null, normalizedRfc, bomVersionId, ct))
      warnings.Add($"{subProduct} se produce por tanda pero todavía no tiene receta: sólo podrás venderlo mientras quede existencia.");
    var replacesVersion = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
      """
      SELECT TOP (1) activeVersion.VersionNumber
      FROM logistica.BomVersion selectedVersion
      JOIN logistica.BomVersion activeVersion ON activeVersion.Rfc=selectedVersion.Rfc AND activeVersion.BomHeaderId=selectedVersion.BomHeaderId AND activeVersion.[Status]='Active'
      WHERE selectedVersion.Rfc=@Rfc AND selectedVersion.Id=@Id;
      """,
      new { Rfc = normalizedRfc, Id = bomVersionId },
      cancellationToken: ct));
    return new() { ReplacesVersionNumber = replacesVersion, Issues = issues, Warnings = warnings };
  }

  public async Task<IReadOnlyList<RecipeUsageDto>> GetRecipeUsageAsync(string rfc, int materialId, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    using var conn = CreateConnection();
    return (await conn.QueryAsync<RecipeUsageDto>(new CommandDefinition(
      """
      SELECT parentHeader.ProductMaterialId, parentMaterial.[Description] AS ProductName,
             parentVersion.VersionNumber, component.Quantity, unitInfo.UnitName
      FROM logistica.BomComponent component
      JOIN logistica.BomVersion parentVersion
        ON parentVersion.Rfc = component.Rfc AND parentVersion.Id = component.BomVersionId
       AND parentVersion.[Status] = 'Active'
      JOIN logistica.BomHeader parentHeader
        ON parentHeader.Rfc = parentVersion.Rfc AND parentHeader.Id = parentVersion.BomHeaderId
      JOIN logistica.Material parentMaterial
        ON parentMaterial.Rfc = parentHeader.Rfc AND parentMaterial.Id = parentHeader.ProductMaterialId
      JOIN logistica.UnitOfMeasure unitInfo ON unitInfo.Id = component.UnitId
      WHERE component.Rfc = @Rfc AND component.ComponentMaterialId = @MaterialId
      ORDER BY parentMaterial.[Description];
      """,
      new { Rfc = normalizedRfc, MaterialId = materialId },
      cancellationToken: ct))).AsList();
  }

  public async Task<RestaurantCommandResult> SaveDraftAsync(BomDraftSaveRequest request, string userName, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    if (request.YieldQuantity <= 0 || request.Components.Count == 0)
    {
      return RestaurantCommandResult.Fail("El rendimiento y al menos un ingrediente son obligatorios.");
    }
    if (request.ProductMaterialId <= 0 || request.YieldUnitId <= 0 || request.Components.Any(component => component.MaterialId <= 0 || component.UnitId <= 0))
    {
      return RestaurantCommandResult.Fail("Selecciona el producto, los ingredientes y sus unidades.");
    }
    if (request.Components.Any(component => component.Quantity <= 0) ||
        request.Components.Select(component => component.MaterialId).Distinct().Count() != request.Components.Count)
    {
      return RestaurantCommandResult.Fail("Los ingredientes deben ser únicos y tener cantidades mayores que cero.");
    }
    if (request.Components.Any(component => component.MaterialId == request.ProductMaterialId))
    {
      return RestaurantCommandResult.Fail("El producto final no puede usarse como ingrediente de sí mismo.");
    }
    if (request.ExpectedWastePercent is < 0 or > 100 || request.Components.Any(component => component.ExpectedWastePercent is < 0 or > 100))
    {
      return RestaurantCommandResult.Fail("La merma debe estar entre 0 y 100 por ciento.");
    }
    if (request.Steps.Any(step => step.DurationMinutes < 0))
    {
      return RestaurantCommandResult.Fail("La duración de un paso no puede ser negativa.");
    }
    if (request.Steps.Any(step => step.Image?.Length > 5 * 1024 * 1024 ||
                                  (step.Image is not null && !(step.ImageContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false))))
    {
      return RestaurantCommandResult.Fail("Cada imagen de un paso debe ser una imagen válida de hasta 5 MB.");
    }

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var requestedMaterialIds = request.Components.Select(item => item.MaterialId).Append(request.ProductMaterialId).Distinct().ToArray();
      var materialBaseUnits = (await conn.QueryAsync<MaterialBaseUnitRow>(new CommandDefinition(
        "SELECT Id, BaseUnitId FROM logistica.Material WHERE Rfc = @Rfc AND Id IN @Ids;",
        new { Rfc = rfc, Ids = requestedMaterialIds }, tx, cancellationToken: ct)))
        .ToDictionary(item => item.Id, item => item.BaseUnitId);
      if (materialBaseUnits.Count != requestedMaterialIds.Length)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("Uno o más materiales no pertenecen al RFC seleccionado.");
      }
      if (request.YieldUnitId != materialBaseUnits[request.ProductMaterialId])
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("La unidad de rendimiento debe ser la unidad base del producto terminado.");
      }

      foreach (var component in request.Components)
      {
        if (component.UnitId != materialBaseUnits[component.MaterialId])
        {
          var hasConversion = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT CAST(CASE WHEN EXISTS
            (
              SELECT 1
              FROM logistica.MaterialUnitConversion conversionInfo
              WHERE conversionInfo.Rfc=@Rfc AND conversionInfo.MaterialId=@MaterialId
                AND conversionInfo.FromUnitId=@UnitId AND conversionInfo.ToUnitId=@BaseUnitId AND conversionInfo.IsActive=1
              UNION ALL
              SELECT 1
              FROM logistica.UnitConversion conversionInfo
              WHERE conversionInfo.FromUnitId=@UnitId AND conversionInfo.ToUnitId=@BaseUnitId AND conversionInfo.IsActive=1
            ) THEN 1 ELSE 0 END AS bit);
            """,
            new
            {
              Rfc = rfc,
              MaterialId = component.MaterialId,
              UnitId = component.UnitId,
              BaseUnitId = materialBaseUnits[component.MaterialId]
            },
            tx,
            cancellationToken: ct));
          if (!hasConversion)
          {
            await tx.RollbackAsync(ct);
            return RestaurantCommandResult.Fail($"La unidad elegida para el ingrediente {component.MaterialId} no tiene una conversión activa hacia su unidad base.");
          }
        }

        var createsCycle = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
          """
          WITH Descendants AS
          (
            SELECT @ComponentMaterialId AS MaterialId, 0 AS Depth
            UNION ALL
            SELECT bomComponent.ComponentMaterialId, descendants.Depth + 1
            FROM Descendants descendants
            JOIN logistica.BomHeader bomHeader ON bomHeader.Rfc = @Rfc AND bomHeader.ProductMaterialId = descendants.MaterialId
            JOIN logistica.BomVersion bomVersion ON bomVersion.Rfc = bomHeader.Rfc AND bomVersion.BomHeaderId = bomHeader.Id AND bomVersion.[Status] = 'Active'
            JOIN logistica.BomComponent bomComponent ON bomComponent.Rfc = bomVersion.Rfc AND bomComponent.BomVersionId = bomVersion.Id
            WHERE descendants.Depth < 31
          )
          SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM Descendants WHERE MaterialId = @ProductMaterialId) THEN 1 ELSE 0 END AS bit)
          OPTION (MAXRECURSION 32);
          """, new { Rfc = rfc, ComponentMaterialId = component.MaterialId, request.ProductMaterialId }, tx, cancellationToken: ct));
        if (createsCycle)
        {
          await tx.RollbackAsync(ct);
          return RestaurantCommandResult.Fail("La receta produciría un ciclo entre productos o subrecetas.");
        }
      }

      long headerId;
      long versionId;
      if (request.BomVersionId.HasValue)
      {
        var existing = await conn.QuerySingleOrDefaultAsync<VersionIdentityRow>(new CommandDefinition(
          """
          SELECT versionInfo.Id, versionInfo.BomHeaderId, versionInfo.[Status], headerInfo.ProductMaterialId
          FROM logistica.BomVersion versionInfo
          JOIN logistica.BomHeader headerInfo ON headerInfo.Rfc = versionInfo.Rfc AND headerInfo.Id = versionInfo.BomHeaderId
          WHERE versionInfo.Rfc = @Rfc AND versionInfo.Id = @Id;
          """, new { Rfc = rfc, Id = request.BomVersionId.Value }, tx, cancellationToken: ct));
        if (existing is null || existing.Status != "Draft" || existing.ProductMaterialId != request.ProductMaterialId)
        {
          await tx.RollbackAsync(ct);
          return RestaurantCommandResult.Fail("Sólo se pueden editar versiones en borrador del mismo producto.");
        }
        headerId = existing.BomHeaderId;
        versionId = existing.Id;
        await conn.ExecuteAsync(new CommandDefinition(
          """
          UPDATE logistica.BomVersion SET YieldQuantity = @YieldQuantity, YieldUnitId = @YieldUnitId,
              ExpectedWastePercent = @ExpectedWastePercent
          WHERE Rfc = @Rfc AND Id = @Id AND [Status] = 'Draft';
          DELETE FROM logistica.BomComponent WHERE Rfc = @Rfc AND BomVersionId = @Id;
          """, new { Rfc = rfc, Id = versionId, request.YieldQuantity, request.YieldUnitId, request.ExpectedWastePercent }, tx, cancellationToken: ct));
      }
      else
      {
        headerId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
          "SELECT Id FROM logistica.BomHeader WHERE Rfc = @Rfc AND ProductMaterialId = @ProductMaterialId;",
          new { Rfc = rfc, request.ProductMaterialId }, tx, cancellationToken: ct)) ?? 0;
        if (headerId == 0)
        {
          headerId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO logistica.BomHeader (Rfc, ProductMaterialId, BomCode, [Name])
            SELECT @Rfc, @ProductMaterialId,
                   CONCAT('BOM-', RIGHT(REPLICATE('0', 6) + CAST(@ProductMaterialId AS varchar(20)), 6)),
                   [Description]
            FROM logistica.Material WHERE Rfc = @Rfc AND Id = @ProductMaterialId;
            SELECT CAST(SCOPE_IDENTITY() AS bigint);
            """, new { Rfc = rfc, request.ProductMaterialId }, tx, cancellationToken: ct));
        }
        var existingDraft = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
          "SELECT TOP (1) Id FROM logistica.BomVersion WITH (UPDLOCK, HOLDLOCK) WHERE Rfc=@Rfc AND BomHeaderId=@HeaderId AND [Status]='Draft' ORDER BY VersionNumber DESC;",
          new { Rfc = rfc, HeaderId = headerId },
          tx,
          cancellationToken: ct));
        if (existingDraft.HasValue)
        {
          await tx.RollbackAsync(ct);
          return RestaurantCommandResult.Fail("Este producto ya tiene un borrador. Ábrelo para continuar en lugar de crear otra versión.");
        }
        versionId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
          """
          DECLARE @VersionNumber int = ISNULL((SELECT MAX(VersionNumber) FROM logistica.BomVersion WITH (UPDLOCK, HOLDLOCK)
                                               WHERE Rfc = @Rfc AND BomHeaderId = @HeaderId), 0) + 1;
          INSERT INTO logistica.BomVersion
            (Rfc, BomHeaderId, VersionNumber, [Status], YieldQuantity, YieldUnitId, ExpectedWastePercent, CreatedBy)
          VALUES
            (@Rfc, @HeaderId, @VersionNumber, 'Draft', @YieldQuantity, @YieldUnitId, @ExpectedWastePercent, @CreatedBy);
          SELECT CAST(SCOPE_IDENTITY() AS bigint);
          """, new { Rfc = rfc, HeaderId = headerId, request.YieldQuantity, request.YieldUnitId, request.ExpectedWastePercent, CreatedBy = NormalizeActor(userName) }, tx, cancellationToken: ct));
      }

      var sortOrder = 0;
      foreach (var component in request.Components)
      {
        await conn.ExecuteAsync(new CommandDefinition(
          """
          INSERT INTO logistica.BomComponent
            (Rfc, BomVersionId, ComponentMaterialId, Quantity, UnitId, ExpectedWastePercent, SortOrder)
          VALUES
            (@Rfc, @BomVersionId, @MaterialId, @Quantity, @UnitId, @ExpectedWastePercent, @SortOrder);
          """, new { Rfc = rfc, BomVersionId = versionId, component.MaterialId, component.Quantity, component.UnitId, component.ExpectedWastePercent, SortOrder = sortOrder++ }, tx, cancellationToken: ct));
      }

      var recipeId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
        "SELECT Id FROM logistica.Recipe WHERE Rfc = @Rfc AND BomVersionId = @BomVersionId;",
        new { Rfc = rfc, BomVersionId = versionId }, tx, cancellationToken: ct));
      if (!recipeId.HasValue)
      {
        recipeId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
          """
          INSERT INTO logistica.Recipe (Rfc, BomVersionId, [Name], SafetyNotes)
          SELECT @Rfc, @BomVersionId, material.[Description], @SafetyNotes
          FROM logistica.Material material WHERE material.Rfc = @Rfc AND material.Id = @ProductMaterialId;
          SELECT CAST(SCOPE_IDENTITY() AS bigint);
          """, new { Rfc = rfc, BomVersionId = versionId, request.ProductMaterialId, request.SafetyNotes }, tx, cancellationToken: ct));
      }
      else
      {
        await conn.ExecuteAsync(new CommandDefinition(
          "UPDATE logistica.Recipe SET SafetyNotes=@SafetyNotes WHERE Rfc=@Rfc AND Id=@RecipeId;",
          new { Rfc = rfc, RecipeId = recipeId.Value, request.SafetyNotes }, tx, cancellationToken: ct));
      }
      await conn.ExecuteAsync(new CommandDefinition(
        "DELETE FROM logistica.RecipeStep WHERE Rfc = @Rfc AND RecipeId = @RecipeId;",
        new { Rfc = rfc, RecipeId = recipeId.Value }, tx, cancellationToken: ct));
      foreach (var step in request.Steps.OrderBy(item => item.StepNumber))
      {
        await conn.ExecuteAsync(new CommandDefinition(
          """
          INSERT INTO logistica.RecipeStep
            (Rfc, RecipeId, StepNumber, Instruction, DurationMinutes, TemperatureC, Equipment, Image, ImageFileName, ImageContentType)
          VALUES
            (@Rfc, @RecipeId, @StepNumber, @Instruction, @DurationMinutes, @TemperatureC, @Equipment, @Image, @ImageFileName, @ImageContentType);
          """, new { Rfc = rfc, RecipeId = recipeId.Value, step.StepNumber, step.Instruction, step.DurationMinutes, step.TemperatureC, step.Equipment, step.Image, step.ImageFileName, step.ImageContentType }, tx, cancellationToken: ct));
      }

      var theoreticalCost = await CalculateTheoreticalCostAsync(conn, tx, rfc, versionId, ct);
      await conn.ExecuteAsync(new CommandDefinition(
        "UPDATE logistica.BomVersion SET FrozenTheoreticalCost = @Cost WHERE Rfc = @Rfc AND Id = @Id;",
        new { Rfc = rfc, Id = versionId, Cost = theoreticalCost }, tx, cancellationToken: ct));
      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok("El borrador de la receta fue guardado.", versionId);
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
      await tx.RollbackAsync(ct);
      return RestaurantCommandResult.Fail("Existe un ingrediente, paso o versión duplicada.");
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<RestaurantCommandResult> ActivateAsync(string rfc, long bomVersionId, string userName, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var version = await conn.QuerySingleOrDefaultAsync<VersionIdentityRow>(new CommandDefinition(
        """
        SELECT versionInfo.Id, versionInfo.BomHeaderId, versionInfo.[Status], headerInfo.ProductMaterialId
        FROM logistica.BomVersion versionInfo
        JOIN logistica.BomHeader headerInfo ON headerInfo.Rfc = versionInfo.Rfc AND headerInfo.Id = versionInfo.BomHeaderId
        WHERE versionInfo.Rfc = @Rfc AND versionInfo.Id = @Id;
        """, new { Rfc = normalizedRfc, Id = bomVersionId }, tx, cancellationToken: ct));
      if (version is null || version.Status != "Draft")
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("La versión no existe en el RFC o ya no está en borrador.");
      }
      var componentCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
        "SELECT COUNT(*) FROM logistica.BomComponent WHERE Rfc = @Rfc AND BomVersionId = @Id;",
        new { Rfc = normalizedRfc, Id = bomVersionId }, tx, cancellationToken: ct));
      if (componentCount == 0)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("Agrega al menos un ingrediente antes de activar la receta.");
      }
      var stepCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
        """
        SELECT COUNT(*)
        FROM logistica.Recipe recipe
        JOIN logistica.RecipeStep stepInfo ON stepInfo.Rfc=recipe.Rfc AND stepInfo.RecipeId=recipe.Id
        WHERE recipe.Rfc=@Rfc AND recipe.BomVersionId=@Id AND NULLIF(LTRIM(RTRIM(stepInfo.Instruction)), '') IS NOT NULL;
        """,
        new { Rfc = normalizedRfc, Id = bomVersionId },
        tx,
        cancellationToken: ct));
      if (stepCount == 0)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("Agrega al menos un paso con instrucciones antes de activar la receta.");
      }
      var invalidUnit = await FindInvalidComponentUnitAsync(conn, tx, normalizedRfc, bomVersionId, ct);
      if (invalidUnit is not null)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail($"La unidad de {invalidUnit.Description} ya no tiene una conversión activa hacia su unidad base.");
      }

      var yieldMismatch = await FindYieldUnitMismatchAsync(conn, tx, normalizedRfc, bomVersionId, ct);
      if (yieldMismatch is not null)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail(
          $"El rendimiento está en {yieldMismatch.YieldUnit} pero {yieldMismatch.Description} se inventaría en {yieldMismatch.BaseUnit}. Corrige el rendimiento antes de activar: de lo contrario el costo del lote completo se cargaría a una sola {yieldMismatch.BaseUnit}.");
      }

      var incompleteSubassembly = await FindIncompleteSubassemblyAsync(conn, tx, normalizedRfc, bomVersionId, version.ProductMaterialId, ct);
      if (incompleteSubassembly is not null)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail(
          $"El ingrediente {incompleteSubassembly.Description} (material {incompleteSubassembly.MaterialId}) está configurado como subreceta y no tiene una receta activa completa. Activa primero esa subreceta o cambia el material a inventario.");
      }

      await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE logistica.BomVersion
        SET [Status] = 'Retired', RetiredAt = SYSUTCDATETIME()
        WHERE Rfc = @Rfc AND BomHeaderId = @HeaderId AND [Status] = 'Active';
        UPDATE logistica.BomVersion
        SET [Status] = 'Active', EffectiveFrom = SYSUTCDATETIME(), RetiredAt = NULL,
            FrozenTheoreticalCost = @Cost
        WHERE Rfc = @Rfc AND Id = @Id AND [Status] = 'Draft';
        """, new
        {
          Rfc = normalizedRfc,
          HeaderId = version.BomHeaderId,
          Id = bomVersionId,
          Cost = await CalculateTheoreticalCostAsync(conn, tx, normalizedRfc, bomVersionId, ct)
        }, tx, cancellationToken: ct));

      // El costo congelado de una receta incluye el de sus subrecetas, así que al activar ésta
      // hay que rehacer el de todas las recetas que la consumen, de la más cercana a la más lejana.
      var recosted = await RecostAncestorsAsync(conn, tx, normalizedRfc, version.ProductMaterialId, ct);

      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok(
        recosted == 0
          ? "La receta quedó en uso; las versiones anteriores permanecen intactas."
          : $"La receta quedó en uso y se recalculó el costo de {recosted} receta{(recosted == 1 ? string.Empty : "s")} que la utiliza{(recosted == 1 ? string.Empty : "n")}.",
        bomVersionId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<RestaurantCommandResult> DeleteDraftAsync(string rfc, long bomVersionId, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var version = await conn.QuerySingleOrDefaultAsync<VersionIdentityRow>(new CommandDefinition(
        """
        SELECT versionInfo.Id, versionInfo.BomHeaderId, versionInfo.[Status], headerInfo.ProductMaterialId
        FROM logistica.BomVersion versionInfo WITH (UPDLOCK, HOLDLOCK)
        JOIN logistica.BomHeader headerInfo WITH (UPDLOCK, HOLDLOCK)
          ON headerInfo.Rfc = versionInfo.Rfc AND headerInfo.Id = versionInfo.BomHeaderId
        WHERE versionInfo.Rfc = @Rfc AND versionInfo.Id = @Id;
        """,
        new { Rfc = normalizedRfc, Id = bomVersionId },
        tx,
        cancellationToken: ct));
      if (version is null || !string.Equals(version.Status, "Draft", StringComparison.OrdinalIgnoreCase))
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("Solo se pueden eliminar versiones que continúan en borrador.");
      }

      var hasProduction = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
        "SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM logistica.ProductionOrder WHERE Rfc=@Rfc AND BomVersionId=@Id) THEN 1 ELSE 0 END AS bit);",
        new { Rfc = normalizedRfc, Id = bomVersionId },
        tx,
        cancellationToken: ct));
      if (hasProduction)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("El borrador ya está relacionado con producción y no se puede eliminar.");
      }

      await conn.ExecuteAsync(new CommandDefinition(
        """
        DELETE stepInfo
        FROM logistica.RecipeStep stepInfo
        JOIN logistica.Recipe recipeInfo
          ON recipeInfo.Rfc = stepInfo.Rfc AND recipeInfo.Id = stepInfo.RecipeId
        WHERE recipeInfo.Rfc = @Rfc AND recipeInfo.BomVersionId = @Id;

        DELETE FROM logistica.Recipe WHERE Rfc = @Rfc AND BomVersionId = @Id;
        DELETE FROM logistica.BomComponent WHERE Rfc = @Rfc AND BomVersionId = @Id;
        DELETE FROM logistica.BomVersion WHERE Rfc = @Rfc AND Id = @Id AND [Status] = 'Draft';

        IF NOT EXISTS (SELECT 1 FROM logistica.BomVersion WHERE Rfc = @Rfc AND BomHeaderId = @HeaderId)
          DELETE FROM logistica.BomHeader WHERE Rfc = @Rfc AND Id = @HeaderId;
        ELSE IF NOT EXISTS
        (
          SELECT 1 FROM logistica.BomVersion
          WHERE Rfc = @Rfc AND BomHeaderId = @HeaderId AND [Status] IN ('Draft', 'Active')
        )
          UPDATE logistica.BomHeader SET IsActive = 0 WHERE Rfc = @Rfc AND Id = @HeaderId;
        """,
        new { Rfc = normalizedRfc, Id = bomVersionId, HeaderId = version.BomHeaderId },
        tx,
        cancellationToken: ct));

      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok("El borrador de receta y sus datos no publicados fueron eliminados.", bomVersionId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<RestaurantCommandResult> RetireAsync(string rfc, long bomVersionId, string userName, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var version = await conn.QuerySingleOrDefaultAsync<VersionIdentityRow>(new CommandDefinition(
        """
        SELECT versionInfo.Id, versionInfo.BomHeaderId, versionInfo.[Status], headerInfo.ProductMaterialId
        FROM logistica.BomVersion versionInfo WITH (UPDLOCK, HOLDLOCK)
        JOIN logistica.BomHeader headerInfo WITH (UPDLOCK, HOLDLOCK)
          ON headerInfo.Rfc = versionInfo.Rfc AND headerInfo.Id = versionInfo.BomHeaderId
        WHERE versionInfo.Rfc = @Rfc AND versionInfo.Id = @Id;
        """,
        new { Rfc = normalizedRfc, Id = bomVersionId },
        tx,
        cancellationToken: ct));
      if (version is null || !string.Equals(version.Status, "Active", StringComparison.OrdinalIgnoreCase))
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("Solo se puede retirar una versión activa.");
      }

      var activeProduction = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
        """
        SELECT COUNT(*) FROM logistica.ProductionOrder WITH (UPDLOCK, HOLDLOCK)
        WHERE Rfc = @Rfc AND BomVersionId = @Id AND [Status] IN ('Planned', 'Started');
        """,
        new { Rfc = normalizedRfc, Id = bomVersionId },
        tx,
        cancellationToken: ct));
      if (activeProduction > 0)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("Completa o cancela la producción pendiente antes de archivar esta receta.");
      }

      var activeParent = await conn.QuerySingleOrDefaultAsync<ActiveParentBomRow>(new CommandDefinition(
        """
        SELECT TOP (1)
               parentHeader.ProductMaterialId,
               parentMaterial.[Description],
               childMaterial.FulfillmentMode
        FROM logistica.Material childMaterial WITH (UPDLOCK, HOLDLOCK)
        JOIN logistica.BomComponent component WITH (UPDLOCK, HOLDLOCK)
          ON component.Rfc = childMaterial.Rfc AND component.ComponentMaterialId = childMaterial.Id
        JOIN logistica.BomVersion parentVersion WITH (UPDLOCK, HOLDLOCK)
          ON parentVersion.Rfc = component.Rfc AND parentVersion.Id = component.BomVersionId
         AND parentVersion.[Status] = 'Active'
        JOIN logistica.BomHeader parentHeader WITH (UPDLOCK, HOLDLOCK)
          ON parentHeader.Rfc = parentVersion.Rfc AND parentHeader.Id = parentVersion.BomHeaderId
        JOIN logistica.Material parentMaterial WITH (UPDLOCK, HOLDLOCK)
          ON parentMaterial.Rfc = parentHeader.Rfc AND parentMaterial.Id = parentHeader.ProductMaterialId
        WHERE childMaterial.Rfc = @Rfc
          AND childMaterial.Id = @ProductMaterialId
          AND childMaterial.FulfillmentMode IN ('MakeToOrder', 'MakeToStock')
        ORDER BY parentHeader.ProductMaterialId;
        """,
        new { Rfc = normalizedRfc, version.ProductMaterialId },
        tx,
        cancellationToken: ct));
      // Una subreceta al momento se explota en cada venta: sin ella el padre deja de poder venderse.
      // Un subproducto por lote se descuenta del inventario, así que el padre sigue vendiéndose
      // mientras quede existencia; lo que se pierde es la forma de reponerlo.
      if (activeParent is not null && string.Equals(activeParent.FulfillmentMode, "MakeToOrder", StringComparison.OrdinalIgnoreCase))
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail(
          $"No se puede archivar esta receta porque el material todavía se usa como subreceta de {activeParent.Description} (material {activeParent.ProductMaterialId}). Reemplázalo en la receta relacionada antes de archivarla.");
      }

      var affected = await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE logistica.BomVersion
        SET [Status] = 'Retired', RetiredAt = SYSUTCDATETIME()
        WHERE Rfc = @Rfc AND Id = @Id AND [Status] = 'Active';

        UPDATE logistica.BomHeader
        SET IsActive = CASE WHEN EXISTS
        (
          SELECT 1 FROM logistica.BomVersion
          WHERE Rfc = @Rfc AND BomHeaderId = @HeaderId AND [Status] IN ('Draft', 'Active')
        ) THEN 1 ELSE 0 END
        WHERE Rfc = @Rfc AND Id = @HeaderId;
        """,
        new { Rfc = normalizedRfc, Id = bomVersionId, HeaderId = version.BomHeaderId },
        tx,
        cancellationToken: ct));
      if (affected == 0)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("La receta cambió mientras se archivaba. Actualiza la página.");
      }

      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok(
        activeParent is null
          ? $"La versión activa fue retirada por {NormalizeActor(userName)} y permanece en el historial."
          : $"La versión fue retirada por {NormalizeActor(userName)}, pero {activeParent.Description} todavía la consume: ese subproducto ya no se podrá producir y sólo se venderá mientras quede existencia.",
        bomVersionId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<IReadOnlyList<RestaurantAllergenDto>> GetAllergensAsync(string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    const string sql =
      """
      SELECT Id,Code,[Name],IsActive FROM logistica.Allergen ORDER BY [Name],Id;
      SELECT AllergenId,MaterialId FROM logistica.MaterialAllergen WHERE Rfc=@Rfc;
      """;
    using var conn = CreateConnection();
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, new { Rfc = normalizedRfc }, cancellationToken: ct));
    var allergens = (await multi.ReadAsync<RestaurantAllergenDto>()).AsList();
    var assignments = (await multi.ReadAsync<AllergenAssignmentRow>()).AsList();
    foreach (var allergen in allergens)
      allergen.MaterialIds = assignments.Where(row => row.AllergenId == allergen.Id).Select(row => row.MaterialId).ToArray();
    return allergens;
  }

  public async Task<RestaurantCommandResult> SaveAllergenAsync(RestaurantAllergenSaveRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var code = request.Code.Trim().ToUpperInvariant();
    var name = request.Name.Trim();
    if (code.Length == 0 || name.Length == 0) return RestaurantCommandResult.Fail("Código y nombre del alérgeno son obligatorios.");
    using var conn = CreateConnection();
    try
    {
      if (request.Id.HasValue)
      {
        var changed = await conn.ExecuteAsync(new CommandDefinition(
          "UPDATE logistica.Allergen SET Code=@Code,[Name]=@Name,IsActive=@IsActive WHERE Id=@Id;",
          new { request.Id, Code = code, Name = name, request.IsActive }, cancellationToken: ct));
        return changed == 1
          ? RestaurantCommandResult.Ok("Alérgeno actualizado.", request.Id.Value)
          : RestaurantCommandResult.Fail("El alérgeno no existe.");
      }
      var id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
        "INSERT INTO logistica.Allergen (Code,[Name],IsActive) VALUES (@Code,@Name,@IsActive); SELECT CAST(SCOPE_IDENTITY() AS int);",
        new { Code = code, Name = name, request.IsActive }, cancellationToken: ct));
      return RestaurantCommandResult.Ok("Alérgeno creado.", id);
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
      return RestaurantCommandResult.Fail("El código de alérgeno ya existe.");
    }
  }

  public async Task<RestaurantCommandResult> SaveMaterialAllergensAsync(
    string rfc,
    int materialId,
    IReadOnlyCollection<int> allergenIds,
    CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    if (materialId <= 0) return RestaurantCommandResult.Fail("Selecciona un material.");
    var ids = allergenIds.Distinct().ToArray();
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);
    try
    {
      var materialExists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
        "SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM logistica.Material WHERE Rfc=@Rfc AND Id=@MaterialId) THEN 1 ELSE 0 END AS bit);",
        new { Rfc = normalizedRfc, MaterialId = materialId }, tx, cancellationToken: ct));
      var validAllergens = ids.Length == 0 ? 0 : await conn.ExecuteScalarAsync<int>(new CommandDefinition(
        "SELECT COUNT(*) FROM logistica.Allergen WHERE Id IN @Ids AND IsActive=1;",
        new { Ids = ids }, tx, cancellationToken: ct));
      if (!materialExists || validAllergens != ids.Length)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("El material o uno de los alérgenos no es válido para el RFC actual.");
      }
      await conn.ExecuteAsync(new CommandDefinition(
        "DELETE FROM logistica.MaterialAllergen WHERE Rfc=@Rfc AND MaterialId=@MaterialId;",
        new { Rfc = normalizedRfc, MaterialId = materialId }, tx, cancellationToken: ct));
      foreach (var allergenId in ids)
        await conn.ExecuteAsync(new CommandDefinition(
          "INSERT INTO logistica.MaterialAllergen (Rfc,MaterialId,AllergenId) VALUES (@Rfc,@MaterialId,@AllergenId);",
          new { Rfc = normalizedRfc, MaterialId = materialId, AllergenId = allergenId }, tx, cancellationToken: ct));
      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok("Alérgenos del material actualizados.", materialId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<IReadOnlyList<MaterialUnitConversionDto>> GetMaterialUnitConversionsAsync(string rfc, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT conversionInfo.Id,conversionInfo.MaterialId,material.[Description] AS MaterialName,
             conversionInfo.FromUnitId,fromUnit.Abbreviation AS FromUnitCode,
             conversionInfo.ToUnitId,toUnit.Abbreviation AS ToUnitCode,
             conversionInfo.Factor,conversionInfo.Notes,conversionInfo.IsActive
      FROM logistica.MaterialUnitConversion conversionInfo
      JOIN logistica.Material material ON material.Rfc=conversionInfo.Rfc AND material.Id=conversionInfo.MaterialId
      JOIN logistica.UnitOfMeasure fromUnit ON fromUnit.Id=conversionInfo.FromUnitId
      JOIN logistica.UnitOfMeasure toUnit ON toUnit.Id=conversionInfo.ToUnitId
      WHERE conversionInfo.Rfc=@Rfc
      ORDER BY material.[Description],fromUnit.UnitName;
      """;
    using var conn = CreateConnection();
    return (await conn.QueryAsync<MaterialUnitConversionDto>(new CommandDefinition(
      sql, new { Rfc = LogisticsRfc.Require(rfc) }, cancellationToken: ct))).AsList();
  }

  public async Task<RestaurantCommandResult> SaveMaterialUnitConversionAsync(MaterialUnitConversionSaveRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    if (request.MaterialId <= 0 || request.FromUnitId <= 0 || request.ToUnitId <= 0 || request.Factor <= 0)
      return RestaurantCommandResult.Fail("Material, unidades y factor positivo son obligatorios.");
    using var conn = CreateConnection();
    var valid = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
      """
      SELECT CAST(CASE WHEN EXISTS
      (
        SELECT 1 FROM logistica.Material material
        WHERE material.Rfc=@Rfc AND material.Id=@MaterialId AND material.BaseUnitId=@ToUnitId
          AND EXISTS(SELECT 1 FROM logistica.UnitOfMeasure WHERE Id=@FromUnitId AND IsActive=1)
      ) THEN 1 ELSE 0 END AS bit);
      """, new { Rfc = rfc, request.MaterialId, request.FromUnitId, request.ToUnitId }, cancellationToken: ct));
    if (!valid) return RestaurantCommandResult.Fail("La unidad destino debe ser la unidad base del material y ambas deben estar activas.");
    try
    {
      if (request.Id.HasValue)
      {
        var changed = await conn.ExecuteAsync(new CommandDefinition(
          """
          UPDATE logistica.MaterialUnitConversion
          SET MaterialId=@MaterialId,FromUnitId=@FromUnitId,ToUnitId=@ToUnitId,Factor=@Factor,Notes=@Notes,IsActive=@IsActive
          WHERE Rfc=@Rfc AND Id=@Id;
          """, new { Rfc = rfc, request.Id, request.MaterialId, request.FromUnitId, request.ToUnitId, request.Factor, request.Notes, request.IsActive }, cancellationToken: ct));
        return changed == 1
          ? RestaurantCommandResult.Ok("Conversión actualizada.", request.Id.Value)
          : RestaurantCommandResult.Fail("La conversión no pertenece al RFC seleccionado.");
      }
      var id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
        """
        INSERT INTO logistica.MaterialUnitConversion (Rfc,MaterialId,FromUnitId,ToUnitId,Factor,Notes,IsActive)
        VALUES (@Rfc,@MaterialId,@FromUnitId,@ToUnitId,@Factor,@Notes,@IsActive);
        SELECT CAST(SCOPE_IDENTITY() AS int);
        """, new { Rfc = rfc, request.MaterialId, request.FromUnitId, request.ToUnitId, request.Factor, request.Notes, request.IsActive }, cancellationToken: ct));
      return RestaurantCommandResult.Ok("Conversión especial creada.", id);
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
      return RestaurantCommandResult.Fail("Ya existe una conversión para ese material y par de unidades.");
    }
  }

  public async Task<RestaurantCommandResult> DeleteMaterialUnitConversionAsync(string rfc, int conversionId, CancellationToken ct = default)
  {
    if (conversionId <= 0) return RestaurantCommandResult.Fail("Selecciona una conversión válida.");
    var normalizedRfc = LogisticsRfc.Require(rfc);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var conversion = await conn.QuerySingleOrDefaultAsync<MaterialConversionIdentityRow>(new CommandDefinition(
        """
        SELECT Id, MaterialId, FromUnitId, ToUnitId
        FROM logistica.MaterialUnitConversion WITH (UPDLOCK, HOLDLOCK)
        WHERE Rfc = @Rfc AND Id = @Id;
        """,
        new { Rfc = normalizedRfc, Id = conversionId },
        tx,
        cancellationToken: ct));
      if (conversion is null)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("La conversión no existe en el RFC seleccionado.");
      }

      var currentBomUses = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
        """
        SELECT COUNT(*)
        FROM logistica.BomComponent component
        JOIN logistica.BomVersion versionInfo
          ON versionInfo.Rfc = component.Rfc AND versionInfo.Id = component.BomVersionId
        WHERE component.Rfc = @Rfc
          AND component.ComponentMaterialId = @MaterialId
          AND component.UnitId = @FromUnitId
          AND versionInfo.[Status] IN ('Draft', 'Active');
        """,
        new { Rfc = normalizedRfc, conversion.MaterialId, conversion.FromUnitId },
        tx,
        cancellationToken: ct));
      if (currentBomUses > 0)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("La conversión se usa en una receta activa o en borrador. Retira primero esa configuración.");
      }

      var affected = await conn.ExecuteAsync(new CommandDefinition(
        "DELETE FROM logistica.MaterialUnitConversion WHERE Rfc = @Rfc AND Id = @Id;",
        new { Rfc = normalizedRfc, Id = conversionId },
        tx,
        cancellationToken: ct));
      if (affected != 1)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("La conversión cambió mientras se eliminaba. Actualiza la página.");
      }

      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok("Conversión especial eliminada.", conversionId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  private static async Task<InvalidComponentUnitRow?> FindInvalidComponentUnitAsync(
    DbConnection conn,
    DbTransaction? tx,
    string rfc,
    long versionId,
    CancellationToken ct)
    => await conn.QuerySingleOrDefaultAsync<InvalidComponentUnitRow>(new CommandDefinition(
      """
      SELECT TOP (1) material.Id AS MaterialId, material.[Description]
      FROM logistica.BomComponent component
      JOIN logistica.Material material ON material.Rfc=component.Rfc AND material.Id=component.ComponentMaterialId
      WHERE component.Rfc=@Rfc AND component.BomVersionId=@Id
        AND component.UnitId<>material.BaseUnitId
        AND NOT EXISTS
        (
          SELECT 1 FROM logistica.MaterialUnitConversion conversionInfo
          WHERE conversionInfo.Rfc=component.Rfc AND conversionInfo.MaterialId=component.ComponentMaterialId
            AND conversionInfo.FromUnitId=component.UnitId AND conversionInfo.ToUnitId=material.BaseUnitId AND conversionInfo.IsActive=1
          UNION ALL
          SELECT 1 FROM logistica.UnitConversion conversionInfo
          WHERE conversionInfo.FromUnitId=component.UnitId AND conversionInfo.ToUnitId=material.BaseUnitId AND conversionInfo.IsActive=1
        )
      ORDER BY component.SortOrder, component.Id;
      """,
      new { Rfc = rfc, Id = versionId },
      tx,
      cancellationToken: ct));

  private static async Task<IncompleteBomMaterialRow?> FindIncompleteSubassemblyAsync(
    DbConnection conn,
    DbTransaction? tx,
    string rfc,
    long versionId,
    int productMaterialId,
    CancellationToken ct)
    => await conn.QuerySingleOrDefaultAsync<IncompleteBomMaterialRow>(new CommandDefinition(
      """
      WITH MaterialTree AS
      (
        SELECT component.ComponentMaterialId AS MaterialId,
               1 AS Depth,
               CAST(CONCAT('/', @ProductMaterialId, '/', component.ComponentMaterialId, '/') AS varchar(max)) AS MaterialPath
        FROM logistica.BomComponent component
        WHERE component.Rfc = @Rfc AND component.BomVersionId = @Id

        UNION ALL

        SELECT childComponent.ComponentMaterialId,
               tree.Depth + 1,
               CAST(CONCAT(tree.MaterialPath, childComponent.ComponentMaterialId, '/') AS varchar(max))
        FROM MaterialTree tree
        JOIN logistica.Material treeMaterial ON treeMaterial.Rfc = @Rfc AND treeMaterial.Id = tree.MaterialId
        JOIN logistica.BomHeader childHeader ON childHeader.Rfc = treeMaterial.Rfc AND childHeader.ProductMaterialId = treeMaterial.Id
        JOIN logistica.BomVersion childVersion ON childVersion.Rfc = childHeader.Rfc AND childVersion.BomHeaderId = childHeader.Id AND childVersion.[Status] = 'Active'
        JOIN logistica.BomComponent childComponent ON childComponent.Rfc = childVersion.Rfc AND childComponent.BomVersionId = childVersion.Id
        WHERE treeMaterial.FulfillmentMode = 'MakeToOrder'
          AND tree.Depth < 31
          AND tree.MaterialPath NOT LIKE CONCAT('%/', childComponent.ComponentMaterialId, '/%')
      )
      SELECT TOP (1) tree.MaterialId, material.[Description]
      FROM MaterialTree tree
      JOIN logistica.Material material ON material.Rfc = @Rfc AND material.Id = tree.MaterialId
      WHERE material.FulfillmentMode = 'MakeToOrder'
        AND NOT EXISTS
        (
          SELECT 1
          FROM logistica.BomHeader requiredHeader
          JOIN logistica.BomVersion requiredVersion ON requiredVersion.Rfc = requiredHeader.Rfc AND requiredVersion.BomHeaderId = requiredHeader.Id AND requiredVersion.[Status] = 'Active'
          JOIN logistica.BomComponent requiredComponent ON requiredComponent.Rfc = requiredVersion.Rfc AND requiredComponent.BomVersionId = requiredVersion.Id
          WHERE requiredHeader.Rfc = material.Rfc AND requiredHeader.ProductMaterialId = material.Id
        )
      ORDER BY tree.Depth, tree.MaterialId
      OPTION (MAXRECURSION 32);
      """,
      new { Rfc = rfc, Id = versionId, ProductMaterialId = productMaterialId },
      tx,
      cancellationToken: ct));

  /// <summary>
  /// Recalcula el costo congelado de cada receta activa que consume, directa o indirectamente,
  /// al material indicado. Se procesa de la más cercana a la más lejana para que cada nivel use
  /// el costo ya actualizado del nivel de abajo. Devuelve cuántas recetas se recalcularon.
  /// </summary>
  /// <summary>
  /// Una receta cuyo rendimiento no está en la unidad base de su material carga el costo de todo
  /// el lote sobre una sola unidad base, e infla el costo de cuanto platillo la use.
  /// </summary>
  /// <summary>
  /// Ingredientes que se producen por lote pero todavía no tienen receta activa. No impiden
  /// activar ni vender —se descuentan del inventario existente— pero no hay forma de reponerlos
  /// con una orden de producción.
  /// </summary>
  private static async Task<IReadOnlyList<string>> FindUnproducibleBatchSubProductsAsync(
    DbConnection conn, DbTransaction? tx, string rfc, long versionId, CancellationToken ct)
    => (await conn.QueryAsync<string>(new CommandDefinition(
      """
      SELECT DISTINCT material.[Description]
      FROM logistica.BomComponent component
      JOIN logistica.Material material
        ON material.Rfc = component.Rfc AND material.Id = component.ComponentMaterialId
      WHERE component.Rfc = @Rfc AND component.BomVersionId = @Id
        AND material.FulfillmentMode = 'MakeToStock'
        AND NOT EXISTS
        (
          SELECT 1
          FROM logistica.BomHeader requiredHeader
          JOIN logistica.BomVersion requiredVersion
            ON requiredVersion.Rfc = requiredHeader.Rfc AND requiredVersion.BomHeaderId = requiredHeader.Id
           AND requiredVersion.[Status] = 'Active'
          WHERE requiredHeader.Rfc = material.Rfc AND requiredHeader.ProductMaterialId = material.Id
        )
      ORDER BY material.[Description];
      """,
      new { Rfc = rfc, Id = versionId },
      tx,
      cancellationToken: ct))).AsList();

  private static async Task<YieldUnitMismatchRow?> FindYieldUnitMismatchAsync(
    DbConnection conn, DbTransaction? tx, string rfc, long versionId, CancellationToken ct)
    => await conn.QuerySingleOrDefaultAsync<YieldUnitMismatchRow>(new CommandDefinition(
      """
      SELECT TOP (1) material.[Description],
             COALESCE(NULLIF(yieldUnit.Abbreviation, ''), yieldUnit.UnitName) AS YieldUnit,
             COALESCE(NULLIF(baseUnit.Abbreviation, ''), baseUnit.UnitName) AS BaseUnit
      FROM logistica.BomVersion versionInfo
      JOIN logistica.BomHeader headerInfo
        ON headerInfo.Rfc = versionInfo.Rfc AND headerInfo.Id = versionInfo.BomHeaderId
      JOIN logistica.Material material
        ON material.Rfc = headerInfo.Rfc AND material.Id = headerInfo.ProductMaterialId
      JOIN logistica.UnitOfMeasure yieldUnit ON yieldUnit.Id = versionInfo.YieldUnitId
      JOIN logistica.UnitOfMeasure baseUnit ON baseUnit.Id = material.BaseUnitId
      WHERE versionInfo.Rfc = @Rfc AND versionInfo.Id = @Id
        AND versionInfo.YieldUnitId <> material.BaseUnitId;
      """,
      new { Rfc = rfc, Id = versionId },
      tx,
      cancellationToken: ct));

  private static async Task<int> RecostAncestorsAsync(
    DbConnection conn, DbTransaction tx, string rfc, int productMaterialId, CancellationToken ct)
  {
    var ancestors = (await conn.QueryAsync<AncestorVersionRow>(new CommandDefinition(
      """
      WITH Ancestors AS
      (
        SELECT parentHeader.ProductMaterialId AS MaterialId, parentVersion.Id AS BomVersionId, 1 AS Depth
        FROM logistica.BomComponent component
        JOIN logistica.BomVersion parentVersion
          ON parentVersion.Rfc = component.Rfc AND parentVersion.Id = component.BomVersionId AND parentVersion.[Status] = 'Active'
        JOIN logistica.BomHeader parentHeader
          ON parentHeader.Rfc = parentVersion.Rfc AND parentHeader.Id = parentVersion.BomHeaderId
        WHERE component.Rfc = @Rfc AND component.ComponentMaterialId = @MaterialId

        UNION ALL

        SELECT parentHeader.ProductMaterialId, parentVersion.Id, ancestor.Depth + 1
        FROM Ancestors ancestor
        JOIN logistica.BomComponent component
          ON component.Rfc = @Rfc AND component.ComponentMaterialId = ancestor.MaterialId
        JOIN logistica.BomVersion parentVersion
          ON parentVersion.Rfc = component.Rfc AND parentVersion.Id = component.BomVersionId AND parentVersion.[Status] = 'Active'
        JOIN logistica.BomHeader parentHeader
          ON parentHeader.Rfc = parentVersion.Rfc AND parentHeader.Id = parentVersion.BomHeaderId
        WHERE ancestor.Depth < 31 AND parentHeader.ProductMaterialId <> @MaterialId
      )
      SELECT BomVersionId, MIN(Depth) AS Depth
      FROM Ancestors
      GROUP BY BomVersionId
      ORDER BY Depth, BomVersionId
      OPTION (MAXRECURSION 32);
      """,
      new { Rfc = rfc, MaterialId = productMaterialId },
      tx,
      cancellationToken: ct))).AsList();

    foreach (var ancestor in ancestors)
    {
      await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE logistica.BomVersion
        SET FrozenTheoreticalCost = @Cost
        WHERE Rfc = @Rfc AND Id = @Id AND [Status] = 'Active';
        """,
        new
        {
          Rfc = rfc,
          Id = ancestor.BomVersionId,
          Cost = await CalculateTheoreticalCostAsync(conn, tx, rfc, ancestor.BomVersionId, ct)
        },
        tx,
        cancellationToken: ct));
    }

    return ancestors.Count;
  }

  private static async Task<decimal> CalculateTheoreticalCostAsync(DbConnection conn, DbTransaction tx, string rfc, long versionId, CancellationToken ct)
  {
    const string sql =
      """
      SELECT CAST(ISNULL(SUM(
        component.Quantity
        * (1 + component.ExpectedWastePercent / 100.0)
        * COALESCE(materialConversion.Factor, globalConversion.Factor, CASE WHEN component.UnitId = material.BaseUnitId THEN 1 END)
        * COALESCE(subBom.FrozenTheoreticalCost / NULLIF(subBom.YieldQuantity, 0), material.BaseUnitPrice, 0)
      ), 0) / NULLIF(versionInfo.YieldQuantity, 0) AS decimal(18,6))
      FROM logistica.BomVersion versionInfo
      JOIN logistica.BomComponent component ON component.Rfc = versionInfo.Rfc AND component.BomVersionId = versionInfo.Id
      JOIN logistica.Material material ON material.Rfc = component.Rfc AND material.Id = component.ComponentMaterialId
      OUTER APPLY
      (
        SELECT TOP (1) conversionInfo.Factor
        FROM logistica.MaterialUnitConversion conversionInfo
        WHERE conversionInfo.Rfc = material.Rfc AND conversionInfo.MaterialId = material.Id
          AND conversionInfo.FromUnitId = component.UnitId AND conversionInfo.ToUnitId = material.BaseUnitId AND conversionInfo.IsActive = 1
      ) materialConversion
      OUTER APPLY
      (
        SELECT TOP (1) conversionInfo.Factor
        FROM logistica.UnitConversion conversionInfo
        WHERE conversionInfo.FromUnitId = component.UnitId AND conversionInfo.ToUnitId = material.BaseUnitId AND conversionInfo.IsActive = 1
      ) globalConversion
      OUTER APPLY
      (
        SELECT TOP (1) childVersion.FrozenTheoreticalCost, childVersion.YieldQuantity
        FROM logistica.BomHeader childHeader
        JOIN logistica.BomVersion childVersion ON childVersion.Rfc = childHeader.Rfc AND childVersion.BomHeaderId = childHeader.Id
        WHERE childHeader.Rfc = material.Rfc AND childHeader.ProductMaterialId = material.Id AND childVersion.[Status] = 'Active'
          AND material.FulfillmentMode IN ('MakeToStock', 'MakeToOrder')
      ) subBom
      WHERE versionInfo.Rfc = @Rfc AND versionInfo.Id = @VersionId
      GROUP BY versionInfo.YieldQuantity;
      """;
    return await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(sql, new { Rfc = rfc, VersionId = versionId }, tx, cancellationToken: ct));
  }

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica de conexiones no devolvió una DbConnection.");

  private static string NormalizeActor(string? value)
    => string.IsNullOrWhiteSpace(value) ? "OrionERP" : value.Trim();

  private sealed class YieldUnitMismatchRow
  {
    public string Description { get; set; } = string.Empty;
    public string YieldUnit { get; set; } = string.Empty;
    public string BaseUnit { get; set; } = string.Empty;
  }

  private sealed class AncestorVersionRow
  {
    public long BomVersionId { get; set; }
    public int Depth { get; set; }
  }

  private sealed class VersionIdentityRow
  {
    public long Id { get; set; }
    public long BomHeaderId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int ProductMaterialId { get; set; }
  }
  private sealed class CostBreakdownVersionRow
  {
    public long BomVersionId { get; set; }
    public decimal YieldQuantity { get; set; }
    public int YieldUnitId { get; set; }
    public string YieldUnitName { get; set; } = string.Empty;
    public decimal StoredUnitCost { get; set; }
  }
  private sealed class CostBreakdownLineRow
  {
    public int MaterialId { get; set; }
    public string MaterialName { get; set; } = string.Empty;
    public decimal RecipeQuantity { get; set; }
    public string RecipeUnitName { get; set; } = string.Empty;
    public decimal WastePercent { get; set; }
    public decimal ConversionFactor { get; set; }
    public string BaseUnitName { get; set; } = string.Empty;
    public decimal UnitCost { get; set; }
    public string CostSource { get; set; } = string.Empty;
    public bool RecipeCostIgnored { get; set; }
  }
  private sealed class IncompleteBomMaterialRow
  {
    public int MaterialId { get; set; }
    public string Description { get; set; } = string.Empty;
  }
  private sealed class InvalidComponentUnitRow
  {
    public int MaterialId { get; set; }
    public string Description { get; set; } = string.Empty;
  }
  private sealed class ActiveParentBomRow
  {
    public string FulfillmentMode { get; set; } = string.Empty;
    public int ProductMaterialId { get; set; }
    public string Description { get; set; } = string.Empty;
  }

  private sealed class MaterialBaseUnitRow
  {
    public int Id { get; set; }
    public int BaseUnitId { get; set; }
  }

  private sealed class MaterialConversionIdentityRow
  {
    public int Id { get; set; }
    public int MaterialId { get; set; }
    public int FromUnitId { get; set; }
    public int ToUnitId { get; set; }
  }

  private sealed class AllergenAssignmentRow
  {
    public int AllergenId { get; set; }
    public int MaterialId { get; set; }
  }
}
