using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.Infrastructure.Features.Reservaciones.ListaReservaciones.Pdf;

public interface IReservacionPdfDocumentFactory
{
  ReservacionPdfDocumentModel CreateFromDetail(ReservacionDetailDto detail);
  ReservacionPdfDocumentModel CreateFromSnapshot(ReservacionPdfSnapshot snapshot);
}
