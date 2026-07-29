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
             recipe.SafetyNotes
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
             recipe.SafetyNotes
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
      """;
    using var conn = CreateConnection();
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, new { Rfc = normalizedRfc, BomVersionId = bomVersionId }, cancellationToken: ct));
    var version = await multi.ReadSingleOrDefaultAsync<BomVersionDto>();
    var components = (await multi.ReadAsync<BomComponentDto>()).AsList();
    var steps = (await multi.ReadAsync<RecipeStepDto>()).AsList();
    if (version is null)
    {
      return null;
    }
    version.Components = components;
    version.Steps = steps;
    return version;
  }

  public async Task<RestaurantCommandResult> SaveDraftAsync(BomDraftSaveRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    if (request.YieldQuantity <= 0 || request.Components.Count == 0)
    {
      return RestaurantCommandResult.Fail("El rendimiento y al menos un componente son obligatorios.");
    }
    if (request.ProductMaterialId <= 0 || request.YieldUnitId <= 0 || request.Components.Any(component => component.MaterialId <= 0 || component.UnitId <= 0))
    {
      return RestaurantCommandResult.Fail("Selecciona el producto, los ingredientes y sus unidades base.");
    }
    if (request.Components.Any(component => component.Quantity <= 0) ||
        request.Components.Select(component => component.MaterialId).Distinct().Count() != request.Components.Count)
    {
      return RestaurantCommandResult.Fail("Los componentes deben ser únicos y tener cantidades mayores que cero.");
    }
    if (request.Components.Any(component => component.MaterialId == request.ProductMaterialId))
    {
      return RestaurantCommandResult.Fail("Un producto no puede ser componente directo de sí mismo.");
    }
    if (request.Steps.Any(step => step.Image?.Length > 5 * 1024 * 1024 ||
                                  (step.Image is not null && !(step.ImageContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false))))
    {
      return RestaurantCommandResult.Fail("Cada imagen de un paso debe ser una imagen válida de hasta 5 MB.");
    }

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);
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
          await tx.RollbackAsync(ct);
          return RestaurantCommandResult.Fail($"La unidad del ingrediente {component.MaterialId} debe ser su unidad base.");
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
          return RestaurantCommandResult.Fail("El BOM produciría un ciclo entre productos o subrecetas.");
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
        versionId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
          """
          DECLARE @VersionNumber int = ISNULL((SELECT MAX(VersionNumber) FROM logistica.BomVersion WITH (UPDLOCK, HOLDLOCK)
                                               WHERE Rfc = @Rfc AND BomHeaderId = @HeaderId), 0) + 1;
          INSERT INTO logistica.BomVersion
            (Rfc, BomHeaderId, VersionNumber, [Status], YieldQuantity, YieldUnitId, ExpectedWastePercent)
          VALUES
            (@Rfc, @HeaderId, @VersionNumber, 'Draft', @YieldQuantity, @YieldUnitId, @ExpectedWastePercent);
          SELECT CAST(SCOPE_IDENTITY() AS bigint);
          """, new { Rfc = rfc, HeaderId = headerId, request.YieldQuantity, request.YieldUnitId, request.ExpectedWastePercent }, tx, cancellationToken: ct));
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
      return RestaurantCommandResult.Ok("El borrador de BOM y receta fue guardado.", versionId);
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
      await tx.RollbackAsync(ct);
      return RestaurantCommandResult.Fail("Existe un componente, paso o versión duplicada.");
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
    await using var tx = await conn.BeginTransactionAsync(ct);
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
        return RestaurantCommandResult.Fail("No se puede activar un BOM sin componentes.");
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
      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok("La versión fue activada; las versiones usadas previamente permanecen intactas.", bomVersionId);
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

  private static async Task<decimal> CalculateTheoreticalCostAsync(DbConnection conn, DbTransaction tx, string rfc, long versionId, CancellationToken ct)
  {
    const string sql =
      """
      SELECT CAST(ISNULL(SUM(
        component.Quantity
        * (1 + component.ExpectedWastePercent / 100.0)
        * COALESCE(materialConversion.Factor, globalConversion.Factor, CASE WHEN component.UnitId = material.BaseUnitId THEN 1 END)
        * COALESCE(subBom.FrozenTheoreticalCost / NULLIF(subBom.YieldQuantity, 0), stockCost.AverageUnitCost, material.Price, 0)
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
        SELECT SUM(balance.Quantity * balance.AverageUnitCost) / NULLIF(SUM(balance.Quantity), 0) AS AverageUnitCost
        FROM logistica.StockBalance balance
        WHERE balance.Rfc = material.Rfc AND balance.MaterialId = material.Id AND balance.Quantity > 0 AND balance.IsRemoved = 0
      ) stockCost
      OUTER APPLY
      (
        SELECT TOP (1) childVersion.FrozenTheoreticalCost, childVersion.YieldQuantity
        FROM logistica.BomHeader childHeader
        JOIN logistica.BomVersion childVersion ON childVersion.Rfc = childHeader.Rfc AND childVersion.BomHeaderId = childHeader.Id
        WHERE childHeader.Rfc = material.Rfc AND childHeader.ProductMaterialId = material.Id AND childVersion.[Status] = 'Active'
      ) subBom
      WHERE versionInfo.Rfc = @Rfc AND versionInfo.Id = @VersionId
      GROUP BY versionInfo.YieldQuantity;
      """;
    return await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(sql, new { Rfc = rfc, VersionId = versionId }, tx, cancellationToken: ct));
  }

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica de conexiones no devolvió una DbConnection.");

  private sealed class VersionIdentityRow
  {
    public long Id { get; set; }
    public long BomHeaderId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int ProductMaterialId { get; set; }
  }

  private sealed class MaterialBaseUnitRow
  {
    public int Id { get; set; }
    public int BaseUnitId { get; set; }
  }

  private sealed class AllergenAssignmentRow
  {
    public int AllergenId { get; set; }
    public int MaterialId { get; set; }
  }
}
