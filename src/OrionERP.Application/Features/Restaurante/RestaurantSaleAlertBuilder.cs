using OrionERP.Application.Features.Logistica.Materials;

namespace OrionERP.Application.Features.Restaurante;

/// <summary>
/// Convierte el diagnóstico de venta en el tablero que ve la caja. No vuelve a evaluar inventario
/// ni recorre el BOM: toma lo que <c>AnalyzeAsync</c> ya calculó y lo reagrupa por material, que es
/// la unidad con la que se repone y se cuenta.
/// </summary>
public static class RestaurantSaleAlertBuilder
{
  /// <summary>Cuántos platillos afectados se enumeran antes de resumir con un contador.</summary>
  public const int MaximumAffectedProductsListed = 5;

  public static RestaurantSaleAlertBoard Build(RestaurantSaleReadinessReport report)
  {
    ArgumentNullException.ThrowIfNull(report);

    // La acción recomendada ya viene resuelta por el diagnóstico, que distingue comprar de producir
    // (un subproducto MakeToStock se repone planeando una producción). Se recupera en vez de
    // recalcularse para que el panel y el Excel nunca digan cosas distintas.
    var actionsByIssue = new Dictionary<(int MaterialId, string Issue), string>();
    var actionsByMaterial = new Dictionary<int, string>();
    foreach (var action in report.Actions)
    {
      if (!action.MaterialId.HasValue || string.IsNullOrWhiteSpace(action.RecommendedAction)) continue;
      actionsByIssue.TryAdd((action.MaterialId.Value, action.Issue ?? string.Empty), action.RecommendedAction);
      actionsByMaterial.TryAdd(action.MaterialId.Value, action.RecommendedAction);
    }

    var materials = report.Ingredients
      .Where(ingredient => Severity(ingredient.Status) < NoIssueSeverity)
      .GroupBy(ingredient => ingredient.MaterialId)
      .Select(group => BuildMaterial(group.ToList(), actionsByIssue, actionsByMaterial))
      .OrderBy(material => Severity(material.Status))
      .ThenBy(
        material => MaterialSortOrder.Key(material.MaterialName, material.MaterialCode),
        MaterialSortOrder.Comparer)
      .ToList();

    // Un BOM roto o una unidad sin conversión también revientan al cobrar, pero contar no los
    // arregla: se listan aparte y sin casilla.
    var productIssues = report.Products
      .Where(product => product.Status == RestaurantSaleReadinessStatuses.ConfigurationBlocked)
      .OrderBy(product => product.ProductName, StringComparer.CurrentCultureIgnoreCase)
      .Select(product => new RestaurantSaleAlertProduct
      {
        ProductId = product.ProductId,
        Sku = product.Sku,
        ProductName = product.ProductName,
        Status = product.Status,
        PredictedPosMessage = product.PredictedPosMessage,
        SuggestedAction = product.SuggestedAction
      })
      .ToList();

    return new RestaurantSaleAlertBoard
    {
      GeneratedAtLocal = report.GeneratedAtLocal,
      AllowSupervisorDeficit = report.AllowSupervisorDeficit,
      BlockedCount = materials.Count(material => material.Status == RestaurantSaleReadinessStatuses.InventoryBlocked),
      SupervisorCount = materials.Count(material => material.Status == RestaurantSaleReadinessStatuses.SupervisorRequired),
      WarningCount = materials.Count(material => material.Status == RestaurantSaleReadinessStatuses.Warning),
      ConfigurationIssueCount = productIssues.Count,
      Materials = materials,
      ProductIssues = productIssues
    };
  }

  private static RestaurantSaleAlertMaterial BuildMaterial(
    IReadOnlyList<RestaurantSaleReadinessIngredient> rows,
    IReadOnlyDictionary<(int MaterialId, string Issue), string> actionsByIssue,
    IReadOnlyDictionary<int, string> actionsByMaterial)
  {
    // El renglón que manda es el más grave; entre iguales, el que más falta.
    var worst = rows
      .OrderBy(row => Severity(row.Status))
      .ThenByDescending(row => row.ShortageQuantity)
      .First();

    var affectedProducts = rows
      .Select(row => row.ProductName)
      .Where(name => !string.IsNullOrWhiteSpace(name))
      .Distinct(StringComparer.CurrentCultureIgnoreCase)
      .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
      .ToList();

    var issue = worst.PredictedPosMessage ?? worst.Status;
    var recommendedAction = actionsByIssue.TryGetValue((worst.MaterialId, issue), out var byIssue)
      ? byIssue
      : actionsByMaterial.GetValueOrDefault(worst.MaterialId, string.Empty);

    return new RestaurantSaleAlertMaterial
    {
      MaterialId = worst.MaterialId,
      MaterialCode = worst.MaterialCode,
      MaterialName = worst.MaterialName,
      BaseUnit = worst.BaseUnit,
      Status = worst.Status,
      // El faltante no se suma entre productos: cada renglón simula vender una unidad de ese
      // producto, no la carta completa.
      ShortageQuantity = rows.Max(row => row.ShortageQuantity),
      UsableQuantity = worst.UsableQuantity,
      MinimumQuantity = worst.MinimumQuantity,
      FulfillmentMode = worst.FulfillmentMode,
      AffectedProductCount = affectedProducts.Count,
      AffectedProducts = affectedProducts.Take(MaximumAffectedProductsListed).ToList(),
      RecommendedAction = recommendedAction,
      PredictedPosMessage = worst.PredictedPosMessage
    };
  }

  private const int NoIssueSeverity = 3;

  /// <summary>Menor es peor, para poder ordenar y elegir el máximo con el mismo criterio.</summary>
  private static int Severity(string status) => status switch
  {
    RestaurantSaleReadinessStatuses.InventoryBlocked => 0,
    RestaurantSaleReadinessStatuses.ConfigurationBlocked => 0,
    RestaurantSaleReadinessStatuses.SupervisorRequired => 1,
    RestaurantSaleReadinessStatuses.Warning => 2,
    _ => NoIssueSeverity
  };
}
