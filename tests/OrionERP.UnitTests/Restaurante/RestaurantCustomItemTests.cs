using OrionERP.Application.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantCustomItemTests
{
  [Fact]
  public void CreateSnapshot_NormalizesDescriptionPriceAndGross()
  {
    var snapshot = RestaurantCustomItemRules.CreateSnapshot(new RestaurantOrderLineCreateRequest
    {
      IsCustom = true,
      CustomName = "  Artículo especial  ",
      CustomUnitPrice = 34.567m,
      Quantity = 2
    });

    Assert.Equal("Artículo especial", snapshot.Name);
    Assert.Equal(34.57m, snapshot.UnitPrice);
    Assert.Equal(69.14m, snapshot.Gross);
  }

  [Theory]
  [InlineData(null, 10)]
  [InlineData("", 10)]
  [InlineData("Cargo", 0)]
  [InlineData("Cargo", -1)]
  public void CreateSnapshot_RejectsIncompleteCustomCharge(string? name, decimal price)
  {
    var line = new RestaurantOrderLineCreateRequest
    {
      IsCustom = true,
      CustomName = name,
      CustomUnitPrice = price,
      Quantity = 1
    };

    Assert.Throws<InvalidOperationException>(() => RestaurantCustomItemRules.CreateSnapshot(line));
  }

  [Fact]
  public void CreateSnapshot_RejectsCatalogReferencesAndModifiers()
  {
    var withProduct = new RestaurantOrderLineCreateRequest
    {
      IsCustom = true,
      ProductId = 10,
      CustomName = "Cargo",
      CustomUnitPrice = 25
    };
    var withModifier = new RestaurantOrderLineCreateRequest
    {
      IsCustom = true,
      CustomName = "Cargo",
      CustomUnitPrice = 25,
      ModifierOptionIds = [5]
    };
    var withMenuSection = new RestaurantOrderLineCreateRequest
    {
      IsCustom = true,
      MenuSectionId = 8,
      CustomName = "Cargo",
      CustomUnitPrice = 25
    };

    Assert.Throws<InvalidOperationException>(() => RestaurantCustomItemRules.CreateSnapshot(withProduct));
    Assert.Throws<InvalidOperationException>(() => RestaurantCustomItemRules.CreateSnapshot(withModifier));
    Assert.Throws<InvalidOperationException>(() => RestaurantCustomItemRules.CreateSnapshot(withMenuSection));
  }

  [Fact]
  public void ValidateCatalogLine_RejectsCashierPriceOverride()
  {
    var line = new RestaurantOrderLineCreateRequest
    {
      ProductId = 10,
      CustomUnitPrice = 1
    };

    Assert.Throws<InvalidOperationException>(() => RestaurantCustomItemRules.ValidateCatalogLine(line));
  }

  [Fact]
  public void CustomCharge_UsesTheSameTaxIncludedBreakdownAsCatalogProducts()
  {
    var snapshot = RestaurantCustomItemRules.CreateSnapshot(new RestaurantOrderLineCreateRequest
    {
      IsCustom = true,
      CustomName = "Cargo",
      CustomUnitPrice = 100,
      Quantity = 1
    });

    var totals = RestaurantPosTotalsCalculator.Calculate(snapshot.Gross, 0, 0, 0.16m, pricesIncludeTax: true);

    Assert.Equal(86.21m, totals.SubtotalBeforeTax);
    Assert.Equal(13.79m, totals.Tax);
    Assert.Equal(100m, totals.Total);
  }

  [Fact]
  public void CustomLineMigration_PreservesProductForeignKeyAndMarksCustomLines()
  {
    var sql = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/Sql/20260724_restaurant_custom_order_lines.sql");

    Assert.Contains("ALTER COLUMN ProductId bigint NULL", sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("IsCustom bit NOT NULL", sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("CK_OrderLine_CustomProduct", sql, StringComparison.Ordinal);
    Assert.Contains("FOREIGN KEY (Rfc, ProductId)", sql, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void KitchenSectionMigration_AddsSnapshotsAndBackfillsCatalogLines()
  {
    var sql = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/Sql/20260810_restaurant_kitchen_menu_sections.sql");

    Assert.Contains("MenuSectionIdSnapshot bigint NULL", sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("MenuSectionNameSnapshot varchar(100) NULL", sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("MenuSectionSortOrderSnapshot int NULL", sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("WHERE lineInfo.IsCustom=0", sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("ORDER BY menuInfo.IsActive DESC,menuInfo.IsPublished DESC", sql, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void CustomCharge_UsesKitchenLifecycleWithoutConsumingInventory()
  {
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOrderService.cs");

    Assert.Contains("const string lineStatus = \"Pending\"", service, StringComparison.Ordinal);
    Assert.Contains("var hasProductionLines = pricedLines.Count > 0", service, StringComparison.Ordinal);
    Assert.Contains("!line.IsCustom && normalizedStatus == \"Preparing\"", service, StringComparison.Ordinal);
    Assert.DoesNotContain("Un cargo personalizado no requiere transición de cocina", service, StringComparison.Ordinal);
    Assert.DoesNotContain("line is null || line.IsCustom || line.Status != \"Ready\"", service, StringComparison.Ordinal);
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath)));
}
