using System.Data.Common;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

public sealed class RestaurantSaleReadinessService : IRestaurantSaleReadinessService
{
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly IRestaurantCatalogService _catalogService;

  public RestaurantSaleReadinessService(
    IDbConnectionFactory connectionFactory,
    IRestaurantCatalogService catalogService)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
  }

  public async Task<RestaurantSaleReadinessReport> AnalyzeAsync(
    string rfc,
    int siteId,
    DateTimeOffset at,
    CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    var generatedAtUtc = at.ToUniversalTime();
    var catalog = await _catalogService.GetPosCatalogAsync(normalizedRfc, siteId, generatedAtUtc, ct);
    var timeZone = TimeZoneInfo.FindSystemTimeZoneById(catalog.Site.TimeZoneId);
    var generatedAtLocal = TimeZoneInfo.ConvertTime(generatedAtUtc, timeZone);
    var menuProducts = catalog.Sections
      .SelectMany(section => section.Products.Select(product => new { Section = section.Name, Product = product }))
      .GroupBy(item => item.Product.Id)
      .Select(group => new MenuProduct(
        group.First().Product,
        string.Join(", ", group.Select(item => item.Section).Distinct(StringComparer.OrdinalIgnoreCase))))
      .OrderBy(item => item.Sections, StringComparer.OrdinalIgnoreCase)
      .ThenBy(item => ProductDisplayName(item.Product), StringComparer.OrdinalIgnoreCase)
      .ToList();

    var optionIds = menuProducts.SelectMany(item => item.Product.ModifierGroups)
      .SelectMany(group => group.Options)
      .Select(option => option.Id)
      .Distinct()
      .ToArray();

    using var connection = CreateConnection();
    await connection.OpenAsync(ct);
    var graph = await RestaurantRequirementGraphLoader.LoadAsync(connection, null, normalizedRfc, optionIds, ct);
    var context = await LoadOperationalContextAsync(connection, normalizedRfc, siteId, ct);
    var fallbackLocationExists = context.Locations.Any(location => location.IsActive && location.IsInventoryEnabled);

    var productRows = new List<RestaurantSaleReadinessProduct>();
    var ingredientRows = new List<RestaurantSaleReadinessIngredient>();
    var bomRows = new List<RestaurantSaleReadinessBomRow>();
    var modifierRows = new List<RestaurantSaleReadinessModifierRow>();
    var actions = new List<RestaurantSaleReadinessAction>();

    foreach (var menuProduct in menuProducts)
    {
      var product = menuProduct.Product;
      var calculation = RestaurantSaleRequirementCalculator.Calculate(graph, product.MaterialId, product.Sku, 1m);
      AppendBomRows(bomRows, graph, product, calculation);

      var productIngredients = new List<RestaurantSaleReadinessIngredient>();
      foreach (var requirement in calculation.Requirements.OrderBy(item => MaterialSortKey(graph, item.Key)))
      {
        if (!graph.Materials.TryGetValue(requirement.Key, out var material)) continue;
        var inventory = EvaluateInventory(
          material,
          requirement.Value,
          context.StockBalances,
          context.LotBalances,
          generatedAtUtc,
          catalog.Site.AllowSupervisorDeficit,
          fallbackLocationExists);
        var ingredient = new RestaurantSaleReadinessIngredient
        {
          ProductId = product.Id,
          ProductSku = product.Sku,
          ProductName = ProductDisplayName(product),
          MaterialId = material.Id,
          MaterialCode = material.Code,
          MaterialName = material.Name,
          BaseUnit = material.BaseUnit,
          BomPath = calculation.RequirementPaths.GetValueOrDefault(material.Id, MaterialLabel(material)),
          BomDepth = calculation.RequirementDepths.GetValueOrDefault(material.Id),
          TrackLots = material.TrackLots,
          RequiredQuantity = RoundQuantity(requirement.Value),
          StockQuantity = inventory.StockQuantity,
          ReservedQuantity = inventory.ReservedQuantity,
          UsableQuantity = inventory.UsableQuantity,
          ExcludedLotQuantity = inventory.ExcludedLotQuantity,
          ProjectedUsableQuantity = inventory.ProjectedUsableQuantity,
          MinimumQuantity = inventory.MinimumQuantity,
          ShortageQuantity = inventory.ShortageQuantity,
          EstimatedSellableUnits = inventory.EstimatedSellableUnits,
          LocationSummary = inventory.LocationSummary,
          Status = inventory.Status,
          PredictedPosMessage = inventory.Message
        };
        productIngredients.Add(ingredient);
        ingredientRows.Add(ingredient);
        if (!string.Equals(ingredient.Status, RestaurantSaleReadinessStatuses.Ready, StringComparison.Ordinal))
        {
          actions.Add(new RestaurantSaleReadinessAction
          {
            Severity = ingredient.Status == RestaurantSaleReadinessStatuses.Warning
              ? RestaurantSaleReadinessSeverities.Warning
              : RestaurantSaleReadinessSeverities.Error,
            ProductSku = product.Sku,
            ProductName = ProductDisplayName(product),
            MaterialId = material.Id,
            Material = $"{material.Code} · {material.Name}",
            Issue = ingredient.PredictedPosMessage ?? ingredient.Status,
            ShortageQuantity = ingredient.ShortageQuantity > 0 ? ingredient.ShortageQuantity : null,
            RecommendedAction = InventoryAction(ingredient)
          });
        }
      }

      foreach (var issue in calculation.Issues)
      {
        var issueMaterial = issue.MaterialId.HasValue && graph.Materials.TryGetValue(issue.MaterialId.Value, out var found)
          ? found
          : null;
        actions.Add(new RestaurantSaleReadinessAction
        {
          Severity = RestaurantSaleReadinessSeverities.Error,
          ProductSku = product.Sku,
          ProductName = ProductDisplayName(product),
          MaterialId = issue.MaterialId,
          Material = issueMaterial is null ? string.Empty : $"{issueMaterial.Code} · {issueMaterial.Name}",
          Issue = issue.Message,
          RecommendedAction = ConfigurationAction(issue.Code)
        });
      }

      var modifierEvaluation = EvaluateModifiers(
        graph,
        product,
        calculation,
        context,
        generatedAtUtc,
        catalog.Site.AllowSupervisorDeficit,
        fallbackLocationExists);
      modifierRows.AddRange(modifierEvaluation.Rows);
      actions.AddRange(modifierEvaluation.Actions);

      var hardInventory = productIngredients.Where(ingredient => ingredient.Status is RestaurantSaleReadinessStatuses.InventoryBlocked).ToList();
      var supervisorInventory = productIngredients.Where(ingredient => ingredient.Status is RestaurantSaleReadinessStatuses.SupervisorRequired).ToList();
      var warnings = productIngredients.Count(ingredient => ingredient.Status == RestaurantSaleReadinessStatuses.Warning)
        + modifierEvaluation.WarningCount;
      var errors = calculation.Issues.Count + hardInventory.Count + supervisorInventory.Count + modifierEvaluation.ErrorCount;
      var requiresPreparationStation = string.Equals(
        product.FulfillmentMode,
        "MakeToOrder",
        StringComparison.OrdinalIgnoreCase);

      string status;
      string? predictedMessage;
      if (product.IsSoldOut)
      {
        status = RestaurantSaleReadinessStatuses.SoldOut;
        predictedMessage = "Uno o más productos están inactivos, agotados o pertenecen a otro RFC.";
        actions.Add(new RestaurantSaleReadinessAction
        {
          Severity = RestaurantSaleReadinessSeverities.Error,
          ProductSku = product.Sku,
          ProductName = ProductDisplayName(product),
          MaterialId = product.MaterialId,
          Material = graph.Materials.TryGetValue(product.MaterialId, out var soldOutMaterial)
            ? $"{soldOutMaterial.Code} · {soldOutMaterial.Name}"
            : string.Empty,
          Issue = predictedMessage,
          RecommendedAction = "Confirma si el producto debe permanecer agotado; de lo contrario, retira la marca de agotado."
        });
      }
      else if (calculation.Issues.Count > 0 || modifierEvaluation.ConfigurationBlocked)
      {
        status = RestaurantSaleReadinessStatuses.ConfigurationBlocked;
        predictedMessage = calculation.Issues.FirstOrDefault()?.Message ?? modifierEvaluation.FirstBlockingMessage;
      }
      else if (hardInventory.Count > 0 || modifierEvaluation.InventoryBlocked)
      {
        status = RestaurantSaleReadinessStatuses.InventoryBlocked;
        predictedMessage = hardInventory.FirstOrDefault()?.PredictedPosMessage ?? modifierEvaluation.FirstBlockingMessage;
      }
      else if (supervisorInventory.Count > 0 || modifierEvaluation.SupervisorRequired)
      {
        status = RestaurantSaleReadinessStatuses.SupervisorRequired;
        predictedMessage = supervisorInventory.FirstOrDefault()?.PredictedPosMessage ?? modifierEvaluation.FirstBlockingMessage;
      }
      else if (warnings > 0 || product.Price <= 0 || (requiresPreparationStation && !product.KitchenStationId.HasValue))
      {
        status = RestaurantSaleReadinessStatuses.Warning;
        predictedMessage = productIngredients.FirstOrDefault(ingredient => ingredient.Status == RestaurantSaleReadinessStatuses.Warning)?.PredictedPosMessage
          ?? (product.Price <= 0 ? "El producto tiene precio cero." : "El producto no tiene estación de cocina asignada.");
      }
      else
      {
        status = RestaurantSaleReadinessStatuses.Ready;
        predictedMessage = "La venta simulada de una unidad no encontró bloqueos.";
      }

      if (product.Price <= 0)
      {
        warnings++;
        actions.Add(new RestaurantSaleReadinessAction
        {
          Severity = RestaurantSaleReadinessSeverities.Warning,
          ProductSku = product.Sku,
          ProductName = ProductDisplayName(product),
          MaterialId = product.MaterialId,
          Issue = "El producto tiene precio cero.",
          RecommendedAction = "Confirma el precio antes de ofrecer el producto."
        });
      }
      if (requiresPreparationStation && !product.KitchenStationId.HasValue)
      {
        warnings++;
        actions.Add(new RestaurantSaleReadinessAction
        {
          Severity = RestaurantSaleReadinessSeverities.Warning,
          ProductSku = product.Sku,
          ProductName = ProductDisplayName(product),
          MaterialId = product.MaterialId,
          Issue = "El producto no tiene estación de cocina asignada.",
          RecommendedAction = "Asigna una estación si el producto requiere preparación."
        });
      }

      var bottleneck = productIngredients
        .Where(ingredient => ingredient.EstimatedSellableUnits.HasValue)
        .OrderBy(ingredient => ingredient.EstimatedSellableUnits)
        .ThenBy(ingredient => ingredient.MaterialCode, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();
      var rootMaterial = graph.Materials.GetValueOrDefault(product.MaterialId);
      productRows.Add(new RestaurantSaleReadinessProduct
      {
        ProductId = product.Id,
        Sku = product.Sku,
        ProductName = ProductDisplayName(product),
        Sections = menuProduct.Sections,
        Price = product.Price,
        MaterialId = product.MaterialId,
        MaterialCode = rootMaterial?.Code ?? string.Empty,
        MaterialName = rootMaterial?.Name ?? string.Empty,
        FulfillmentMode = product.FulfillmentMode,
        KitchenStationName = product.KitchenStationName,
        IsActive = product.IsActive,
        IsSoldOut = product.IsSoldOut,
        Status = status,
        CanSellWithoutOverride = status is RestaurantSaleReadinessStatuses.Ready or RestaurantSaleReadinessStatuses.Warning,
        RequiresSupervisor = status == RestaurantSaleReadinessStatuses.SupervisorRequired,
        EstimatedSellableUnits = bottleneck?.EstimatedSellableUnits,
        LeafIngredientCount = productIngredients.Count,
        ErrorCount = errors + (product.IsSoldOut ? 1 : 0),
        WarningCount = warnings,
        BottleneckMaterial = bottleneck is null ? null : $"{bottleneck.MaterialCode} · {bottleneck.MaterialName}",
        PredictedPosMessage = predictedMessage,
        SuggestedAction = SuggestedProductAction(status)
      });
    }

    var environmentChecks = BuildEnvironmentChecks(catalog, menuProducts, context);
    actions.AddRange(environmentChecks
      .Where(check => check.Status is RestaurantSaleReadinessStatuses.Warning or RestaurantSaleReadinessStatuses.ConfigurationBlocked)
      .Select(check => new RestaurantSaleReadinessAction
      {
        Severity = check.Status == RestaurantSaleReadinessStatuses.Warning
          ? RestaurantSaleReadinessSeverities.Warning
          : RestaurantSaleReadinessSeverities.Error,
        Issue = $"{check.Area}: {check.Detail}",
        RecommendedAction = check.RecommendedAction
      }));

    return new RestaurantSaleReadinessReport
    {
      Rfc = normalizedRfc,
      SiteId = siteId,
      SiteCode = catalog.Site.SiteCode,
      SiteName = catalog.Site.Name,
      SiteTimeZoneId = catalog.Site.TimeZoneId,
      MenuName = catalog.MenuName,
      UsesFallbackCatalog = string.Equals(catalog.MenuName, "Catálogo activo", StringComparison.OrdinalIgnoreCase),
      AllowSupervisorDeficit = catalog.Site.AllowSupervisorDeficit,
      GeneratedAtUtc = generatedAtUtc,
      GeneratedAtLocal = generatedAtLocal,
      Products = productRows,
      Ingredients = ingredientRows,
      BomRows = bomRows,
      Modifiers = modifierRows,
      EnvironmentChecks = environmentChecks,
      Actions = actions
        .DistinctBy(action => new { action.ProductSku, action.MaterialId, action.Issue })
        .OrderBy(action => SeverityRank(action.Severity))
        .ThenBy(action => action.ProductSku, StringComparer.OrdinalIgnoreCase)
        .ThenBy(action => action.Material, StringComparer.OrdinalIgnoreCase)
        .ToList()
    };
  }

  private static async Task<OperationalContext> LoadOperationalContextAsync(
    DbConnection connection,
    string rfc,
    int siteId,
    CancellationToken ct)
  {
    const string sql =
      """
      SELECT balance.Id, balance.MaterialId, balance.LocationId, balance.Quantity,
             balance.ReservedQuantity, balance.MinQuantity, balance.MaxQuantity,
             balance.IsRemoved, locationInfo.LocationCode, locationInfo.LocationName,
             locationInfo.IsActive AS LocationIsActive, locationInfo.IsInventoryEnabled,
             priorityInfo.Priority
      FROM logistica.StockBalance balance
      JOIN logistica.Location locationInfo ON locationInfo.Rfc = balance.Rfc AND locationInfo.Id = balance.LocationId
      OUTER APPLY
      (
        SELECT MIN(sitePriority.Priority) AS Priority
        FROM restaurante.SiteLocationPriority sitePriority
        WHERE sitePriority.Rfc = balance.Rfc AND sitePriority.SiteId = @SiteId
          AND sitePriority.LocationId = balance.LocationId
      ) priorityInfo
      WHERE balance.Rfc = @Rfc AND balance.IsRemoved = 0;

      SELECT lotBalance.MaterialLotId, lotBalance.MaterialId, lotBalance.LocationId,
             lotBalance.Quantity, lotBalance.ReservedQuantity, lot.LotCode, lot.ExpiresAt,
             lot.IsBlocked, locationInfo.LocationCode, locationInfo.LocationName,
             priorityInfo.Priority
      FROM logistica.LotBalance lotBalance
      JOIN logistica.MaterialLot lot ON lot.Rfc = lotBalance.Rfc AND lot.Id = lotBalance.MaterialLotId
      JOIN logistica.Location locationInfo ON locationInfo.Rfc = lotBalance.Rfc AND locationInfo.Id = lotBalance.LocationId
      OUTER APPLY
      (
        SELECT MIN(sitePriority.Priority) AS Priority
        FROM restaurante.SiteLocationPriority sitePriority
        WHERE sitePriority.Rfc = lotBalance.Rfc AND sitePriority.SiteId = @SiteId
          AND sitePriority.LocationId = lotBalance.LocationId
      ) priorityInfo
      WHERE lotBalance.Rfc = @Rfc;

      SELECT Id, LocationCode, LocationName, IsActive, IsInventoryEnabled
      FROM logistica.Location WHERE Rfc = @Rfc;

      SELECT Id, StationCode, [Name], IsActive
      FROM restaurante.KitchenStation WHERE Rfc = @Rfc AND SiteId = @SiteId;

      SELECT COUNT(*) FROM restaurante.CashShift
      WHERE Rfc = @Rfc AND SiteId = @SiteId AND [Status] = 'Open';

      SELECT COUNT(*) FROM restaurante.SiteLocationPriority
      WHERE Rfc = @Rfc AND SiteId = @SiteId;
      """;

    using var multi = await connection.QueryMultipleAsync(new CommandDefinition(
      sql,
      new { Rfc = rfc, SiteId = siteId },
      cancellationToken: ct));
    return new OperationalContext
    {
      StockBalances = (await multi.ReadAsync<StockBalanceRow>()).AsList(),
      LotBalances = (await multi.ReadAsync<LotBalanceRow>()).AsList(),
      Locations = (await multi.ReadAsync<LocationRow>()).AsList(),
      Stations = (await multi.ReadAsync<StationRow>()).AsList(),
      OpenShiftCount = await multi.ReadSingleAsync<int>(),
      LocationPriorityCount = await multi.ReadSingleAsync<int>()
    };
  }

  private static ModifierEvaluation EvaluateModifiers(
    RestaurantSaleRequirementGraph graph,
    RestaurantProductDto product,
    RestaurantSaleRequirementCalculation baseCalculation,
    OperationalContext context,
    DateTimeOffset at,
    bool allowSupervisorDeficit,
    bool fallbackLocationExists)
  {
    var result = new ModifierEvaluation();
    foreach (var group in product.ModifierGroups)
    {
      if (group.MinSelections < 0 || group.MaxSelections < group.MinSelections || group.Options.Count < group.MinSelections)
      {
        result.ConfigurationBlocked = true;
        result.ErrorCount++;
        result.FirstBlockingMessage ??= $"El grupo {group.Name} requiere entre {group.MinSelections} y {group.MaxSelections} opciones.";
        result.Actions.Add(new RestaurantSaleReadinessAction
        {
          Severity = RestaurantSaleReadinessSeverities.Error,
          ProductSku = product.Sku,
          ProductName = ProductDisplayName(product),
          Issue = result.FirstBlockingMessage,
          RecommendedAction = "Corrige los límites del grupo o activa suficientes opciones."
        });
      }

      var optionReadyCount = 0;
      foreach (var option in group.Options)
      {
        var deltas = graph.ModifierDeltas.Where(delta => delta.OptionId == option.Id).ToList();
        var optionBlocked = false;
        var optionSupervisor = false;
        var optionWarning = false;
        if (deltas.Count == 0)
        {
          result.Rows.Add(new RestaurantSaleReadinessModifierRow
          {
            ProductId = product.Id,
            ProductSku = product.Sku,
            ProductName = ProductDisplayName(product),
            GroupId = group.Id,
            GroupName = group.Name,
            MinSelections = group.MinSelections,
            MaxSelections = group.MaxSelections,
            OptionId = option.Id,
            OptionName = option.Name,
            PriceDelta = option.PriceDelta,
            Status = RestaurantSaleReadinessStatuses.Ready,
            Message = "Sin impacto adicional de inventario."
          });
        }
        foreach (var delta in deltas)
        {
          var row = new RestaurantSaleReadinessModifierRow
          {
            ProductId = product.Id,
            ProductSku = product.Sku,
            ProductName = ProductDisplayName(product),
            GroupId = group.Id,
            GroupName = group.Name,
            MinSelections = group.MinSelections,
            MaxSelections = group.MaxSelections,
            OptionId = option.Id,
            OptionName = option.Name,
            PriceDelta = option.PriceDelta,
            MaterialId = delta.MaterialId,
            QuantityDelta = delta.QuantityDelta,
            DeltaUnit = delta.Unit
          };
          if (!graph.Materials.TryGetValue(delta.MaterialId, out var material) || !material.IsActive)
          {
            row.Status = RestaurantSaleReadinessStatuses.ConfigurationBlocked;
            row.Message = $"El modificador referencia el material {delta.MaterialId}, que no existe o está inactivo.";
            optionBlocked = true;
          }
          else
          {
            row.MaterialCode = material.Code;
            row.MaterialName = material.Name;
            var factor = RestaurantSaleRequirementCalculator.FindConversionFactor(graph, material.Id, delta.UnitId);
            row.ConversionFactor = factor;
            if (!factor.HasValue)
            {
              row.Status = RestaurantSaleReadinessStatuses.ConfigurationBlocked;
              row.Message = "Falta una conversión para los ingredientes de un modificador.";
              optionBlocked = true;
            }
            else
            {
              var impact = delta.QuantityDelta * factor.Value;
              var totalRequirement = Math.Max(0, baseCalculation.Requirements.GetValueOrDefault(material.Id) + impact);
              row.BaseQuantityImpact = RoundQuantity(impact);
              var inventory = EvaluateInventory(material, totalRequirement, context.StockBalances, context.LotBalances, at, allowSupervisorDeficit, fallbackLocationExists);
              row.AvailableAfterBaseProduct = RoundQuantity(inventory.UsableQuantity - baseCalculation.Requirements.GetValueOrDefault(material.Id));
              row.Status = inventory.Status;
              row.Message = inventory.Message ?? "La opción no agrega un bloqueo de inventario.";
              optionBlocked |= inventory.Status is RestaurantSaleReadinessStatuses.InventoryBlocked;
              optionSupervisor |= inventory.Status is RestaurantSaleReadinessStatuses.SupervisorRequired;
              optionWarning |= inventory.Status is RestaurantSaleReadinessStatuses.Warning;
            }
          }
          result.Rows.Add(row);
          if (row.Status is not RestaurantSaleReadinessStatuses.Ready)
          {
            result.Actions.Add(new RestaurantSaleReadinessAction
            {
              Severity = row.Status == RestaurantSaleReadinessStatuses.Warning
                ? RestaurantSaleReadinessSeverities.Warning
                : RestaurantSaleReadinessSeverities.Error,
              ProductSku = product.Sku,
              ProductName = ProductDisplayName(product),
              MaterialId = row.MaterialId,
              Material = string.IsNullOrWhiteSpace(row.MaterialCode) ? row.MaterialName : $"{row.MaterialCode} · {row.MaterialName}",
              Issue = $"Modificador {group.Name} / {option.Name}: {row.Message}",
              RecommendedAction = row.Status == RestaurantSaleReadinessStatuses.ConfigurationBlocked
                ? "Corrige el material o la conversión del modificador."
                : "Repón el ingrediente o deshabilita temporalmente esta opción."
            });
          }
        }

        if (!optionBlocked && !optionSupervisor) optionReadyCount++;
        if (optionBlocked) result.ErrorCount++;
        if (optionBlocked || optionSupervisor || optionWarning) result.WarningCount++;
      }

      if (group.MinSelections > 0 && optionReadyCount < group.MinSelections)
      {
        var hasConfigurationFailure = result.Rows.Any(row => row.GroupId == group.Id && row.Status == RestaurantSaleReadinessStatuses.ConfigurationBlocked);
        result.ConfigurationBlocked |= hasConfigurationFailure;
        result.InventoryBlocked |= !hasConfigurationFailure && result.Rows.Any(row => row.GroupId == group.Id && row.Status == RestaurantSaleReadinessStatuses.InventoryBlocked);
        result.SupervisorRequired |= !hasConfigurationFailure && !result.InventoryBlocked
          && result.Rows.Any(row => row.GroupId == group.Id && row.Status == RestaurantSaleReadinessStatuses.SupervisorRequired);
        result.FirstBlockingMessage ??= hasConfigurationFailure
          ? $"El grupo obligatorio {group.Name} no tiene suficientes opciones correctamente configuradas."
          : $"El grupo obligatorio {group.Name} no tiene suficientes opciones con inventario para venderse sin intervención.";
        result.Actions.Add(new RestaurantSaleReadinessAction
        {
          Severity = RestaurantSaleReadinessSeverities.Error,
          ProductSku = product.Sku,
          ProductName = ProductDisplayName(product),
          Issue = result.FirstBlockingMessage,
          RecommendedAction = hasConfigurationFailure
            ? "Corrige las opciones obligatorias antes de ofrecer el producto."
            : "Repón los ingredientes de al menos las opciones mínimas requeridas."
        });
      }
    }
    return result;
  }

  private static InventoryEvaluation EvaluateInventory(
    RestaurantSaleMaterialNode material,
    decimal requiredQuantity,
    IReadOnlyList<StockBalanceRow> balances,
    IReadOnlyList<LotBalanceRow> lots,
    DateTimeOffset at,
    bool allowSupervisorDeficit,
    bool fallbackLocationExists)
  {
    var roundedRequired = RoundQuantity(requiredQuantity);
    var materialBalances = balances.Where(balance => balance.MaterialId == material.Id && !balance.IsRemoved).ToList();
    var stockQuantity = RoundQuantity(materialBalances.Sum(balance => balance.Quantity));
    var minimumQuantity = RoundQuantity(materialBalances.Where(balance => balance.MinQuantity.HasValue).Sum(balance => balance.MinQuantity ?? 0));
    var hasMinimum = materialBalances.Any(balance => balance.MinQuantity.HasValue);
    decimal reserved;
    decimal usable;
    decimal excluded = 0;
    string locationSummary;

    if (material.TrackLots)
    {
      var materialLots = lots.Where(lot => lot.MaterialId == material.Id).ToList();
      reserved = RoundQuantity(materialLots.Sum(lot => lot.ReservedQuantity));
      var eligible = materialLots.Where(lot => !lot.IsBlocked && (!lot.ExpiresAt.HasValue || lot.ExpiresAt.Value >= DateOnly.FromDateTime(at.UtcDateTime.Date)))
        .Select(lot => new { Row = lot, Available = Math.Max(0, lot.Quantity - lot.ReservedQuantity) })
        .Where(item => item.Available > 0)
        .OrderBy(item => item.Row.Priority ?? int.MaxValue)
        .ThenBy(item => item.Row.ExpiresAt.HasValue ? 0 : 1)
        .ThenBy(item => item.Row.ExpiresAt)
        .ThenBy(item => item.Row.MaterialLotId)
        .ToList();
      usable = RoundQuantity(eligible.Sum(item => item.Available));
      excluded = RoundQuantity(materialLots
        .Where(lot => lot.IsBlocked || (lot.ExpiresAt.HasValue && lot.ExpiresAt.Value < DateOnly.FromDateTime(at.UtcDateTime.Date)))
        .Sum(lot => Math.Max(0, lot.Quantity - lot.ReservedQuantity)));
      locationSummary = string.Join("; ", eligible.GroupBy(item => new { item.Row.LocationId, item.Row.LocationName })
        .Select(group => $"{group.Key.LocationName}: {group.Sum(item => item.Available):N4}")
        .Take(8));
    }
    else
    {
      reserved = RoundQuantity(materialBalances.Sum(balance => balance.ReservedQuantity));
      var eligible = materialBalances
        .Select(balance => new { Row = balance, Available = Math.Max(0, balance.Quantity - balance.ReservedQuantity) })
        .Where(item => item.Available > 0)
        .OrderBy(item => item.Row.Priority ?? int.MaxValue)
        .ThenBy(item => item.Row.LocationId)
        .ToList();
      usable = RoundQuantity(eligible.Sum(item => item.Available));
      locationSummary = string.Join("; ", eligible
        .Select(item => $"{item.Row.LocationName}: {item.Available:N4}")
        .Take(8));
    }

    var shortage = RoundQuantity(Math.Max(0, roundedRequired - usable));
    var projected = RoundQuantity(usable - roundedRequired);
    decimal? estimatedUnits = roundedRequired > 0
      ? decimal.Floor(Math.Max(0, usable) / roundedRequired)
      : null;
    if (shortage > 0)
    {
      var message = $"Inventario insuficiente para el material {material.Id}. Faltan {shortage:N4}.";
      return new InventoryEvaluation(
        stockQuantity, reserved, usable, excluded, projected, minimumQuantity, shortage,
        estimatedUnits, locationSummary,
        allowSupervisorDeficit && fallbackLocationExists
          ? RestaurantSaleReadinessStatuses.SupervisorRequired
          : RestaurantSaleReadinessStatuses.InventoryBlocked,
        message);
    }
    if (hasMinimum && projected <= minimumQuantity)
    {
      return new InventoryEvaluation(
        stockQuantity, reserved, usable, excluded, projected, minimumQuantity, 0,
        estimatedUnits, locationSummary, RestaurantSaleReadinessStatuses.Warning,
        $"La venta puede completarse, pero el disponible proyectado de {material.Code} quedaría en {projected:N4}, en o debajo del mínimo {minimumQuantity:N4}.");
    }
    return new InventoryEvaluation(
      stockQuantity, reserved, usable, excluded, projected, minimumQuantity, 0,
      estimatedUnits, locationSummary, RestaurantSaleReadinessStatuses.Ready, null);
  }

  private static void AppendBomRows(
    ICollection<RestaurantSaleReadinessBomRow> destination,
    RestaurantSaleRequirementGraph graph,
    RestaurantProductDto product,
    RestaurantSaleRequirementCalculation calculation)
  {
    foreach (var trace in calculation.Trace)
    {
      graph.Materials.TryGetValue(trace.ParentMaterialId, out var parent);
      RestaurantSaleMaterialNode? component = null;
      if (trace.ComponentMaterialId.HasValue) graph.Materials.TryGetValue(trace.ComponentMaterialId.Value, out component);
      destination.Add(new RestaurantSaleReadinessBomRow
      {
        ProductId = product.Id,
        ProductSku = product.Sku,
        ProductName = ProductDisplayName(product),
        Depth = trace.Depth,
        Path = trace.Path,
        ParentMaterialId = trace.ParentMaterialId,
        ParentMaterialCode = parent?.Code ?? string.Empty,
        ParentMaterialName = parent?.Name ?? string.Empty,
        BomVersionId = trace.BomVersionId,
        BomVersionNumber = trace.BomVersionNumber,
        YieldQuantity = trace.YieldQuantity,
        YieldUnit = trace.YieldUnit,
        ComponentMaterialId = trace.ComponentMaterialId,
        ComponentMaterialCode = component?.Code ?? string.Empty,
        ComponentMaterialName = component?.Name ?? string.Empty,
        ComponentFulfillmentMode = component?.FulfillmentMode ?? string.Empty,
        ComponentQuantity = trace.ComponentQuantity,
        ComponentUnit = trace.ComponentUnit,
        ExpectedWastePercent = trace.ExpectedWastePercent,
        ConversionFactor = trace.ConversionFactor,
        RequiredBaseQuantity = trace.RequiredBaseQuantity,
        Status = trace.Status,
        Message = trace.Message
      });
    }
  }

  private static IReadOnlyList<RestaurantSaleReadinessEnvironmentCheck> BuildEnvironmentChecks(
    RestaurantPosCatalogDto catalog,
    IReadOnlyList<MenuProduct> products,
    OperationalContext context)
  {
    var fallback = string.Equals(catalog.MenuName, "Catálogo activo", StringComparison.OrdinalIgnoreCase);
    var missingStations = products.Count(item => item.Product.KitchenStationId.HasValue
      && !context.Stations.Any(station => station.Id == item.Product.KitchenStationId && station.IsActive));
    return
    [
      new()
      {
        Area = "Sede",
        Check = "Módulo habilitado",
        Status = catalog.Site.IsEnabled ? RestaurantSaleReadinessStatuses.Ready : RestaurantSaleReadinessStatuses.ConfigurationBlocked,
        Detail = catalog.Site.IsEnabled ? "La sede está habilitada." : "Restaurante está deshabilitado para esta sede.",
        RecommendedAction = catalog.Site.IsEnabled ? string.Empty : "Habilita la sede antes de vender."
      },
      new()
      {
        Area = "Menú",
        Check = "Menú publicado y vigente",
        Status = fallback ? RestaurantSaleReadinessStatuses.Warning : RestaurantSaleReadinessStatuses.Ready,
        Detail = fallback ? "No se resolvió un menú publicado vigente; el POS usa todo el catálogo activo." : $"Menú vigente: {catalog.MenuName}.",
        RecommendedAction = fallback ? "Revisa publicación y horarios del menú." : string.Empty
      },
      new()
      {
        Area = "Inventario",
        Check = "Ubicación para déficit",
        Status = context.Locations.Any(location => location.IsActive && location.IsInventoryEnabled)
          ? RestaurantSaleReadinessStatuses.Ready
          : RestaurantSaleReadinessStatuses.ConfigurationBlocked,
        Detail = context.Locations.Any(location => location.IsActive && location.IsInventoryEnabled)
          ? "Existe al menos una ubicación activa habilitada para inventario."
          : "No hay una ubicación de inventario configurada para registrar el déficit.",
        RecommendedAction = context.Locations.Any(location => location.IsActive && location.IsInventoryEnabled)
          ? string.Empty
          : "Configura al menos una ubicación activa y habilitada para inventario."
      },
      new()
      {
        Area = "Inventario",
        Check = "Prioridades de ubicación",
        Status = context.LocationPriorityCount > 0 ? RestaurantSaleReadinessStatuses.Ready : RestaurantSaleReadinessStatuses.Warning,
        Detail = context.LocationPriorityCount > 0 ? $"Hay {context.LocationPriorityCount} prioridad(es) configurada(s)." : "No hay prioridades de ubicación para la sede.",
        RecommendedAction = context.LocationPriorityCount > 0 ? string.Empty : "Define el orden de consumo de ubicaciones para la sede."
      },
      new()
      {
        Area = "Cocina",
        Check = "Estaciones activas",
        Status = missingStations == 0 ? RestaurantSaleReadinessStatuses.Ready : RestaurantSaleReadinessStatuses.Warning,
        Detail = missingStations == 0 ? "Las estaciones asignadas están activas." : $"{missingStations} producto(s) apuntan a una estación inexistente o inactiva.",
        RecommendedAction = missingStations == 0 ? string.Empty : "Corrige las estaciones asignadas a esos productos."
      },
      new()
      {
        Area = "Caja",
        Check = "Turno abierto",
        Status = context.OpenShiftCount > 0 ? RestaurantSaleReadinessStatuses.Ready : RestaurantSaleReadinessStatuses.Warning,
        Detail = context.OpenShiftCount > 0 ? $"Hay {context.OpenShiftCount} turno(s) de caja abierto(s)." : "No hay un turno de caja abierto; descuentos y déficit con supervisor no podrán autorizarse.",
        RecommendedAction = context.OpenShiftCount > 0 ? string.Empty : "Abre el turno de caja antes del servicio."
      },
      new()
      {
        Area = "Simulación",
        Check = "Alcance",
        Status = RestaurantSaleReadinessStatuses.Ready,
        Detail = "Cada producto se evaluó por separado con cantidad 1, sin promociones, descuentos, cliente, entrega ni pagos.",
        RecommendedAction = "Consulta la fecha del reporte: el inventario puede cambiar después de descargarlo."
      },
      new()
      {
        Area = "Simulación",
        Check = "Modificadores",
        Status = RestaurantSaleReadinessStatuses.Ready,
        Detail = "Cada opción se probó individualmente sobre el producto base; no se generaron todas las combinaciones posibles.",
        RecommendedAction = "Revisa la hoja Modificadores para opciones obligatorias o bloqueadas."
      }
    ];
  }

  private static string SuggestedProductAction(string status) => status switch
  {
    RestaurantSaleReadinessStatuses.Ready => "Sin acción inmediata.",
    RestaurantSaleReadinessStatuses.Warning => "Revisa advertencias y repón inventario antes del servicio.",
    RestaurantSaleReadinessStatuses.SupervisorRequired => "Repón inventario; mientras tanto, prepara autorización de supervisor y turno abierto.",
    RestaurantSaleReadinessStatuses.InventoryBlocked => "Repón o ajusta inventario antes de ofrecer el producto.",
    RestaurantSaleReadinessStatuses.ConfigurationBlocked => "Corrige BOM, materiales, unidades o modificadores antes de vender.",
    RestaurantSaleReadinessStatuses.SoldOut => "Confirma o retira la marca de agotado.",
    _ => "Revisar."
  };

  private static string InventoryAction(RestaurantSaleReadinessIngredient ingredient)
    => ingredient.Status switch
    {
      RestaurantSaleReadinessStatuses.Warning => "Repón el material antes de llegar al mínimo operativo.",
      RestaurantSaleReadinessStatuses.SupervisorRequired => "Repón inventario o prepara la autorización de déficit con supervisor.",
      _ => "Repón o corrige el saldo del material antes de vender."
    };

  private static string ConfigurationAction(string issueCode)
    => issueCode switch
    {
      "BOM_MISSING" => "Crea o activa un BOM con componentes para el material indicado.",
      "BOM_CONVERSION_MISSING" or "MODIFIER_CONVERSION_MISSING" => "Configura la conversión hacia la unidad base del material.",
      "BOM_CYCLE_OR_DEPTH" => "Elimina la dependencia circular o simplifica el BOM.",
      "MATERIAL_INACTIVE" or "BOM_COMPONENT_INACTIVE" => "Reactiva o sustituye el material inactivo.",
      _ => "Corrige la configuración indicada antes de vender."
    };

  private static int SeverityRank(string severity) => severity switch
  {
    RestaurantSaleReadinessSeverities.Error => 0,
    RestaurantSaleReadinessSeverities.Warning => 1,
    _ => 2
  };

  private static string ProductDisplayName(RestaurantProductDto product)
    => string.IsNullOrWhiteSpace(product.VariantName) ? product.Name : $"{product.Name} · {product.VariantName}";

  private static string MaterialLabel(RestaurantSaleMaterialNode material)
    => string.IsNullOrWhiteSpace(material.Name) ? $"material {material.Id}" : $"{material.Name} (material {material.Id})";

  private static string MaterialSortKey(RestaurantSaleRequirementGraph graph, int materialId)
    => graph.Materials.TryGetValue(materialId, out var material) ? $"{material.Code}|{material.Name}" : materialId.ToString("D10");

  private static decimal RoundQuantity(decimal value)
    => decimal.Round(value, 4, MidpointRounding.AwayFromZero);

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica de conexiones no devolvió una DbConnection.");

  private sealed record MenuProduct(RestaurantProductDto Product, string Sections);

  private sealed record InventoryEvaluation(
    decimal StockQuantity,
    decimal ReservedQuantity,
    decimal UsableQuantity,
    decimal ExcludedLotQuantity,
    decimal ProjectedUsableQuantity,
    decimal MinimumQuantity,
    decimal ShortageQuantity,
    decimal? EstimatedSellableUnits,
    string LocationSummary,
    string Status,
    string? Message);

  private sealed class ModifierEvaluation
  {
    public List<RestaurantSaleReadinessModifierRow> Rows { get; } = [];
    public List<RestaurantSaleReadinessAction> Actions { get; } = [];
    public bool ConfigurationBlocked { get; set; }
    public bool InventoryBlocked { get; set; }
    public bool SupervisorRequired { get; set; }
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public string? FirstBlockingMessage { get; set; }
  }

  private sealed class OperationalContext
  {
    public IReadOnlyList<StockBalanceRow> StockBalances { get; init; } = [];
    public IReadOnlyList<LotBalanceRow> LotBalances { get; init; } = [];
    public IReadOnlyList<LocationRow> Locations { get; init; } = [];
    public IReadOnlyList<StationRow> Stations { get; init; } = [];
    public int OpenShiftCount { get; init; }
    public int LocationPriorityCount { get; init; }
  }

  private sealed class StockBalanceRow
  {
    public int Id { get; set; }
    public int MaterialId { get; set; }
    public int LocationId { get; set; }
    public decimal Quantity { get; set; }
    public decimal ReservedQuantity { get; set; }
    public decimal? MinQuantity { get; set; }
    public decimal? MaxQuantity { get; set; }
    public bool IsRemoved { get; set; }
    public string LocationCode { get; set; } = string.Empty;
    public string LocationName { get; set; } = string.Empty;
    public bool LocationIsActive { get; set; }
    public bool IsInventoryEnabled { get; set; }
    public int? Priority { get; set; }
  }

  private sealed class LotBalanceRow
  {
    public long MaterialLotId { get; set; }
    public int MaterialId { get; set; }
    public int LocationId { get; set; }
    public decimal Quantity { get; set; }
    public decimal ReservedQuantity { get; set; }
    public string LotCode { get; set; } = string.Empty;
    public DateOnly? ExpiresAt { get; set; }
    public bool IsBlocked { get; set; }
    public string LocationCode { get; set; } = string.Empty;
    public string LocationName { get; set; } = string.Empty;
    public int? Priority { get; set; }
  }

  private sealed class LocationRow
  {
    public int Id { get; set; }
    public string LocationCode { get; set; } = string.Empty;
    public string LocationName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool IsInventoryEnabled { get; set; }
  }

  private sealed class StationRow
  {
    public int Id { get; set; }
    public string StationCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
  }
}
