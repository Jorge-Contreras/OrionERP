using System.Globalization;
using OrionERP.Application.Features.Logistica.Materials;

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

    // Se redondea a la escala real de las columnas de cantidad. Con presentaciones completas el
    // producto siempre fue exacto; al permitir fracciones evita que lo validado y lo guardado
    // difieran en la última cifra.
    return decimal.Round(
      displayQuantity * NormalizePurchaseQuantity(purchaseQuantity),
      MaterialPurchaseIncrement.QuantityScale,
      MidpointRounding.AwayFromZero);
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

  public static string FormatBaseUnitPrice(decimal? baseUnitPrice, string? baseUnitName, CultureInfo culture)
  {
    if (!baseUnitPrice.HasValue)
    {
      return "-";
    }

    var formattedPrice = baseUnitPrice.Value.ToString("#,0.00####", culture);
    var unitSuffix = string.IsNullOrWhiteSpace(baseUnitName) ? "unidad base" : baseUnitName.Trim();
    return $"{culture.NumberFormat.CurrencySymbol}{formattedPrice} / {unitSuffix}";
  }

  public static string? BuildPurchasePresentationPriceEquivalent(
    decimal? baseUnitPrice,
    decimal purchaseQuantity,
    string? purchaseUnitName,
    CultureInfo culture)
  {
    if (!UsesPurchaseUnit(purchaseUnitName))
    {
      return null;
    }

    var presentationPrice = MaterialPriceCalculator.CalculatePurchasePresentationPrice(baseUnitPrice, purchaseQuantity);
    return presentationPrice.HasValue
      ? $"Equivale a {presentationPrice.Value.ToString("C2", culture)} / {purchaseUnitName!.Trim()}"
      : null;
  }
}
