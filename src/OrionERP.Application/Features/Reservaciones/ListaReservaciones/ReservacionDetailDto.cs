using System;
using System.Collections.Generic;

namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public sealed class ReservacionDetailDto
{
  public int Id { get; set; }
  public int? ClienteId { get; set; }
  public string Cliente { get; set; } = string.Empty;
  public DateTime? CheckIn { get; set; }
  public DateTime? CheckOut { get; set; }
  public string? Status { get; set; }
  public decimal TotalPrice { get; set; }
  public decimal Pagado { get; set; }
  public decimal PorPagar { get; set; }
  public string? Notes { get; set; }
  public IReadOnlyList<ReservacionPagoDto> Pagos { get; set; } = Array.Empty<ReservacionPagoDto>();
}
