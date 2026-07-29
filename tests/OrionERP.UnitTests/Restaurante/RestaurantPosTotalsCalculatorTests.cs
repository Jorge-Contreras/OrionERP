using OrionERP.Application.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantPosTotalsCalculatorTests
{
  [Fact]
  public void Calculate_BreaksIncludedTaxOutOfTheSellingPrice()
  {
    var totals = RestaurantPosTotalsCalculator.Calculate(
      merchandiseTotal: 232m,
      discountTotal: 0,
      delivery: 0,
      taxRate: 0.16m,
      pricesIncludeTax: true);

    Assert.Equal(200m, totals.SubtotalBeforeTax);
    Assert.Equal(32m, totals.Tax);
    Assert.Equal(232m, totals.Total);
  }

  [Fact]
  public void Calculate_AppliesIncludedTaxBreakdownAfterDiscount()
  {
    var totals = RestaurantPosTotalsCalculator.Calculate(
      merchandiseTotal: 232m,
      discountTotal: 23.20m,
      delivery: 30m,
      taxRate: 0.16m,
      pricesIncludeTax: true);

    Assert.Equal(208.80m, totals.MerchandiseAfterDiscount);
    Assert.Equal(180m, totals.SubtotalBeforeTax);
    Assert.Equal(28.80m, totals.Tax);
    Assert.Equal(238.80m, totals.Total);
    Assert.Equal(totals.Total, totals.SubtotalBeforeTax + totals.Tax + totals.Delivery);
  }

  [Fact]
  public void Calculate_AddsTaxWhenPricesExcludeIt()
  {
    var totals = RestaurantPosTotalsCalculator.Calculate(
      merchandiseTotal: 200m,
      discountTotal: 20m,
      delivery: 30m,
      taxRate: 0.16m,
      pricesIncludeTax: false);

    Assert.Equal(180m, totals.SubtotalBeforeTax);
    Assert.Equal(28.80m, totals.Tax);
    Assert.Equal(238.80m, totals.Total);
  }
}
