using OrionERP.Application.Features.Logistica.Purchasing;

namespace OrionERP.Web.Features.Logistica.Purchasing;

public interface IPurchaseOrderPdfDocumentFactory
{
  Task<PurchaseOrderPdfDocumentModel> CreateFromDetailAsync(PurchaseOrderDetailDto detail, CancellationToken ct = default);
}
