using System;
using System.Collections.Generic;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.Web.Features.Reservaciones.ListaReservaciones;

public sealed record ReservacionPdfSnapshot(
  int ReservationId,
  string Cliente,
  string Status,
  DateTime? CheckIn,
  DateTime? CheckOut,
  string? Recomendacion,
  bool Facturable,
  string? Notes,
  decimal TotalSuites,
  decimal TotalExtras,
  decimal SubTotal,
  decimal Tax,
  decimal Ish,
  decimal TotalReservacion,
  decimal TotalPagado,
  decimal PorPagar,
  IReadOnlyList<ReservacionSuiteDto> Suites,
  IReadOnlyList<ReservacionExtraDto> Extras,
  IReadOnlyList<ReservacionPagoDto> Pagos,
  IReadOnlyList<ReservacionAttachmentDto> Attachments);
