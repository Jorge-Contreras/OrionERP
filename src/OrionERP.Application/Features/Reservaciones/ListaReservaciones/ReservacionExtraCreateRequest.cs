namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public sealed class ReservacionExtraCreateRequest
{
  public int ReservationId { get; set; }
  public int ExtraId { get; set; }
  public decimal UnitPrice { get; set; }
  public int Quantity { get; set; } = 1;
  public string? Notes { get; set; }
}
