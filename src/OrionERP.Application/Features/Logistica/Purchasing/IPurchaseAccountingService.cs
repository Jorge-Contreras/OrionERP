using OrionERP.Application.Features.Logistica.Shared;

namespace OrionERP.Application.Features.Logistica.Purchasing;

/// <summary>
/// Liga compras recibidas con pólizas, en ambos sentidos: generar o ligar una póliza desde la
/// compra, y ligar compras desde la póliza.
/// </summary>
public interface IPurchaseAccountingService
{
  /// <summary><c>null</c> si la compra no existe o no pertenece a la empresa y sede autorizadas.</summary>
  Task<PurchaseAccountingWorkspaceDto?> GetPurchaseOrderWorkspaceAsync(int purchaseOrderId, CancellationToken ct = default);

  /// <summary>
  /// Pólizas con saldo que podrían cubrir la compra, primero las de monto y fecha más parecidos.
  /// Un número busca por folio de póliza; un texto, por concepto o memo.
  /// </summary>
  Task<IReadOnlyList<PurchaseAccountingCandidateDto>> SearchPolicyCandidatesAsync(
    int purchaseOrderId, string? search, CancellationToken ct = default);

  Task<IReadOnlyList<PurchaseOrderPostingStatusDto>> GetPostingStatusesAsync(
    IReadOnlyCollection<int> purchaseOrderIds, CancellationToken ct = default);

  /// <summary>
  /// Crea una póliza EGRESO balanceada por lo que falta contabilizar, con las cuentas
  /// SUBTOTAL_GASTO, IVA_ACREDITABLE y TOTAL_GASTO de Ajustes, y la liga a la compra.
  /// Un intento que falla a medias se retoma sobre la misma póliza.
  /// </summary>
  Task<LogisticsCommandResult> GeneratePolicyAsync(
    PurchaseAccountingGenerateRequest request, string? userName, CancellationToken ct = default);

  Task<LogisticsCommandResult> LinkAsync(PurchaseAccountingLinkRequest request, string? userName, CancellationToken ct = default);

  Task<LogisticsCommandResult> UnlinkAsync(int linkId, CancellationToken ct = default);

  /// <summary><c>null</c> si la póliza no pertenece a la empresa autorizada.</summary>
  Task<PurchasePolizaLinksDto?> GetTransaccionLinksAsync(int transaccionId, CancellationToken ct = default);

  /// <summary>Compras recibidas con saldo por contabilizar, primero las de monto y fecha más parecidos a la póliza.</summary>
  Task<IReadOnlyList<PurchaseOrderAccountingCandidateDto>> SearchPurchaseOrderCandidatesAsync(
    int transaccionId, string? search, CancellationToken ct = default);
}
