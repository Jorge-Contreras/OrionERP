namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public sealed class RoomOptionDto
{
  public int Id { get; set; }
  public string RoomName { get; set; } = string.Empty;
  public string? RoomType { get; set; }
  public decimal BasePrice { get; set; }
}
