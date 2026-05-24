using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using OrionERP.Application.Features.Bonhomia.PublicBooking;

namespace OrionERP.Web.Features.Bonhomia.Checkout;

public sealed class BonhomiaQuoteTokenService : IBonhomiaQuoteTokenService
{
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
  private readonly IDataProtector _protector;

  public BonhomiaQuoteTokenService(IDataProtectionProvider dataProtectionProvider)
  {
    _protector = dataProtectionProvider.CreateProtector("Bonhomia.PublicQuote.v1");
  }

  public string CreateToken(BonhomiaQuoteDto quote)
  {
    ArgumentNullException.ThrowIfNull(quote);
    var json = JsonSerializer.Serialize(quote, JsonOptions);
    return _protector.Protect(json);
  }

  public bool TryValidate(string? token, out BonhomiaQuoteDto? quote, out string errorMessage)
  {
    quote = null;
    errorMessage = string.Empty;

    if (string.IsNullOrWhiteSpace(token))
    {
      errorMessage = "La cotizacion expiro. Vuelve a generar el resumen.";
      return false;
    }

    try
    {
      var json = _protector.Unprotect(token);
      quote = JsonSerializer.Deserialize<BonhomiaQuoteDto>(json, JsonOptions);
      if (quote is null)
      {
        errorMessage = "La cotizacion no se pudo leer.";
        return false;
      }

      if (quote.ExpiresAtUtc <= DateTimeOffset.UtcNow)
      {
        quote = null;
        errorMessage = "La cotizacion expiro. Vuelve a generar el resumen.";
        return false;
      }

      var expectedFingerprint = BonhomiaQuoteCalculator.CreateFingerprint(quote);
      if (!string.Equals(expectedFingerprint, quote.Fingerprint, StringComparison.Ordinal))
      {
        quote = null;
        errorMessage = "La cotizacion cambio. Vuelve a generar el resumen.";
        return false;
      }

      return true;
    }
    catch
    {
      quote = null;
      errorMessage = "La cotizacion no es valida. Vuelve a generar el resumen.";
      return false;
    }
  }
}
