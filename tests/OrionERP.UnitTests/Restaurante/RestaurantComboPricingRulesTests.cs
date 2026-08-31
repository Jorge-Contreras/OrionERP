using OrionERP.Application.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantComboPricingRulesTests
{
  [Fact]
  public void CalculateUnitPrice_AddsOptionAndComponentModifierSupplementsOnce()
  {
    var selections = new[]
    {
      new RestaurantComboPriceSelection(0m, 1m, [30m]),
      new RestaurantComboPriceSelection(15m, 1m, Array.Empty<decimal>())
    };

    var price = RestaurantComboPricingRules.CalculateUnitPrice(95m, selections);

    Assert.Equal(140m, price);
  }

  [Fact]
  public void CalculateUnitPrice_MultipliesComponentModifiersByConfiguredComponentQuantity()
  {
    var selections = new[]
    {
      new RestaurantComboPriceSelection(5m, 2m, [3.25m, 1.50m])
    };

    var price = RestaurantComboPricingRules.CalculateUnitPrice(80m, selections);

    Assert.Equal(94.50m, price);
  }

  [Fact]
  public void CalculateUnitPrice_RoundsAwayFromZeroAtCurrencyPrecision()
  {
    var selections = new[]
    {
      new RestaurantComboPriceSelection(0.005m, 1m, Array.Empty<decimal>())
    };

    Assert.Equal(10.01m, RestaurantComboPricingRules.CalculateUnitPrice(10m, selections));
  }

  [Fact]
  public void CalculateUnitPrice_RejectsNegativeBasePrice()
  {
    Assert.Throws<InvalidOperationException>(() =>
      RestaurantComboPricingRules.CalculateUnitPrice(-1m, Array.Empty<RestaurantComboPriceSelection>()));
  }
}
