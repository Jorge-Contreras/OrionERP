using OrionERP.Application.Features.Payments.Clip;

namespace OrionERP.Application.Features.Restaurante;

public sealed class RestaurantCheckoutOptions : ClipClientOptions
{
  public const string SectionName = "RestaurantCheckout";

  /// <summary>Ruta relativa del endpoint que recibe los avisos de Clip.</summary>
  public const string WebhookPath = "/api/restaurant/checkout/clip-webhook";

  public string Currency { get; set; } = "MXN";

  /// <summary>Idioma del formulario de tarjeta del SDK: <c>es</c> o <c>en</c>.</summary>
  public string ClipLocale { get; set; } = "es";

  public string MerchantProfileKey { get; set; } = "clip-v1";
  public int QuoteTokenLifetimeMinutes { get; set; } = 10;
  public int ProcessorHeartbeatMaxAgeSeconds { get; set; } = 60;
  public string? PublicBaseUrl { get; set; }
  public string TermsVersion { get; set; } = "online-orders-v1";
  public string PrivacyVersion { get; set; } = "online-orders-v1";
  public string GoogleMapsBrowserApiKey { get; set; } = string.Empty;
  public string GoogleMapsServerApiKey { get; set; } = string.Empty;
  public string GoogleMapsMapId { get; set; } = string.Empty;

  /// <summary>
  /// Minutos que un cargo puede quedarse sin resolver antes de que la
  /// recuperación lo declare perdido. Tiene que cubrir con holgura el tiempo de
  /// vida del card token (15 min) para no cerrar un cargo que aún podía aparecer.
  /// </summary>
  public int ChargeReconciliationWindowMinutes { get; set; } = 30;

  /// <summary>
  /// Pagos diferidos. Clip no reembolsa por API los pagos a MSI/MCI, así que el
  /// valor 1 (sin diferir) es el único que conserva la operación de reembolso.
  /// </summary>
  public int Installments { get; set; } = 1;

  public string AllergenDisclaimer { get; set; }
    = "Las notas están sujetas a disponibilidad y no eliminan el riesgo de contaminación cruzada.";

  /// <summary>
  /// Destino absoluto de los avisos de Clip. Se manda en el cuerpo de cada pago,
  /// no se da de alta en ningún panel, así que sólo depende de la URL pública.
  /// </summary>
  public string? WebhookUrl
    => Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var baseUri)
        && baseUri.Scheme == Uri.UriSchemeHttps
      ? new Uri(baseUri, WebhookPath).ToString()
      : null;

  /// <summary>
  /// Clip no requiere dar de alta un webhook ni entrega un identificador que
  /// validar, así que lo único que hace falta para recibir avisos es una URL
  /// pública HTTPS a la cual mandarlos.
  /// </summary>
  public bool IsWebhookConfigured => WebhookUrl is not null;
}

public static class RestaurantCheckoutOptionsPolicy
{
  /// <summary>Tope de Clip por transacción en línea, ya con identidad verificada.</summary>
  public const decimal MaximumOnlineTransactionAmount = 10_000m;

  private static readonly int[] SupportedInstallments = [1, 3, 6, 9, 12, 18, 24];

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
    if (options.ChargeReconciliationWindowMinutes is < 20 or > 240)
      errors.Add("RestaurantCheckout:ChargeReconciliationWindowMinutes debe estar entre 20 y 240.");
    if (string.IsNullOrWhiteSpace(options.MerchantProfileKey) || options.MerchantProfileKey.Length > 50)
      errors.Add("RestaurantCheckout:MerchantProfileKey es obligatorio y admite hasta 50 caracteres.");
    if (!IsSupportedLocale(options.ClipLocale))
      errors.Add("RestaurantCheckout:ClipLocale admite es o en.");
    if (!SupportedInstallments.Contains(options.Installments))
      errors.Add("RestaurantCheckout:Installments admite 1, 3, 6, 9, 12, 18 o 24.");
    if (string.IsNullOrWhiteSpace(options.TermsVersion) || string.IsNullOrWhiteSpace(options.PrivacyVersion))
      errors.Add("RestaurantCheckout requiere versiones vigentes de términos y privacidad.");

    // Una clave de prueba en produccion no cobra y una productiva en sandbox si
    // cobra, asi que el prefijo se valida en cuanto hay clave, sin importar el
    // ambiente del proceso.
    if (!string.IsNullOrWhiteSpace(options.ClipApiKey) && !options.IsApiKeyEnvironmentConsistent)
    {
      errors.Add(options.UseLiveClip
        ? "RestaurantCheckout:ClipApiKey de produccion no debe tener el prefijo test_."
        : "RestaurantCheckout:ClipApiKey de sandbox debe tener el prefijo test_.");
    }

    if (production)
    {
      // La venta en linea se publica apagada. Mientras no se instale el perfil
      // Live de Clip el sitio publico tiene que arrancar igual; el cobro queda
      // cerrado por las compuertas de readiness en tiempo de ejecucion. En
      // cuanto aparece cualquier dato de Clip, el perfil completo es obligatorio.
      if (!string.IsNullOrWhiteSpace(options.ClipApiKey) || !string.IsNullOrWhiteSpace(options.ClipApiSecret))
      {
        if (!options.UseLiveClip)
          errors.Add("La venta en producción requiere RestaurantCheckout:Environment=Live.");
        if (!options.IsClipConfigured)
          errors.Add("La venta en producción requiere credenciales Clip de RestaurantCheckout.");
      }
      if (!Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out var publicUri)
          || publicUri.Scheme != Uri.UriSchemeHttps)
        errors.Add("RestaurantCheckout:PublicBaseUrl debe ser HTTPS en producción.");
    }
    return errors;
  }

  private static bool IsSupportedLocale(string? value)
  {
    var normalized = value?.Trim();
    return string.Equals(normalized, "es", StringComparison.OrdinalIgnoreCase)
      || string.Equals(normalized, "en", StringComparison.OrdinalIgnoreCase);
  }

}
