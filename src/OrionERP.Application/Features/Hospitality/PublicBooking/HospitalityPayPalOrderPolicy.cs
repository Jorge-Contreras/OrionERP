namespace OrionERP.Application.Features.Bonhomia.PublicBooking;

/// <summary>
/// Binds a PayPal order to the protected quote that created it. Amount and
/// currency are not sufficient when multiple public sites share a merchant.
/// </summary>
public static class BonhomiaPayPalOrderPolicy
{
  public static string CreateReferenceId(BonhomiaQuoteDto quote)
  {
    ArgumentNullException.ThrowIfNull(quote);
    return quote.QuoteId.ToString("N");
  }

  public static void EnsureOrderBelongsToQuote(
    string? customId,
    string? referenceId,
    BonhomiaQuoteDto quote)
  {
    ArgumentNullException.ThrowIfNull(quote);

    if (string.IsNullOrWhiteSpace(quote.Fingerprint)
        || !string.Equals(customId?.Trim(), quote.Fingerprint, StringComparison.Ordinal)
        || !string.Equals(referenceId?.Trim(), CreateReferenceId(quote), StringComparison.OrdinalIgnoreCase))
    {
      throw new BonhomiaPublicBookingException(
        "paypal_quote_mismatch",
        "La orden PayPal no corresponde a esta cotizacion ni a este website.");
    }
  }

  public static void EnsureCaptureBelongsToQuote(
    BonhomiaPayPalCaptureResult capture,
    BonhomiaQuoteDto quote)
  {
    ArgumentNullException.ThrowIfNull(capture);
    EnsureOrderBelongsToQuote(capture.CustomId, capture.ReferenceId, quote);
  }
}
