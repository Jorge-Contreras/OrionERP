using OrionERP.Application.Features.Bonhomia.PublicBooking;

namespace OrionERP.Web.Features.Bonhomia.Checkout;

public interface IBonhomiaQuoteTokenService
{
  string CreateToken(BonhomiaQuoteDto quote);
  bool TryValidate(string? token, out BonhomiaQuoteDto? quote, out string errorMessage);
}
