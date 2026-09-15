using OrionERP.Application.Features.Payments.PayPal;

namespace OrionERP.Application.Features.Restaurante;

/// <summary>
/// Builds provider metadata from the verified public-site identity so the
/// shared restaurant payment code never depends on a tenant or brand literal.
/// </summary>
public static class RestaurantPayPalMetadataPolicy
{
  public static string ReferenceId(string publicSiteKey, Guid checkoutAttemptId)
    => $"online:{SiteToken(publicSiteKey, upperCase: false)}:{RequireId(checkoutAttemptId):N}";

  public static string InvoiceId(string publicSiteKey, Guid checkoutAttemptId)
    => $"{SiteToken(publicSiteKey, upperCase: true)}-{RequireId(checkoutAttemptId):N}";

  public static string RefundInvoiceId(string publicSiteKey, Guid refundId)
    => $"{SiteToken(publicSiteKey, upperCase: true)}-R-{RequireId(refundId):N}";

  public static string CustomId(string publicSiteKey, string fingerprint)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
    var normalizedFingerprint = fingerprint.Trim();
    if (normalizedFingerprint.Length != 64
        || normalizedFingerprint.Any(character => !char.IsAsciiHexDigit(character)))
      throw new ArgumentException("The quote fingerprint must be a SHA-256 hex value.", nameof(fingerprint));
    return $"{SiteToken(publicSiteKey, upperCase: false)}:{normalizedFingerprint.ToUpperInvariant()}";
  }

  public static string RequestId(string publicSiteKey, string operation, Guid checkoutAttemptId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(operation);
    return PayPalRequestId.From(
      $"restaurant-{SiteToken(publicSiteKey, upperCase: false)}-{operation.Trim().ToLowerInvariant()}-{RequireId(checkoutAttemptId):N}");
  }

  private static string SiteToken(string publicSiteKey, bool upperCase)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(publicSiteKey);
    var token = publicSiteKey.Trim();
    if (token.Length is 0 or > 48
        || token.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
      throw new ArgumentException("The public-site key is not provider-safe.", nameof(publicSiteKey));
    return upperCase ? token.ToUpperInvariant() : token.ToLowerInvariant();
  }

  private static Guid RequireId(Guid value)
    => value == Guid.Empty
      ? throw new ArgumentException("A stable local identifier is required.")
      : value;
}
