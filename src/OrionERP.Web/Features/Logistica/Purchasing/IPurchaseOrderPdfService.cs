namespace OrionERP.Web.Features.Logistica.Purchasing;

public interface IPurchaseOrderPdfService
{
  byte[] Generate(PurchaseOrderPdfDocumentModel model);
}
