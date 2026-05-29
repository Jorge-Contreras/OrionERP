using System;
using System.Globalization;
using System.Linq;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.Infrastructure.Features.Reservaciones.ListaReservaciones.Pdf;

public sealed class ReservacionPdfDocumentFactory : IReservacionPdfDocumentFactory
{
  public ReservacionPdfDocumentModel CreateFromDetail(ReservacionDetailDto detail)
  {
    ArgumentNullException.ThrowIfNull(detail);

    return CreateFromSnapshot(new ReservacionPdfSnapshot(
      detail.Id,
      detail.Cliente,
      detail.Status ?? string.Empty,
      detail.CheckIn,
      detail.CheckOut,
      detail.RecommenedBy,
      detail.RequiresCfdi,
      detail.Notes,
      detail.TotalSuites,
      detail.SuiteDiscountPercent,
      detail.SuiteDiscountAmount,
      detail.TotalExtras,
      detail.SubTotal,
      detail.Tax,
      detail.Ish,
      detail.TotalPrice,
      detail.Pagado,
      detail.PorPagar,
      detail.Suites,
      detail.Extras,
      detail.Pagos,
      detail.Attachments));
  }

  public ReservacionPdfDocumentModel CreateFromSnapshot(ReservacionPdfSnapshot snapshot)
  {
    ArgumentNullException.ThrowIfNull(snapshot);

    var culture = CultureInfo.CurrentCulture;
    var numNoches = 0;
    if (snapshot.CheckIn.HasValue && snapshot.CheckOut.HasValue && snapshot.CheckOut.Value.Date >= snapshot.CheckIn.Value.Date)
    {
      numNoches = (int)(snapshot.CheckOut.Value.Date - snapshot.CheckIn.Value.Date).TotalDays;
    }

    return new ReservacionPdfDocumentModel(
      snapshot.ReservationId,
      snapshot.Cliente,
      snapshot.Status,
      FormatDate(snapshot.CheckIn, culture),
      FormatDate(snapshot.CheckOut, culture),
      numNoches.ToString(culture),
      snapshot.Recomendacion ?? string.Empty,
      snapshot.Facturable ? "Si" : "No",
      snapshot.Notes ?? string.Empty,
      DateTime.Now.ToString("f", culture),
      FormatCurrency(snapshot.TotalSuites, culture),
      snapshot.SuiteDiscountPercent > 1m ? snapshot.SuiteDiscountPercent.ToString("0.##", culture) : string.Empty,
      snapshot.SuiteDiscountAmount > 0m ? FormatCurrency(-snapshot.SuiteDiscountAmount, culture) : string.Empty,
      FormatCurrency(snapshot.TotalExtras, culture),
      FormatCurrency(snapshot.SubTotal, culture),
      FormatCurrency(snapshot.Tax, culture),
      FormatCurrency(snapshot.Ish, culture),
      FormatCurrency(snapshot.TotalReservacion, culture),
      FormatCurrency(snapshot.TotalPagado, culture),
      FormatCurrency(snapshot.PorPagar, culture),
      snapshot.Suites.Select(suite => new ReservacionPdfSuiteRow(
          suite.Fecha.ToShortDateString(),
          suite.Suite,
          FormatCurrency(suite.Precio, culture),
          suite.LimpiezaProfunda ? "Si" : "No"))
        .ToList(),
      snapshot.Extras.Select(extra => new ReservacionPdfExtraRow(
          extra.Name,
          extra.Description ?? string.Empty,
          extra.Quantity.ToString(culture),
          FormatCurrency(extra.UnitPrice, culture),
          FormatCurrency(extra.Price, culture),
          extra.Notes ?? string.Empty))
        .ToList(),
      snapshot.Pagos.Select(pago => new ReservacionPdfPagoRow(
          pago.TransaccionId.ToString(culture),
          pago.Fecha.ToShortDateString(),
          FormatCurrency(pago.Monto, culture),
          pago.Concepto))
        .ToList(),
      snapshot.Attachments.Select(attachment => new ReservacionPdfAttachmentRow(
          attachment.AttachmentName,
          attachment.AttachmentExtension,
          attachment.AttachmentDescription ?? string.Empty,
          attachment.Length.ToString("N0", culture)))
        .ToList());
  }

  private static string FormatDate(DateTime? value, CultureInfo culture)
    => value.HasValue ? value.Value.ToString("d", culture) : string.Empty;

  private static string FormatCurrency(decimal value, CultureInfo culture)
    => value.ToString("C", culture);
}
