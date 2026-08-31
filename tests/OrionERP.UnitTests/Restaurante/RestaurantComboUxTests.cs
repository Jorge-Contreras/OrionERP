using OrionERP.Application.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantComboUxTests
{
  [Fact]
  public void Admin_SeparatesComboProductsAndUsesSemanticIngredientEffects()
  {
    var products = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantAdminPage.razor");
    var menus = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantMenuManagementPage.razor");

    Assert.Contains("productEditor.ProductKind", products, StringComparison.Ordinal);
    Assert.Contains("RestaurantProductKinds.Combo", products, StringComparison.Ordinal);
    Assert.Contains("if (!IsComboProduct)", products, StringComparison.Ordinal);
    Assert.Contains("CatalogService.GetCombosAsync", menus, StringComparison.Ordinal);
    Assert.Contains("CatalogService.SaveComboAsync", menus, StringComparison.Ordinal);
    Assert.Contains("RestaurantModifierEffectKinds.AddQuantity", menus, StringComparison.Ordinal);
    Assert.Contains("RestaurantModifierEffectKinds.RemoveIngredient", menus, StringComparison.Ordinal);
    Assert.Contains("Crear sustitución", menus, StringComparison.Ordinal);
  }

  [Fact]
  public void Pos_PersonalizesComponentsSplitsOneUnitAndPersistsDraftV2()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPosPage.razor");

    Assert.Contains("class=\"pos-cart-line__customize\"", page, StringComparison.Ordinal);
    Assert.Contains("selectedProduct.ComboSlots", page, StringComparison.Ordinal);
    Assert.Contains("ComboSelections = item.ComboComponents.Select", page, StringComparison.Ordinal);
    Assert.Contains("if (source.Quantity > 1)", page, StringComparison.Ordinal);
    Assert.Contains("source.Quantity--;", page, StringComparison.Ordinal);
    Assert.Contains("AddOrMergeCartLine(candidate)", page, StringComparison.Ordinal);
    Assert.Contains("Version = 2", page, StringComparison.Ordinal);
    Assert.Contains("maxlength=\"500\"", page, StringComparison.Ordinal);
  }

  [Fact]
  public void PosPricePreview_UsesSharedRuleForMultipleComponentQuantity()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPosPage.razor");
    var selections = new[]
    {
      new RestaurantComboPriceSelection(15m, 2m, [3.25m, 1.50m])
    };

    var expected = RestaurantComboPricingRules.CalculateUnitPrice(95m, selections);

    Assert.Equal(119.50m, expected);
    Assert.Contains(
      "RestaurantComboPricingRules.CalculateUnitPrice(selectedProduct.Price, SelectedComboPriceSelections)",
      page,
      StringComparison.Ordinal);
    Assert.Contains(
      "new RestaurantComboPriceSelection(0, option.Quantity, [modifier.PriceDelta])",
      page,
      StringComparison.Ordinal);
  }

  [Fact]
  public void DesktopPriceSummary_DoesNotShareTheFocusedContentScrollContainer()
  {
    var styles = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPosPage.razor.css");

    Assert.Contains(
      ".pos-personalization-layout { display:grid; grid-template-columns:minmax(0,1fr) 280px; grid-template-rows:minmax(0,1fr); min-height:0; height:calc(90vh - 92px); max-height:calc(90vh - 92px); overflow:clip; }",
      styles,
      StringComparison.Ordinal);
    Assert.Contains(
      ".pos-personalization-content { min-height:0; overflow:auto; overscroll-behavior:contain;",
      styles,
      StringComparison.Ordinal);
    Assert.Contains(".pos-personalization-summary {", styles, StringComparison.Ordinal);
    Assert.Contains("min-height:0", styles, StringComparison.Ordinal);
    Assert.Contains(
      ".pos-personalization-layout { grid-template-columns:1fr; grid-template-rows:auto auto; height:auto; max-height:calc(96vh - 92px); overflow:auto; }",
      styles,
      StringComparison.Ordinal);
  }

  [Fact]
  public void PublicMenu_ExplainsCombosWithoutOrderingControls()
  {
    var page = ReadRepoFile("src/OrionERP.Bruno.Web/Features/BrunoMenuPage.razor");

    Assert.Contains("menu-card__combo-badge", page, StringComparison.Ordinal);
    Assert.Contains("ComboSlotSummary", page, StringComparison.Ordinal);
    Assert.Contains("Personalízalo al ordenar en caja", page, StringComparison.Ordinal);
    Assert.DoesNotContain("Agregar a la orden", page, StringComparison.Ordinal);
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath)));
}
