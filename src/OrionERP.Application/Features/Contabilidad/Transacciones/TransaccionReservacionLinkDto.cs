using System;

namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed class TransaccionReservacionLinkDto
{
  public int ReservationId { get; set; }
  public int TransaccionId { get; set; }
  public decimal Amount { get; set; }
  public string Cliente { get; set; } = string.Empty;
  public DateTime? CheckIn { get; set; }
  public DateTime? CheckOut { get; set; }
  public string? Status { get; set; }
  public decimal TotalPrice { get; set; }
  public decimal Pagado { get; set; }
  public decimal PorPagar { get; set; }
  public string? Notes { get; set; }
}
