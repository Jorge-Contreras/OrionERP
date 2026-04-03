using System;

namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public sealed class ListaReservacionItemDto
{
  public int Id { get; set; }
  public string Cliente { get; set; } = string.Empty;
  public DateTime? CheckIn { get; set; }
  public DateTime? CheckOut { get; set; }
  public string? Status { get; set; }
  public decimal TotalPrice { get; set; }
  public decimal Pagado { get; set; }
  public decimal PorPagar { get; set; }
  public string? Notes { get; set; }
}
