using System.Globalization;

namespace OrionERP.Web.Features.Logistica.Purchasing;

public static class PurchaseQuantityDisplay
{
  public static decimal NormalizePurchaseQuantity(decimal purchaseQuantity)
    => purchaseQuantity > 0m ? purchaseQuantity : 1m;

  public static bool UsesPurchaseUnit(string? purchaseUnitName)
    => !string.IsNullOrWhiteSpace(purchaseUnitName);

  public static decimal ToDisplayQuantity(decimal baseQuantity, decimal purchaseQuantity, string? purchaseUnitName)
  {
    if (!UsesPurchaseUnit(purchaseUnitName))
    {
      return baseQuantity;
    }

    return baseQuantity / NormalizePurchaseQuantity(purchaseQuantity);
  }

  public static decimal ToBaseQuantity(decimal displayQuantity, decimal purchaseQuantity, string? purchaseUnitName)
  {
    if (!UsesPurchaseUnit(purchaseUnitName))
    {
      return displayQuantity;
    }

    return displayQuantity * NormalizePurchaseQuantity(purchaseQuantity);
  }

  public static string FormatQuantity(
    decimal baseQuantity,
    decimal purchaseQuantity,
    string? baseUnitName,
    string? purchaseUnitName,
    CultureInfo culture)
  {
    var displayQuantity = ToDisplayQuantity(baseQuantity, purchaseQuantity, purchaseUnitName);
    var formattedQuantity = displayQuantity.ToString("N2", culture);
    var unitName = GetPrimaryUnitName(baseUnitName, purchaseUnitName);

    return string.IsNullOrWhiteSpace(unitName)
      ? formattedQuantity
      : $"{formattedQuantity} {unitName}";
  }

  public static string GetPrimaryUnitName(string? baseUnitName, string? purchaseUnitName)
    => UsesPurchaseUnit(purchaseUnitName)
      ? purchaseUnitName!.Trim()
      : string.IsNullOrWhiteSpace(baseUnitName)
        ? "unidad"
        : baseUnitName.Trim();

  public static string? BuildPresentationSummary(
    string? baseUnitName,
    decimal purchaseQuantity,
    string? purchaseUnitName,
    CultureInfo culture)
  {
    if (!UsesPurchaseUnit(purchaseUnitName) || string.IsNullOrWhiteSpace(baseUnitName))
    {
      return null;
    }

    var purchaseUnitText = purchaseUnitName!.Trim();
    var baseUnitText = baseUnitName.Trim();
    var normalizedPurchaseQuantity = NormalizePurchaseQuantity(purchaseQuantity);
    var hasEquivalentUnits = normalizedPurchaseQuantity == 1m
      && string.Equals(purchaseUnitText, baseUnitText, StringComparison.OrdinalIgnoreCase);

    if (hasEquivalentUnits)
    {
      return null;
    }

    return $"Internamente: 1 {purchaseUnitText} = {normalizedPurchaseQuantity.ToString("N2", culture)} {baseUnitText}";
  }
}
