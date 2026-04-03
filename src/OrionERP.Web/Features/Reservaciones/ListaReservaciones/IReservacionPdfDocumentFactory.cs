using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.Web.Features.Reservaciones.ListaReservaciones;

public interface IReservacionPdfDocumentFactory
{
  ReservacionPdfDocumentModel CreateFromDetail(ReservacionDetailDto detail);
  ReservacionPdfDocumentModel CreateFromSnapshot(ReservacionPdfSnapshot snapshot);
}
