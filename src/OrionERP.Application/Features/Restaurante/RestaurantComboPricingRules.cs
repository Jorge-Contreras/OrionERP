namespace OrionERP.Application.Features.Restaurante;

public readonly record struct RestaurantComboPriceSelection(
  decimal OptionPriceDelta,
  decimal ComponentQuantity,
  IReadOnlyCollection<decimal> ModifierPriceDeltas);

public static class RestaurantComboPricingRules
{
  public static decimal CalculateUnitSupplement(IEnumerable<RestaurantComboPriceSelection> selections)
  {
    ArgumentNullException.ThrowIfNull(selections);
    return decimal.Round(selections.Sum(selection =>
      selection.OptionPriceDelta +
      selection.ModifierPriceDeltas.Sum() * selection.ComponentQuantity), 2, MidpointRounding.AwayFromZero);
  }

  public static decimal CalculateUnitPrice(
    decimal basePrice,
    IEnumerable<RestaurantComboPriceSelection> selections)
  {
    if (basePrice < 0)
    {
      throw new InvalidOperationException("El precio base del combo no puede ser negativo.");
    }
    return decimal.Round(basePrice + CalculateUnitSupplement(selections), 2, MidpointRounding.AwayFromZero);
  }
}
