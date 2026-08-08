namespace OrionERP.Application.Features.Logistica.Materials;

public static class MaterialPriceCalculator
{
  public const int BaseUnitPriceScale = 6;
  public const int PurchasePresentationPriceScale = 2;

  public static decimal? NormalizeBaseUnitPrice(decimal? baseUnitPrice)
    => baseUnitPrice.HasValue
      ? decimal.Round(baseUnitPrice.Value, BaseUnitPriceScale, MidpointRounding.AwayFromZero)
      : null;

  public static decimal? CalculatePurchasePresentationPrice(decimal? baseUnitPrice, decimal purchaseQuantity)
  {
    if (!baseUnitPrice.HasValue || purchaseQuantity <= 0m)
    {
      return null;
    }

    return decimal.Round(
      baseUnitPrice.Value * purchaseQuantity,
      PurchasePresentationPriceScale,
      MidpointRounding.AwayFromZero);
  }

  public static decimal? CalculateBaseUnitPrice(decimal? purchasePresentationPrice, decimal purchaseQuantity)
  {
    if (!purchasePresentationPrice.HasValue || purchaseQuantity <= 0m)
    {
      return null;
    }

    return decimal.Round(
      purchasePresentationPrice.Value / purchaseQuantity,
      BaseUnitPriceScale,
      MidpointRounding.AwayFromZero);
  }
}
