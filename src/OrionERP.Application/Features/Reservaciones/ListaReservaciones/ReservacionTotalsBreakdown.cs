namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public sealed record ReservacionTotalsBreakdown(
  decimal TotalSuites,
  decimal SuiteDiscountPercent,
  decimal SuiteDiscountAmount,
  decimal TotalExtras,
  decimal SubTotal,
  decimal Tax,
  decimal Ish,
  decimal TotalReservacion,
  decimal TotalPagado,
  decimal PorPagar,
  int NumNoches);
