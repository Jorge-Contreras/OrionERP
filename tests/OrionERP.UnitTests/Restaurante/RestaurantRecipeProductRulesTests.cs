using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Web.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantRecipeProductRulesTests
{
  [Fact]
  public void SemiFinishedWithoutRecipes_StaysAvailableInTheReadyGroup()
  {
    // Regresión de "salsa verde": un semielaborado nuevo no tenía forma de crear su primera receta.
    var salsaVerde = Material(7205, "salsa verde", "SemiFinished", "MakeToStock");

    var options = RestaurantRecipeProductRules.BuildProductOptions(
      [Option(salsaVerde)],
      [salsaVerde],
      []);

    var salsa = Assert.Single(options);
    Assert.Equal(RestaurantRecipeProductRules.ReadyGroup, salsa.Group);
  }

  [Fact]
  public void RawMaterialWithoutRecipes_StaysAvailableInTheSecondaryGroup()
  {
    var tortillas = Material(7100, "TORTILLAS", "RawMaterial", "StockItem");

    var options = RestaurantRecipeProductRules.BuildProductOptions(
      [Option(tortillas)],
      [tortillas],
      []);

    var tortilla = Assert.Single(options);
    Assert.Equal(RestaurantRecipeProductRules.OtherGroup, tortilla.Group);
  }

  [Fact]
  public void RawMaterialThatAlreadyHasRecipes_IsTreatedAsRecipeCapable()
  {
    // "TOTOPOS" es RawMaterial pero ya tiene versiones: debe seguir en el grupo principal.
    var totopos = Material(7252, "TOTOPOS", "RawMaterial", "StockItem");

    var options = RestaurantRecipeProductRules.BuildProductOptions(
      [Option(totopos)],
      [totopos],
      [7252]);

    Assert.Equal(RestaurantRecipeProductRules.ReadyGroup, Assert.Single(options).Group);
  }

  [Fact]
  public void ProductOptions_PutRecipeCapableFirstAndKeepIncomingOrderWithinEachGroup()
  {
    var aceite = Material(1, "ACEITE VEGETAL", "RawMaterial", "StockItem");
    var chilaquiles = Material(2, "CHILAQUILES", "FinishedGood", "MakeToOrder");
    var tortillas = Material(3, "TORTILLAS", "RawMaterial", "StockItem");
    var salsaVerde = Material(4, "salsa verde", "SemiFinished", "MakeToStock");

    var options = RestaurantRecipeProductRules.BuildProductOptions(
      [Option(aceite), Option(chilaquiles), Option(tortillas), Option(salsaVerde)],
      [aceite, chilaquiles, tortillas, salsaVerde],
      []);

    Assert.Equal([2, 4, 1, 3], options.Select(option => option.Id).ToArray());
    Assert.Equal(
      [
        RestaurantRecipeProductRules.ReadyGroup,
        RestaurantRecipeProductRules.ReadyGroup,
        RestaurantRecipeProductRules.OtherGroup,
        RestaurantRecipeProductRules.OtherGroup
      ],
      options.Select(option => option.Group ?? string.Empty).ToArray());
  }

  [Fact]
  public void ProductOptions_NeverDropMaterialsThatAreMissingFromTheCatalog()
  {
    var orphan = Option(Material(99, "MATERIAL SIN FICHA", "RawMaterial", "StockItem"));

    var options = RestaurantRecipeProductRules.BuildProductOptions([orphan], [], []);

    Assert.Equal(RestaurantRecipeProductRules.OtherGroup, Assert.Single(options).Group);
  }

  [Theory]
  [InlineData("MakeToOrder")]
  [InlineData("MakeToStock")]
  public void ClassificationNotice_IsSilentWhenTheMaterialIsAlreadyProduced(string fulfillmentMode)
  {
    var material = Material(1, "SALSA VERDE", "SemiFinished", fulfillmentMode);

    Assert.Null(RestaurantRecipeProductRules.ClassificationNotice(material));
  }

  [Fact]
  public void ClassificationNotice_WarnsAboutProductionForAPurchasedMaterial()
  {
    var material = Material(1, "TORTILLAS", "RawMaterial", "StockItem");

    var notice = RestaurantRecipeProductRules.ClassificationNotice(material);

    Assert.NotNull(notice);
    Assert.Contains("no podrás planear producción", notice, StringComparison.Ordinal);
    Assert.DoesNotContain("precio de compra", notice, StringComparison.Ordinal);
  }

  [Fact]
  public void ClassificationNotice_AlsoWarnsAboutCostWhenThePurchasePriceWouldWin()
  {
    var material = Material(7252, "TOTOPOS", "RawMaterial", "StockItem");
    material.BaseUnitPrice = 0.06275m;

    var notice = RestaurantRecipeProductRules.ClassificationNotice(material);

    Assert.NotNull(notice);
    Assert.Contains("precio de compra", notice, StringComparison.Ordinal);
  }

  [Fact]
  public void ClassificationNotice_IsSilentWithoutASelectedMaterial()
    => Assert.Null(RestaurantRecipeProductRules.ClassificationNotice(null));

  private static MaterialListItemDto Material(int id, string description, string productType, string fulfillmentMode)
    => new()
    {
      Id = id,
      MaterialCode = $"MAT-{id:D6}",
      Description = description,
      ProductType = productType,
      FulfillmentMode = fulfillmentMode,
      MaterialClass = "Consumable",
      Status = "ACTIVO",
      IsActive = true
    };

  private static RestaurantMaterialOption Option(MaterialListItemDto material)
    => new(material.Id, material.MaterialCode, material.Description);
}
