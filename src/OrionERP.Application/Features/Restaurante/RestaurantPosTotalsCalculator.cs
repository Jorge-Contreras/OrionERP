namespace OrionERP.Application.Features.Restaurante;

public readonly record struct RestaurantPosTotalsBreakdown(
  decimal MerchandiseAfterDiscount,
  decimal SubtotalBeforeTax,
  decimal Tax,
  decimal Delivery,
  decimal Total);

public static class RestaurantPosTotalsCalculator
{
  public static RestaurantPosTotalsBreakdown Calculate(
    decimal merchandiseTotal,
    decimal discountTotal,
    decimal delivery,
    decimal taxRate,
    bool pricesIncludeTax)
  {
    var merchandiseAfterDiscount = RoundCurrency(Math.Max(0, merchandiseTotal - discountTotal));
    var normalizedDelivery = RoundCurrency(Math.Max(0, delivery));
    var normalizedTaxRate = Math.Max(0, taxRate);

    decimal subtotalBeforeTax;
    decimal tax;
    if (pricesIncludeTax)
    {
      tax = normalizedTaxRate == 0
        ? 0
        : RoundCurrency(merchandiseAfterDiscount - merchandiseAfterDiscount / (1 + normalizedTaxRate));
      subtotalBeforeTax = RoundCurrency(merchandiseAfterDiscount - tax);
    }
    else
    {
      subtotalBeforeTax = merchandiseAfterDiscount;
      tax = RoundCurrency(subtotalBeforeTax * normalizedTaxRate);
    }

    var total = RoundCurrency(subtotalBeforeTax + tax + normalizedDelivery);
    return new RestaurantPosTotalsBreakdown(
      merchandiseAfterDiscount,
      subtotalBeforeTax,
      tax,
      normalizedDelivery,
      total);
  }

  private static decimal RoundCurrency(decimal value)
    => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
