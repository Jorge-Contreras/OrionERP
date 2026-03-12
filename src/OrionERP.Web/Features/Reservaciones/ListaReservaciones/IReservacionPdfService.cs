namespace OrionERP.Web.Features.Reservaciones.ListaReservaciones;

public interface IReservacionPdfService
{
  byte[] Generate(ReservacionPdfDocumentModel model);
}
