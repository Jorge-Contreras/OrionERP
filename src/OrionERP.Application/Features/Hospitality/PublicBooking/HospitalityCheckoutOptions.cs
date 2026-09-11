namespace OrionERP.Application.Features.Hospitality.PublicBooking;

public sealed class HospitalityCheckoutOptions
{
  public const string SectionName = "HospitalityCheckout";
  public const string LegacySectionName = "BonhomiaCheckout";

  public string Environment { get; set; } = "Sandbox";
  public string Currency { get; set; } = "MXN";
  public string PayPalClientId { get; set; } = string.Empty;
  public string PayPalClientSecret { get; set; } = string.Empty;
  public string PayPalLocale { get; set; } = "es_MX";
  public int QuoteTokenLifetimeMinutes { get; set; } = 30;
  public int PdfTokenLifetimeMinutes { get; set; } = 30;
  public int AvailabilityDays { get; set; } = 60;
  public int MaxStayNights { get; set; } = 60;
  public string TimeZone { get; set; } = HospitalityBookingCutoffPolicy.DefaultTimeZone;
  public TimeOnly SameDayCutoff { get; set; } = new(17, 0);
  public string? PublicBaseUrl { get; set; }
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
