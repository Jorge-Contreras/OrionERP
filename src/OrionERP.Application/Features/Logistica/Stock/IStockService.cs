using System.Threading;
using System.Threading.Tasks;
using OrionERP.Application.Features.Logistica.Shared;

namespace OrionERP.Application.Features.Logistica.Stock;

public interface IStockService
{
  Task<IReadOnlyList<StockListItemDto>> GetStockAsync(StockFilter filter, CancellationToken ct = default);
  Task<LogisticsCommandResult> AddMaterialToLocationAsync(LocationMaterialAddRequest request, CancellationToken ct = default);
  Task<LogisticsCommandResult> SaveStockThresholdsAsync(StockThresholdUpdateRequest request, CancellationToken ct = default);
  Task<IReadOnlyList<StockTransactionDto>> GetStockTransactionsAsync(int stockBalanceId, CancellationToken ct = default);
  Task<IReadOnlyList<LocationMaterialAttachmentDto>> GetLocationMaterialAttachmentsAsync(int locationId, int materialId, bool includeDeleted = false, CancellationToken ct = default);
  Task<LogisticsBinaryContent?> GetLocationMaterialAttachmentContentAsync(int attachmentId, CancellationToken ct = default);
  Task<LogisticsCommandResult> SaveLocationMaterialAttachmentAsync(LocationMaterialAttachmentCreateRequest request, CancellationToken ct = default);
  Task<LogisticsCommandResult> RemoveLocationMaterialAsync(int stockBalanceId, string? removedBy, CancellationToken ct = default);
  Task<LogisticsCommandResult> ReactivateLocationMaterialAsync(int stockBalanceId, string? reactivatedBy, CancellationToken ct = default);
}
