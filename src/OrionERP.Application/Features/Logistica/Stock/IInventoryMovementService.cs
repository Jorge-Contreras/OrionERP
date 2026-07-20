using OrionERP.Application.Features.Logistica.Shared;

namespace OrionERP.Application.Features.Logistica.Stock;

public interface IInventoryMovementService
{
  Task<InventoryMovementWorkspaceDto> GetWorkspaceAsync(string rfc, CancellationToken ct = default);
  Task<LogisticsCommandResult> PostTransferAsync(InventoryTransferCreateRequest request, string userName, CancellationToken ct = default);
  Task<LogisticsCommandResult> PostAdjustmentAsync(InventoryAdjustmentCreateRequest request, string userName, CancellationToken ct = default);
}
