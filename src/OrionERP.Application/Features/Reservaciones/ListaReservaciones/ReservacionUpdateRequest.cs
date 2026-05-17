using System;

namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public sealed class ReservacionUpdateRequest
{
  public int Id { get; set; }
  public int? ClienteId { get; set; }
  public DateTime? CheckIn { get; set; }
  public DateTime? CheckOut { get; set; }
  public string? Status { get; set; }
  public string? RecommenedBy { get; set; }
  public string? Notes { get; set; }
  public bool Taxable { get; set; }
  public decimal TotalPrice { get; set; }
  public decimal SuiteDiscountPercent { get; set; }
}
