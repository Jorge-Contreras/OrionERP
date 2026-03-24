using System.Threading;
using System.Threading.Tasks;
using OrionERP.Application.Features.Logistica.Shared;

namespace OrionERP.Application.Features.Logistica.Stock;

public interface IStockService
{
  Task<IReadOnlyList<StockListItemDto>> GetStockAsync(StockFilter filter, CancellationToken ct = default);
  Task<IReadOnlyList<StockTransactionDto>> GetStockTransactionsAsync(int stockBalanceId, CancellationToken ct = default);
  Task<IReadOnlyList<LocationMaterialAttachmentDto>> GetLocationMaterialAttachmentsAsync(int locationId, int materialId, CancellationToken ct = default);
  Task<LogisticsBinaryContent?> GetLocationMaterialAttachmentContentAsync(int attachmentId, CancellationToken ct = default);
  Task<LogisticsCommandResult> SaveLocationMaterialAttachmentAsync(LocationMaterialAttachmentCreateRequest request, CancellationToken ct = default);
}
