namespace OrionERP.Web.Features.Bonhomia.Checkout;

public interface IBonhomiaReservationPdfTokenService
{
  string CreateToken(int reservationId);
  bool TryValidate(int reservationId, string? token, out string? errorMessage);
}
