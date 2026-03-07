namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public sealed record class ReservacionCommandResult(bool Success, string Message)
{
  public static ReservacionCommandResult Ok(string message)
    => new(true, message);

  public static ReservacionCommandResult Fail(string message)
    => new(false, message);
}
