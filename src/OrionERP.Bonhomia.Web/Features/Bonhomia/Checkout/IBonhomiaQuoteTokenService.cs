using OrionERP.Application.Features.Hospitality.PublicBooking;

namespace OrionERP.Bonhomia.Web.Features.Bonhomia.Checkout;

public interface IHospitalityQuoteTokenService
{
  string CreateToken(HospitalityQuoteDto quote);
  bool TryValidate(string? token, out HospitalityQuoteDto? quote, out string errorMessage);
}
