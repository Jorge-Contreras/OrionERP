namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed class TransaccionReservacionLinkUpsertRequest
{
  public int TransaccionId { get; set; }
  public int ReservationId { get; set; }
  public decimal Amount { get; set; }
}
