namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantModifierQuantityTests
{
  [Fact]
  public void Pos_OffersAStepperWhenTheGroupAllowsMoreThanOnePick()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPosPage.razor");
    var styles = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPosPage.razor.css");

    Assert.Contains("StepModifier(selectedModifierIds, group, option, 1)", page, StringComparison.Ordinal);
    Assert.Contains("StepModifier(componentDraft.ModifierIds,group,modifier,1)", page, StringComparison.Ordinal);
    Assert.Contains("GroupSelectionCount(selectedModifierIds, group) >= group.MaxSelections", page, StringComparison.Ordinal);
    Assert.Contains("private List<long> selectedModifierIds = [];", page, StringComparison.Ordinal);
    Assert.Contains(".pos-modifier__stepper", styles, StringComparison.Ordinal);
  }

  [Fact]
  public void OrderService_CountsRepeatedPicksAndPersistsTheirQuantity()
  {
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOrderService.cs");

    // El máximo del grupo pasó a ser un presupuesto de selecciones, así que repetir ya no es un error.
    Assert.DoesNotContain("No se puede repetir el mismo modificador", service, StringComparison.Ordinal);
    Assert.Contains("var selected = selectedOptionIds.Count(groupOptionIds.Contains);", service, StringComparison.Ordinal);
    Assert.Contains("selectedOptionIds.Count(optionId => optionId == row.Id)", service, StringComparison.Ordinal);
    Assert.Contains("@Name,@PriceDelta,@Quantity,@GroupName,@EffectKind", service, StringComparison.Ordinal);
    Assert.Contains("modifiers.Sum(item => item.Option.PriceDelta * item.Quantity)", service, StringComparison.Ordinal);
    Assert.Contains(
      "modifiers.SelectMany(modifier => Enumerable.Repeat(modifier.Option.Id, modifier.Quantity)).ToArray()",
      service,
      StringComparison.Ordinal);
  }

  [Fact]
  public void MenuAdmin_LimitsEffectUnitsToTheMaterialBaseUnitAndItsConversions()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantMenuManagementPage.razor");

    Assert.Contains("BomService.GetRecipeUnitOptionsAsync(rfc)", page, StringComparison.Ordinal);
    Assert.Contains("EffectUnitOptions(delta)", page, StringComparison.Ordinal);
    Assert.Contains("SetEffectMaterial(delta,materialId)", page, StringComparison.Ordinal);
    Assert.DoesNotContain("foreach(var unit in materialCatalog.Units)", page, StringComparison.Ordinal);
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath)));
}
