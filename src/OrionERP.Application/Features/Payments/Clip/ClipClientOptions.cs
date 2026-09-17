namespace OrionERP.Application.Features.Payments.Clip;

/// <summary>
/// Credenciales de la API de Clip. A diferencia de PayPal, Clip no expone un
/// host distinto para pruebas: la misma URL base atiende ambos ambientes y
/// distingue el sandbox por el prefijo <c>test_</c> de la clave. Los sitios
/// públicos derivan su propio tipo de opciones para que cada proceso mantenga
/// una sección de configuración independiente.
/// </summary>
public class ClipClientOptions
{
  /// <summary>Prefijo obligatorio de las claves de prueba de Clip.</summary>
  public const string TestApiKeyPrefix = "test_";

  public string Environment { get; set; } = "Sandbox";

  /// <summary>
  /// Clave API. La usa el SDK en el navegador para tokenizar y el backend para
  /// cobrar, así que no es secreta por sí misma; el secreto es la clave secreta.
  /// </summary>
  public string ClipApiKey { get; set; } = string.Empty;

  /// <summary>
  /// Clave secreta. Clip la muestra una única vez al crear la credencial y sólo
  /// la API de reembolsos la necesita, porque esa exige
  /// <c>Basic base64(clave:secreto)</c> mientras que la de pagos acepta
  /// <c>Bearer</c>. Sin ella se puede cobrar pero no devolver.
  /// </summary>
  public string ClipApiSecret { get; set; } = string.Empty;

  public bool IsClipConfigured => !string.IsNullOrWhiteSpace(ClipApiKey);

  /// <summary>Cobrar sólo necesita la clave; reembolsar necesita además el secreto.</summary>
  public bool AreRefundsConfigured
    => IsClipConfigured && !string.IsNullOrWhiteSpace(ClipApiSecret);

  public bool UseLiveClip
    => string.Equals(Environment, "Live", StringComparison.OrdinalIgnoreCase)
      || string.Equals(Environment, "Production", StringComparison.OrdinalIgnoreCase);

  /// <summary>
  /// Una clave de prueba en producción cobraría nada y una clave productiva en
  /// sandbox cobraría de verdad, así que el prefijo tiene que concordar con el
  /// ambiente declarado.
  /// </summary>
  public bool IsApiKeyEnvironmentConsistent
  {
    get
    {
      var key = ClipApiKey?.Trim();
      if (string.IsNullOrEmpty(key)) return false;
      var isTestKey = key.StartsWith(TestApiKeyPrefix, StringComparison.Ordinal);
      return UseLiveClip ? !isTestKey : isTestKey;
    }
  }

  public Uri ClipBaseUri => new("https://api.payclip.com");
}
