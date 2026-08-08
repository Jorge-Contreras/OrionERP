using OrionERP.Application.Features.Logistica.Materials;

namespace OrionERP.UnitTests.Logistica;

public sealed class MaterialPriceCalculatorTests
{
  [Theory]
  [InlineData(0.054267, 2027, 110.00)]
  [InlineData(16.899, 23.67, 400.00)]
  [InlineData(0, 24, 0)]
  public void CalculatePurchasePresentationPrice_RoundsCurrencyAwayFromZero(
    decimal baseUnitPrice,
    decimal purchaseQuantity,
    decimal expected)
  {
    var result = MaterialPriceCalculator.CalculatePurchasePresentationPrice(baseUnitPrice, purchaseQuantity);

    Assert.Equal(expected, result);
  }

  [Theory]
  [InlineData(110, 2027, 0.054267)]
  [InlineData(350, 34000, 0.010294)]
  [InlineData(0, 24, 0)]
  public void CalculateBaseUnitPrice_RoundsToSixDecimalsAwayFromZero(
    decimal presentationPrice,
    decimal purchaseQuantity,
    decimal expected)
  {
    var result = MaterialPriceCalculator.CalculateBaseUnitPrice(presentationPrice, purchaseQuantity);

    Assert.Equal(expected, result);
  }

  [Fact]
  public void Conversion_ReturnsNull_WhenPriceIsClearedOrQuantityIsInvalid()
  {
    Assert.Null(MaterialPriceCalculator.CalculatePurchasePresentationPrice(null, 24m));
    Assert.Null(MaterialPriceCalculator.CalculatePurchasePresentationPrice(2.82m, 0m));
    Assert.Null(MaterialPriceCalculator.CalculateBaseUnitPrice(null, 24m));
    Assert.Null(MaterialPriceCalculator.CalculateBaseUnitPrice(67.68m, -1m));
  }

  [Fact]
  public void PresentationRoundTrip_PreservesLargePackageCurrencyValue()
  {
    var baseUnitPrice = MaterialPriceCalculator.CalculateBaseUnitPrice(350m, 34000m);

    Assert.Equal(0.010294m, baseUnitPrice);
    Assert.Equal(350m, MaterialPriceCalculator.CalculatePurchasePresentationPrice(baseUnitPrice, 34000m));
  }

  [Fact]
  public void NormalizeBaseUnitPrice_UsesSixDecimalPrecision()
  {
    Assert.Equal(1.234568m, MaterialPriceCalculator.NormalizeBaseUnitPrice(1.2345675m));
    Assert.Null(MaterialPriceCalculator.NormalizeBaseUnitPrice(null));
  }
}
