namespace OrionERP.Application.Features.Logistica.Purchasing;

public static class PurchaseReceiptAmountCalculator
{
  public const decimal StandardIvaRate = 0.16m;
  public const int MoneyScale = 2;
  public const int UnitCostScale = 6;

  public static PurchaseReceiptAmounts Calculate(decimal totalAmount, bool includesIva)
  {
    if (totalAmount < 0m)
    {
      throw new ArgumentOutOfRangeException(nameof(totalAmount), "El total no puede ser negativo.");
    }

    var total = decimal.Round(totalAmount, MoneyScale, MidpointRounding.AwayFromZero);
    if (!includesIva)
    {
      return new PurchaseReceiptAmounts(total, 0m, total);
    }

    var subtotal = decimal.Round(total / (1m + StandardIvaRate), MoneyScale, MidpointRounding.AwayFromZero);
    var iva = total - subtotal;
    return new PurchaseReceiptAmounts(subtotal, iva, total);
  }

  public static decimal CalculateBaseUnitCost(decimal totalAmount, bool includesIva, decimal baseQuantity)
  {
    if (baseQuantity <= 0m)
    {
      throw new ArgumentOutOfRangeException(nameof(baseQuantity), "La cantidad debe ser mayor a cero.");
    }

    var amounts = Calculate(totalAmount, includesIva);
    return decimal.Round(amounts.SubtotalAmount / baseQuantity, UnitCostScale, MidpointRounding.AwayFromZero);
  }
}

public readonly record struct PurchaseReceiptAmounts(
  decimal SubtotalAmount,
  decimal IvaAmount,
  decimal TotalAmount);
