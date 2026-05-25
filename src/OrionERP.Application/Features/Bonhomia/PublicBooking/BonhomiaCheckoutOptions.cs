namespace OrionERP.Application.Features.Bonhomia.PublicBooking;

public sealed class BonhomiaCheckoutOptions
{
  public const string SectionName = "BonhomiaCheckout";

  public string Environment { get; set; } = "Sandbox";
  public string Currency { get; set; } = "MXN";
  public string PayPalClientId { get; set; } = string.Empty;
  public string PayPalClientSecret { get; set; } = string.Empty;
  public int QuoteTokenLifetimeMinutes { get; set; } = 30;
  public int PdfTokenLifetimeMinutes { get; set; } = 30;
  public int AvailabilityDays { get; set; } = 60;
  public int MaxStayNights { get; set; } = 60;
  public string? PublicBaseUrl { get; set; }
  public string AccountingRfc { get; set; } = "OHM191112Q26";
  public int AccountingCategoryId { get; set; } = 19;
  public string AccountingPaymentForm { get; set; } = "03";
  public string AccountingAccount { get; set; } = "ORION HABITAT DE MEXICO";

  public bool IsPayPalConfigured
    => !string.IsNullOrWhiteSpace(PayPalClientId)
      && !string.IsNullOrWhiteSpace(PayPalClientSecret);

  public bool UseLivePayPal
    => string.Equals(Environment, "Live", StringComparison.OrdinalIgnoreCase)
      || string.Equals(Environment, "Production", StringComparison.OrdinalIgnoreCase);

  public Uri PayPalBaseUri
    => UseLivePayPal
      ? new Uri("https://api-m.paypal.com")
      : new Uri("https://api-m.sandbox.paypal.com");
}
