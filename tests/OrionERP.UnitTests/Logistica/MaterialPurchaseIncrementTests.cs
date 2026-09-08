using System.Globalization;
using OrionERP.Application.Features.Logistica.Materials;

namespace OrionERP.UnitTests.Logistica;

public sealed class MaterialPurchaseIncrementTests
{
  private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-MX");

  [Theory]
  // Papel higiénico: 24 rollos por paquete, sólo paquetes cerrados.
  [InlineData(48, 24, "Paquete", 1, true)]
  [InlineData(36, 24, "Paquete", 1, false)]
  // Pollo: 1000 gramos por kilo, el proveedor despacha fracciones.
  [InlineData(1500, 1000, "Kilo", 0, true)]
  [InlineData(1234.5, 1000, "Kilo", 0, true)]
  // El mismo pollo cerrado sólo aceptaría kilos completos.
  [InlineData(1500, 1000, "Kilo", 1, false)]
  [InlineData(2000, 1000, "Kilo", 1, true)]
  // Escalón intermedio: medios kilos.
  [InlineData(1500, 1000, "Kilo", 0.5, true)]
  [InlineData(1750, 1000, "Kilo", 0.5, false)]
  public void IsValidQuantity_HonorsConfiguredIncrement(
    decimal baseQuantity,
    decimal purchaseQuantity,
    string purchaseUnitName,
    decimal increment,
    bool expected)
  {
    var result = MaterialPurchaseIncrement.IsValidQuantity(baseQuantity, purchaseQuantity, purchaseUnitName, increment);

    Assert.Equal(expected, result);
  }

  [Fact]
  public void IsValidQuantity_LeavesMaterialsWithoutPurchasePresentationUnconstrained()
  {
    Assert.True(MaterialPurchaseIncrement.IsValidQuantity(1.5m, 1m, purchaseUnitName: null, MaterialPurchaseIncrement.WholePresentation));
  }

  [Fact]
  public void IsValidQuantity_RequiresWholeUnits_WhenPresentationExistsAtQuantityOne()
  {
    Assert.False(MaterialPurchaseIncrement.IsValidQuantity(1.5m, 1m, "Paquete", MaterialPurchaseIncrement.WholePresentation));
    Assert.True(MaterialPurchaseIncrement.IsValidQuantity(2m, 1m, "Paquete", MaterialPurchaseIncrement.WholePresentation));
  }

  [Theory]
  [InlineData(null, 1)]
  [InlineData(-3.0, 1)]
  [InlineData(0.0, 0)]
  [InlineData(0.5, 0.5)]
  public void Normalize_TreatsMissingAndNegativeValuesAsWholePresentation(double? increment, decimal expected)
  {
    var result = MaterialPurchaseIncrement.Normalize(increment.HasValue ? (decimal)increment.Value : null);

    Assert.Equal(expected, result);
  }

  [Fact]
  public void RoundUpToIncrement_FillsThePresentation_WhenOnlyWholeUnitsAreSold()
  {
    var result = MaterialPurchaseIncrement.RoundUpToIncrement(30m, 24m, MaterialPurchaseIncrement.WholePresentation);

    Assert.Equal(48m, result);
  }

  [Fact]
  public void RoundUpToIncrement_KeepsTheRawNeed_WhenFractionsAreSold()
  {
    var result = MaterialPurchaseIncrement.RoundUpToIncrement(1370m, 1000m, MaterialPurchaseIncrement.Fractional);

    Assert.Equal(1370m, result);
  }

  [Fact]
  public void RoundUpToIncrement_RisesToTheNextStep_WhenTheIncrementIsPartial()
  {
    var result = MaterialPurchaseIncrement.RoundUpToIncrement(1370m, 1000m, 0.5m);

    Assert.Equal(1500m, result);
  }

  [Fact]
  public void DescribeRequirement_NamesThePresentationForWholeUnits()
  {
    Assert.Equal(
      "24.00 Rollo por Paquete",
      MaterialPurchaseIncrement.DescribeRequirement("Rollo", "Paquete", 24m, MaterialPurchaseIncrement.WholePresentation, Culture));

    Assert.Equal(
      "1 Paquete",
      MaterialPurchaseIncrement.DescribeRequirement("Paquete", "Paquete", 1m, MaterialPurchaseIncrement.WholePresentation, Culture));
  }

  [Fact]
  public void DescribeRequirement_NamesTheStepWhenItIsNotTheFullPresentation()
  {
    Assert.Equal(
      "0.50 Kilo",
      MaterialPurchaseIncrement.DescribeRequirement("Gramo", "Kilo", 1000m, 0.5m, Culture));
  }

  [Fact]
  public void DescribeMode_ExplainsBothCasesToTheUser()
  {
    Assert.Equal("Solo presentaciones completas", MaterialPurchaseIncrement.DescribeMode(MaterialPurchaseIncrement.WholePresentation));
    Assert.Equal("Se puede comprar fraccionado", MaterialPurchaseIncrement.DescribeMode(MaterialPurchaseIncrement.Fractional));
  }
}
