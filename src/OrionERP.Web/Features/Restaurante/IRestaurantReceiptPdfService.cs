namespace OrionERP.Web.Features.Restaurante;

public interface IRestaurantReceiptPdfService
{
  byte[] Generate(RestaurantReceiptPdfDocumentModel model);
}
