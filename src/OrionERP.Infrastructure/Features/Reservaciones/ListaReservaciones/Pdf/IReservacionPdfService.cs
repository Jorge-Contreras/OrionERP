namespace OrionERP.Infrastructure.Features.Reservaciones.ListaReservaciones.Pdf;

public interface IReservacionPdfService
{
  byte[] Generate(ReservacionPdfDocumentModel model);
}
