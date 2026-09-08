using OrionERP.Application.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantSaleAlertBuilderTests
{
  [Fact]
  public void Build_GroupsTheSameMaterialAcrossProductsKeepingTheWorstStatus()
  {
    // La tortilla aparece en dos platillos: en uno sólo queda bajo el mínimo, en el otro bloquea.
    var report = Report(
      ingredients:
      [
        Ingredient(1, "Taco", 500, "MAT-500", "Tortilla", RestaurantSaleReadinessStatuses.Warning, shortage: 0),
        Ingredient(2, "Quesadilla", 500, "MAT-500", "Tortilla", RestaurantSaleReadinessStatuses.InventoryBlocked, shortage: 2.5m)
      ]);

    var board = RestaurantSaleAlertBuilder.Build(report);

    var material = Assert.Single(board.Materials);
    Assert.Equal(500, material.MaterialId);
    Assert.Equal(RestaurantSaleReadinessStatuses.InventoryBlocked, material.Status);
    Assert.Equal(2, material.AffectedProductCount);
    Assert.Equal(["Quesadilla", "Taco"], material.AffectedProducts);
    Assert.Equal(1, board.BlockedCount);
    Assert.Equal(0, board.WarningCount);
  }

  [Fact]
  public void Build_TakesTheLargestShortageInsteadOfAddingThemUp()
  {
    // Cada renglón simula vender UNA unidad de su producto; sumarlos inventaría un faltante que
    // nadie pidió.
    var report = Report(
      ingredients:
      [
        Ingredient(1, "Taco", 500, "MAT-500", "Tortilla", RestaurantSaleReadinessStatuses.InventoryBlocked, shortage: 2m),
        Ingredient(2, "Quesadilla", 500, "MAT-500", "Tortilla", RestaurantSaleReadinessStatuses.InventoryBlocked, shortage: 3.5m)
      ]);

    var board = RestaurantSaleAlertBuilder.Build(report);

    Assert.Equal(3.5m, Assert.Single(board.Materials).ShortageQuantity);
  }

  [Fact]
  public void Build_LeavesOutMaterialsThatAreReady()
  {
    var report = Report(
      ingredients:
      [
        Ingredient(1, "Taco", 500, "MAT-500", "Tortilla", RestaurantSaleReadinessStatuses.Ready, shortage: 0),
        Ingredient(1, "Taco", 600, "MAT-600", "Queso", RestaurantSaleReadinessStatuses.SupervisorRequired, shortage: 1m)
      ]);

    var board = RestaurantSaleAlertBuilder.Build(report);

    Assert.Equal(600, Assert.Single(board.Materials).MaterialId);
  }

  [Fact]
  public void Build_OrdersBlockersFirstAndThenByMaterialName()
  {
    var report = Report(
      ingredients:
      [
        Ingredient(1, "Taco", 700, "MAT-700", "Zanahoria", RestaurantSaleReadinessStatuses.Warning, shortage: 0),
        Ingredient(1, "Taco", 800, "MAT-800", "Aguacate", RestaurantSaleReadinessStatuses.SupervisorRequired, shortage: 1m),
        Ingredient(1, "Taco", 900, "MAT-900", "Res", RestaurantSaleReadinessStatuses.InventoryBlocked, shortage: 4m),
        Ingredient(1, "Taco", 950, "MAT-950", "Cerdo", RestaurantSaleReadinessStatuses.InventoryBlocked, shortage: 1m)
      ]);

    var board = RestaurantSaleAlertBuilder.Build(report);

    Assert.Equal(["Cerdo", "Res", "Aguacate", "Zanahoria"], board.Materials.Select(item => item.MaterialName));
  }

  [Fact]
  public void Build_SendsConfigurationBlockedProductsToTheirOwnListBecauseCountingCannotFixThem()
  {
    var report = Report(
      ingredients: [],
      products:
      [
        Product(10, "SKU-10", "Combo roto", RestaurantSaleReadinessStatuses.ConfigurationBlocked),
        Product(11, "SKU-11", "Postre agotado", RestaurantSaleReadinessStatuses.SoldOut),
        Product(12, "SKU-12", "Taco", RestaurantSaleReadinessStatuses.Ready)
      ]);

    var board = RestaurantSaleAlertBuilder.Build(report);

    Assert.Empty(board.Materials);
    var issue = Assert.Single(board.ProductIssues);
    Assert.Equal("Combo roto", issue.ProductName);
    Assert.Equal(1, board.ConfigurationIssueCount);
  }

  [Fact]
  public void BadgeCount_CountsWhatWillFailOrAskForASupervisorButNotTheWarnings()
  {
    var report = Report(
      ingredients:
      [
        Ingredient(1, "Taco", 500, "MAT-500", "Tortilla", RestaurantSaleReadinessStatuses.InventoryBlocked, shortage: 2m),
        Ingredient(1, "Taco", 600, "MAT-600", "Queso", RestaurantSaleReadinessStatuses.SupervisorRequired, shortage: 1m),
        Ingredient(1, "Taco", 700, "MAT-700", "Cebolla", RestaurantSaleReadinessStatuses.Warning, shortage: 0)
      ],
      products: [Product(10, "SKU-10", "Combo roto", RestaurantSaleReadinessStatuses.ConfigurationBlocked)]);

    var board = RestaurantSaleAlertBuilder.Build(report);

    Assert.Equal(1, board.BlockedCount);
    Assert.Equal(1, board.SupervisorCount);
    Assert.Equal(1, board.WarningCount);
    Assert.Equal(3, board.BadgeCount);
    Assert.True(board.HasBlockers);
    Assert.False(board.IsClean);
  }

  [Fact]
  public void Build_ReusesTheActionThatTheDiagnosticAlreadyResolvedForThatMaterial()
  {
    // Un subproducto MakeToStock se repone produciendo, no comprando. El texto ya lo decidió el
    // diagnóstico; el tablero no debe recalcularlo por su cuenta.
    var ingredient = Ingredient(
      1, "Taco", 500, "MAT-500", "Salsa verde",
      RestaurantSaleReadinessStatuses.InventoryBlocked, shortage: 2m);
    ingredient.FulfillmentMode = "MakeToStock";
    ingredient.PredictedPosMessage = "Inventario insuficiente para MAT-500 · Salsa verde. Faltan 2.0000.";

    var report = Report(
      ingredients: [ingredient],
      actions:
      [
        new RestaurantSaleReadinessAction
        {
          MaterialId = 500,
          Issue = ingredient.PredictedPosMessage,
          RecommendedAction = "Planea una producción de Salsa verde en Restaurante › Producción antes de vender."
        }
      ]);

    var board = RestaurantSaleAlertBuilder.Build(report);

    Assert.StartsWith("Planea una producción", Assert.Single(board.Materials).RecommendedAction, StringComparison.Ordinal);
  }

  [Fact]
  public void Build_OnACleanReportLeavesTheBadgeAtZero()
  {
    var board = RestaurantSaleAlertBuilder.Build(Report(
      ingredients: [Ingredient(1, "Taco", 500, "MAT-500", "Tortilla", RestaurantSaleReadinessStatuses.Ready, shortage: 0)]));

    Assert.True(board.IsClean);
    Assert.Equal(0, board.BadgeCount);
    Assert.False(board.HasBlockers);
  }

  private static RestaurantSaleReadinessReport Report(
    IReadOnlyList<RestaurantSaleReadinessIngredient> ingredients,
    IReadOnlyList<RestaurantSaleReadinessProduct>? products = null,
    IReadOnlyList<RestaurantSaleReadinessAction>? actions = null)
    => new()
    {
      Rfc = "AAA010101AAA",
      SiteId = 1,
      GeneratedAtLocal = new DateTimeOffset(2026, 9, 5, 13, 30, 0, TimeSpan.FromHours(-6)),
      Ingredients = ingredients,
      Products = products ?? [],
      Actions = actions ?? []
    };

  private static RestaurantSaleReadinessIngredient Ingredient(
    long productId,
    string productName,
    int materialId,
    string materialCode,
    string materialName,
    string status,
    decimal shortage)
    => new()
    {
      ProductId = productId,
      ProductSku = $"SKU-{productId}",
      ProductName = productName,
      MaterialId = materialId,
      MaterialCode = materialCode,
      MaterialName = materialName,
      BaseUnit = "kg",
      Status = status,
      ShortageQuantity = shortage,
      UsableQuantity = 1m,
      MinimumQuantity = 0.5m,
      FulfillmentMode = "StockItem"
    };

  private static RestaurantSaleReadinessProduct Product(long productId, string sku, string name, string status)
    => new()
    {
      ProductId = productId,
      Sku = sku,
      ProductName = name,
      Status = status,
      PredictedPosMessage = "mensaje",
      SuggestedAction = "acción"
    };
}
