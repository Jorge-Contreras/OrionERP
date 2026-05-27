namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public sealed class ReservacionExtraDto
{
  public int Id { get; set; }
  public int ExtraId { get; set; }
  public string Name { get; set; } = string.Empty;
  public string? Description { get; set; }
  public decimal UnitPrice { get; set; }
  public int Quantity { get; set; }
  public decimal Price { get; set; }
  public string? Notes { get; set; }
}
