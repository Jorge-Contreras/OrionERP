using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Bonhomia.PublicBooking;

namespace OrionERP.Bonhomia.Web.Features.Bonhomia.Checkout;

public sealed class BonhomiaReservationPdfTokenService : IBonhomiaReservationPdfTokenService
{
  private readonly ITimeLimitedDataProtector _protector;
  private readonly BonhomiaCheckoutOptions _options;

  public BonhomiaReservationPdfTokenService(
    IDataProtectionProvider dataProtectionProvider,
    IOptions<BonhomiaCheckoutOptions> options)
  {
    _protector = dataProtectionProvider
      .CreateProtector("OrionERP.Bonhomia.Reservaciones.Pdf")
      .ToTimeLimitedDataProtector();
    _options = options.Value;
  }

  public string CreateToken(int reservationId)
  {
    var lifetime = TimeSpan.FromMinutes(Math.Max(_options.PdfTokenLifetimeMinutes, 1));
    var protectedValue = _protector.Protect(reservationId.ToString(CultureInfo.InvariantCulture), lifetime);
    return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(protectedValue));
  }

  public bool TryValidate(int reservationId, string? token, out string? errorMessage)
  {
    errorMessage = "Token invalido o expirado.";
    if (string.IsNullOrWhiteSpace(token))
    {
      return false;
    }

    try
    {
      var protectedValue = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token));
      var payload = _protector.Unprotect(protectedValue);
      if (!string.Equals(payload, reservationId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
      {
        return false;
      }

      errorMessage = null;
      return true;
    }
    catch
    {
      return false;
    }
  }
}
