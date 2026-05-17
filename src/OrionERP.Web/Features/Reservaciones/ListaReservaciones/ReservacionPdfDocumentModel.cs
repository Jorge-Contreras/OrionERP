using System.Collections.Generic;

namespace OrionERP.Web.Features.Reservaciones.ListaReservaciones;

public sealed record ReservacionPdfDocumentModel(
  int ReservationId,
  string Cliente,
  string Status,
  string CheckIn,
  string CheckOut,
  string NumNoches,
  string Recomendacion,
  string Facturable,
  string Notes,
  string GeneratedAt,
  string TotalSuites,
  string SuiteDiscountPercent,
  string SuiteDiscountAmount,
  string TotalExtras,
  string SubTotal,
  string Tax,
  string Ish,
  string TotalReservacion,
  string TotalPagado,
  string PorPagar,
  IReadOnlyList<ReservacionPdfSuiteRow> Suites,
  IReadOnlyList<ReservacionPdfExtraRow> Extras,
  IReadOnlyList<ReservacionPdfPagoRow> Pagos,
  IReadOnlyList<ReservacionPdfAttachmentRow> Archivos);

public sealed record ReservacionPdfSuiteRow(
  string Fecha,
  string Suite,
  string Precio,
  string Limpieza);

public sealed record ReservacionPdfExtraRow(
  string Suite,
  string Descripcion,
  string Precio,
  string Descuento,
  string Total,
  string Notas);

public sealed record ReservacionPdfPagoRow(
  string Id,
  string Fecha,
  string Monto,
  string Concepto);

public sealed record ReservacionPdfAttachmentRow(
  string Nombre,
  string Extension,
  string Descripcion,
  string Tamano);
