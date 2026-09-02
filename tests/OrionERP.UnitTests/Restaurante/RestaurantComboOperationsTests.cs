namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantComboOperationsTests
{
  [Fact]
  public void OrderService_RevalidatesComboAndPersistsParentChildSnapshots()
  {
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOrderService.cs");

    Assert.Contains("BuildComboPlans(request, products, comboRows, menuSections, modifierRows)", service, StringComparison.Ordinal);
    Assert.Contains("La versión actual no permite incluir un combo dentro de otro combo", service, StringComparison.Ordinal);
    Assert.Contains("configura su ruta operacional en el combo", service, StringComparison.Ordinal);
    Assert.Contains("RestaurantOrderLineKinds.ComboComponent", service, StringComparison.Ordinal);
    Assert.Contains("ParentProductNameSnapshot", service, StringComparison.Ordinal);
    Assert.Contains("ComboSlotNameSnapshot", service, StringComparison.Ordinal);
    Assert.Contains("ModifierGroupNameSnapshot", service, StringComparison.Ordinal);
    Assert.Contains("component.Modifiers.SelectMany(modifier => Enumerable.Repeat(modifier.Option.PriceDelta, modifier.Quantity))", service, StringComparison.Ordinal);
  }

  [Fact]
  public void OrderService_AppliesFinanceToParentAndInventoryToComponents()
  {
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOrderService.cs");

    Assert.Contains("Lines = pricedLines.Select", service, StringComparison.Ordinal);
    Assert.Contains("component.TotalQuantity(line.Request.Quantity)", service, StringComparison.Ordinal);
    Assert.Contains("CalculateRequirementCostAsync(conn, tx, rfc, inventoryPlan.Requirements, ct)", service, StringComparison.Ordinal);
    Assert.Contains("RestaurantComboPricingRules.CalculateUnitPrice", service, StringComparison.Ordinal);
    Assert.Contains("LineKind<>'Combo'", service, StringComparison.Ordinal);
  }

  [Fact]
  public void Reports_ExcludeComponentsFromSalesAndUseInventoryAndModifierSnapshotsForCost()
  {
    var backoffice = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantBackofficeService.cs");
    var analytics = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantAnalyticsService.cs");

    Assert.Contains("lineInfo.LineKind<>'ComboComponent'", backoffice, StringComparison.Ordinal);
    Assert.Contains("line.LineKind<>'ComboComponent'", analytics, StringComparison.Ordinal);
    Assert.Contains("parentLine.LineKind='Combo'", analytics, StringComparison.Ordinal);
    Assert.Contains("componentLine.ParentOrderLineId=parentLine.Id", analytics, StringComparison.Ordinal);
    Assert.Contains("OrderLineModifierIngredientEffect", analytics, StringComparison.Ordinal);
    Assert.Contains("effect.BaseQuantityDelta", analytics, StringComparison.Ordinal);
    Assert.Contains("effect.BaseQuantityDelta*effect.FrozenBaseUnitCost", analytics, StringComparison.Ordinal);
    Assert.Contains("effect.EffectKind='RemoveIngredient'", analytics, StringComparison.Ordinal);
    Assert.Contains("removed.FrozenBaseUnitCost", analytics, StringComparison.Ordinal);
    Assert.Contains("SUM(orderInfo.TheoreticalCost)", analytics, StringComparison.Ordinal);
  }

  [Fact]
  public void CatalogProtectsNestedComponentsAndInactiveModifierMaterials()
  {
    var catalog = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantCatalogService.cs");

    Assert.Contains("ComponentProductId=@ProductId", catalog, StringComparison.Ordinal);
    Assert.Contains("no puede convertirse en combo", catalog, StringComparison.Ordinal);
    Assert.Contains("Id IN @Ids AND IsActive=1", catalog, StringComparison.Ordinal);
    Assert.Contains("material no pertenece al RFC activo o está inactivo", catalog, StringComparison.Ordinal);
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath)));
}
