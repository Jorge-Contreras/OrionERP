namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public sealed class ReservacionExtraDto
{
  public int Id { get; set; }
  public int RoomId { get; set; }
  public string RoomName { get; set; } = string.Empty;
  public string RoomDescription { get; set; } = string.Empty;
  public decimal Price { get; set; }
  public string? Notes { get; set; }
  public decimal Discount { get; set; }
  public decimal DiscountedPrice { get; set; }
}
