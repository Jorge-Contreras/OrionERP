namespace OrionERP.Application.Features.Payments.PayPal;

/// <summary>
/// Common PayPal REST API credentials and endpoint selection. Public websites
/// should derive their own options type from this class so each process keeps
/// an independent configuration section, even when merchant credentials are
/// temporarily shared.
/// </summary>
public class PayPalClientOptions
{
  public string Environment { get; set; } = "Sandbox";
  public string PayPalClientId { get; set; } = string.Empty;
  public string PayPalClientSecret { get; set; } = string.Empty;
  public string PayPalWebhookId { get; set; } = string.Empty;

  public bool IsPayPalConfigured
    => !string.IsNullOrWhiteSpace(PayPalClientId)
      && !string.IsNullOrWhiteSpace(PayPalClientSecret);

  public bool IsWebhookVerificationConfigured
    => IsPayPalConfigured && !string.IsNullOrWhiteSpace(PayPalWebhookId);

  public bool UseLivePayPal
    => string.Equals(Environment, "Live", StringComparison.OrdinalIgnoreCase)
      || string.Equals(Environment, "Production", StringComparison.OrdinalIgnoreCase);

  public Uri PayPalBaseUri
    => UseLivePayPal
      ? new Uri("https://api-m.paypal.com")
      : new Uri("https://api-m.sandbox.paypal.com");
}
