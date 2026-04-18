using OrionERP.Application.Features.Logistica.Shared;

namespace OrionERP.Application.Features.Logistica.Purchasing;

public interface IPurchaseOrderService
{
  Task<IReadOnlyList<PurchaseOrderListItemDto>> GetPurchaseOrdersAsync(PurchaseOrderFilter filter, CancellationToken ct = default);
  Task<PurchaseOrderDetailDto?> GetPurchaseOrderAsync(int purchaseOrderId, CancellationToken ct = default);
  Task<PurchaseOrderCatalogDto> GetCatalogAsync(CancellationToken ct = default);
  Task<LogisticsCommandResult> SaveDraftAsync(PurchaseOrderUpsertRequest request, string? savedBy, CancellationToken ct = default);
  Task<LogisticsCommandResult> IssueAsync(int purchaseOrderId, string? issuedBy, CancellationToken ct = default);
  Task<LogisticsCommandResult> ReceiveAsync(PurchaseReceiptCreateRequest request, string? receivedBy, CancellationToken ct = default);
  Task<LogisticsCommandResult> CompleteAsync(int purchaseOrderId, string? completedBy, CancellationToken ct = default);
  Task<LogisticsCommandResult> CancelAsync(int purchaseOrderId, string? cancelledBy, CancellationToken ct = default);
}
