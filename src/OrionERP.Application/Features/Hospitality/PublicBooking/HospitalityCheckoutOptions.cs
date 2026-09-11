namespace OrionERP.Application.Features.Bonhomia.PublicBooking;

public sealed class BonhomiaCheckoutOptions
{
  public const string SectionName = "BonhomiaCheckout";

  public string Environment { get; set; } = "Sandbox";
  public string Currency { get; set; } = "MXN";
  public string PayPalClientId { get; set; } = string.Empty;
  public string PayPalClientSecret { get; set; } = string.Empty;
  public string PayPalLocale { get; set; } = "es_MX";
  public int QuoteTokenLifetimeMinutes { get; set; } = 30;
  public int PdfTokenLifetimeMinutes { get; set; } = 30;
  public int AvailabilityDays { get; set; } = 60;
  public int MaxStayNights { get; set; } = 60;
  public string TimeZone { get; set; } = BonhomiaBookingCutoffPolicy.DefaultTimeZone;
  public string? PublicBaseUrl { get; set; }
  public string AccountingRfc { get; set; } = string.Empty;
  public string AccountingPaymentForm { get; set; } = "03";
  public string AccountingAccount { get; set; } = string.Empty;
  public string PublicName { get; set; } = "Hospedaje";
  public string ReservationSourceLabel { get; set; } = "Website de hospedaje";
  public string PdfFilePrefix { get; set; } = "reservacion";

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
