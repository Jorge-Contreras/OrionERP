namespace OrionERP.Web.Features.Reservaciones.OpenClaw;

public interface IOpenClawReservationPdfTokenService
{
  string CreateToken(int reservationId);
  bool TryValidate(int reservationId, string? token, out string? errorMessage);
}
