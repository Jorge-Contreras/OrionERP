namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public sealed class ReservacionExtraUpdateRequest
{
  public int Id { get; set; }
  public int ReservationId { get; set; }
  public int? RoomId { get; set; }
  public decimal Price { get; set; }
  public string? Notes { get; set; }
}
