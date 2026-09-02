namespace OrionERP.Web.Features.Restaurante;

public interface IRestaurantReceiptPdfService
{
  byte[] Generate(RestaurantReceiptPdfDocumentModel model);

  /// <summary>
  /// Genera el comprobante de 80 mm con los datos bancarios que el cliente usa
  /// para pagar con transferencia electrónica de fondos (SPEI).
  /// </summary>
  byte[] GenerateTransferSlip(RestaurantTransferSlipDocumentModel model);
}
