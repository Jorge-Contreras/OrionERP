namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public sealed class RoomCalendarResourceDto
{
  public int RoomId { get; set; }
  public string RoomCode { get; set; } = string.Empty;
  public string RoomName { get; set; } = string.Empty;
  public string RoomType { get; set; } = string.Empty;
  public decimal BasePrice { get; set; }
  public int DisplayOrder { get; set; }
  public bool CalendarEnabled { get; set; }
}
