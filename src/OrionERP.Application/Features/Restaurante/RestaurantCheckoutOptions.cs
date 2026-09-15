using OrionERP.Application.Features.Payments.PayPal;

namespace OrionERP.Application.Features.Restaurante;

public sealed class RestaurantCheckoutOptions : PayPalClientOptions
{
  public const string SectionName = "RestaurantCheckout";

  public string Currency { get; set; } = "MXN";
  public string PayPalLocale { get; set; } = "es_MX";
  public string MerchantProfileKey { get; set; } = "shared-paypal-v1";
  public int QuoteTokenLifetimeMinutes { get; set; } = 10;
  public int ProcessorHeartbeatMaxAgeSeconds { get; set; } = 60;
  public string? PublicBaseUrl { get; set; }
  public string TermsVersion { get; set; } = "online-orders-v1";
  public string PrivacyVersion { get; set; } = "online-orders-v1";
  /// <summary>
  /// Private, keyed credential profiles retained only for refunds of captures
  /// made before the active merchant account changed.
  /// </summary>
  public Dictionary<string, PayPalClientOptions> HistoricalMerchantProfiles { get; set; }
    = new(StringComparer.Ordinal);
  public string AllergenDisclaimer { get; set; }
    = "Las notas están sujetas a disponibilidad y no eliminan el riesgo de contaminación cruzada.";
}

public static class RestaurantCheckoutOptionsPolicy
{
  public static IReadOnlyList<string> Validate(RestaurantCheckoutOptions options, bool production)
  {
    ArgumentNullException.ThrowIfNull(options);
    var errors = new List<string>();
    if (!string.Equals(options.Currency?.Trim(), "MXN", StringComparison.OrdinalIgnoreCase))
      errors.Add("RestaurantCheckout:Currency debe ser MXN.");
    if (options.QuoteTokenLifetimeMinutes is < 5 or > 30)
      errors.Add("RestaurantCheckout:QuoteTokenLifetimeMinutes debe estar entre 5 y 30.");
    if (options.ProcessorHeartbeatMaxAgeSeconds is < 15 or > 300)
      errors.Add("RestaurantCheckout:ProcessorHeartbeatMaxAgeSeconds debe estar entre 15 y 300.");
    if (string.IsNullOrWhiteSpace(options.MerchantProfileKey) || options.MerchantProfileKey.Length > 50)
      errors.Add("RestaurantCheckout:MerchantProfileKey es obligatorio y admite hasta 50 caracteres.");
    if (string.IsNullOrWhiteSpace(options.TermsVersion) || string.IsNullOrWhiteSpace(options.PrivacyVersion))
      errors.Add("RestaurantCheckout requiere versiones vigentes de términos y privacidad.");
    foreach (var (key, profile) in options.HistoricalMerchantProfiles)
    {
      if (string.IsNullOrWhiteSpace(key) || key.Length > 80 || !profile.IsPayPalConfigured)
        errors.Add("Cada perfil PayPal histórico requiere una clave y credenciales privadas completas.");
    }
    if (production)
    {
      if (!options.UseLivePayPal)
        errors.Add("La venta en producción requiere RestaurantCheckout:Environment=Live.");
      if (!options.IsPayPalConfigured)
        errors.Add("La venta en producción requiere credenciales PayPal de RestaurantCheckout.");
      if (!options.IsWebhookVerificationConfigured)
        errors.Add("La venta en producción requiere RestaurantCheckout:PayPalWebhookId.");
      if (!Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out var publicUri)
          || publicUri.Scheme != Uri.UriSchemeHttps)
        errors.Add("RestaurantCheckout:PublicBaseUrl debe ser HTTPS en producción.");
    }
    return errors;
  }
}

public interface IRestaurantPayPalClientResolver
{
  IPayPalOrdersClient Resolve(string merchantProfileKey);
}
