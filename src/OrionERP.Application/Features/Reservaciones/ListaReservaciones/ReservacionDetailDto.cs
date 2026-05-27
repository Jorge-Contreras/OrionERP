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
  public string? RecommenedBy { get; set; }
  public bool RequiresCfdi { get; set; }
  public decimal TotalPrice { get; set; }
  public decimal Pagado { get; set; }
  public decimal PorPagar { get; set; }
  public decimal TotalSuites { get; set; }
  public decimal SuiteDiscountPercent { get; set; }
  public decimal SuiteDiscountAmount { get; set; }
  public decimal TotalExtras { get; set; }
  public decimal SubTotal { get; set; }
  public decimal Tax { get; set; }
  public decimal Ish { get; set; }
  public int NumNoches { get; set; }
  public string? Notes { get; set; }
  public IReadOnlyList<ReservacionSuiteDto> Suites { get; set; } = Array.Empty<ReservacionSuiteDto>();
  public IReadOnlyList<ReservacionExtraDto> Extras { get; set; } = Array.Empty<ReservacionExtraDto>();
  public IReadOnlyList<ReservacionPagoDto> Pagos { get; set; } = Array.Empty<ReservacionPagoDto>();
  public IReadOnlyList<ReservacionAttachmentDto> Attachments { get; set; } = Array.Empty<ReservacionAttachmentDto>();
  public AirbnbReservationBreakdownDto? AirbnbBreakdown { get; set; }
}
