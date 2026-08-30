using OrionERP.Application.Features.Logistica.Materials;

namespace OrionERP.UnitTests.Logistica;

public sealed class MaterialProductionRoleTests
{
  [Theory]
  [InlineData(MaterialProductionRoles.PurchasedInput, "RawMaterial", "StockItem")]
  [InlineData(MaterialProductionRoles.Resale, "Resale", "StockItem")]
  [InlineData(MaterialProductionRoles.BatchSubProduct, "SemiFinished", "MakeToStock")]
  [InlineData(MaterialProductionRoles.OnDemandSubRecipe, "SemiFinished", "MakeToOrder")]
  [InlineData(MaterialProductionRoles.OnDemandFinishedGood, "FinishedGood", "MakeToOrder")]
  [InlineData(MaterialProductionRoles.BatchFinishedGood, "FinishedGood", "MakeToStock")]
  public void EveryRoleMapsToExactlyOnePair(string key, string productType, string fulfillmentMode)
  {
    var role = MaterialProductionRoles.Find(key);

    Assert.NotNull(role);
    Assert.Equal(productType, role.ProductType);
    Assert.Equal(fulfillmentMode, role.FulfillmentMode);
    Assert.Equal(key, MaterialProductionRoles.Resolve(productType, fulfillmentMode));
  }

  [Fact]
  public void RolesAndPairsAreBothUnique()
  {
    Assert.Equal(
      MaterialProductionRoles.All.Count,
      MaterialProductionRoles.All.Select(role => role.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    Assert.Equal(
      MaterialProductionRoles.All.Count,
      MaterialProductionRoles.All.Select(role => $"{role.ProductType}|{role.FulfillmentMode}").Distinct(StringComparer.OrdinalIgnoreCase).Count());
  }

  [Fact]
  public void OnlyBatchRolesAreProducible()
  {
    var producible = MaterialProductionRoles.All.Where(role => role.IsProducible).Select(role => role.Key).ToArray();

    Assert.Equal(
      [MaterialProductionRoles.BatchSubProduct, MaterialProductionRoles.BatchFinishedGood],
      producible);
    // Producible y "MakeToStock" deben significar lo mismo: es lo que exige la página de producción.
    Assert.All(
      MaterialProductionRoles.All,
      role => Assert.Equal(role.IsProducible, role.FulfillmentMode == "MakeToStock"));
  }

  [Fact]
  public void RolesThatRequireARecipeAreExactlyTheOnesThatAreNotPurchased()
    => Assert.All(
      MaterialProductionRoles.All,
      role => Assert.Equal(role.RequiresRecipe, role.FulfillmentMode != "StockItem"));

  [Theory]
  [InlineData("FinishedGood", "StockItem")]   // reventa mal tipada
  [InlineData("SemiFinished", "StockItem")]   // semielaborado que no se puede producir
  [InlineData("RawMaterial", "MakeToOrder")]  // insumo marcado para explotarse
  [InlineData("Resale", "MakeToStock")]
  [InlineData(null, null)]
  [InlineData("", "")]
  public void InvalidPairsResolveToUnclassifiedInsteadOfGuessing(string? productType, string? fulfillmentMode)
  {
    Assert.Equal(MaterialProductionRoles.Unclassified, MaterialProductionRoles.Resolve(productType, fulfillmentMode));
    Assert.Null(MaterialProductionRoles.Find(MaterialProductionRoles.Unclassified));
  }

  [Fact]
  public void ResolveIgnoresCasingAndSurroundingSpace()
    => Assert.Equal(
      MaterialProductionRoles.BatchSubProduct,
      MaterialProductionRoles.Resolve("  semifinished ", "MAKETOSTOCK"));

  [Fact]
  public void SchemaDefaultIsOnlyThePurchasedInputPair()
  {
    Assert.True(MaterialProductionRoles.IsSchemaDefault("RawMaterial", "StockItem"));
    Assert.False(MaterialProductionRoles.IsSchemaDefault("SemiFinished", "MakeToStock"));
    Assert.False(MaterialProductionRoles.IsSchemaDefault(null, null));
  }

  [Fact]
  public void EveryRoleCarriesLabelAndDescriptionForTheSelector()
    => Assert.All(MaterialProductionRoles.All, role =>
    {
      Assert.False(string.IsNullOrWhiteSpace(role.Label));
      Assert.False(string.IsNullOrWhiteSpace(role.Description));
    });
}
