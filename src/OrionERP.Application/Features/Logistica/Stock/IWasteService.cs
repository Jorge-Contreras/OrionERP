using OrionERP.Application.Features.Logistica.Shared;

namespace OrionERP.Application.Features.Logistica.Stock;

/// <summary>
/// Baja de producto del inventario: caducado, dañado, derramado o no vendido.
/// </summary>
/// <remarks>
/// El documento es <c>logistica.InventoryAdjustment</c> con <c>AdjustmentType='Waste'</c>, el
/// mismo riel que ya usa la pantalla de movimientos; aquí sólo se le agregan el flujo de
/// revisión y la reversa. El alcance es RFC más visibilidad de ubicación, sin dimensión de
/// sede: <c>logistica.Location</c> no la tiene y exigirla dejaría sin merma a las empresas
/// que no operan Hospedaje.
/// </remarks>
public interface IWasteService
{
  /// <summary>Ubicaciones, saldos y lotes disponibles, más los indicadores de la pantalla.</summary>
  Task<WasteWorkspaceDto> GetWorkspaceAsync(string rfc, CancellationToken ct = default);

  Task<WasteHistoryDto> GetHistoryAsync(WasteHistoryQuery query, CancellationToken ct = default);

  /// <summary>
  /// Aplica la baja de inmediato para que las existencias no mientan. Queda
  /// <see cref="WasteStatuses.Approved"/> si la captura un supervisor y
  /// <see cref="WasteStatuses.PendingReview"/> en cualquier otro caso.
  /// </summary>
  Task<LogisticsCommandResult> PostAsync(WasteCreateRequest request, WasteActor actor, CancellationToken ct = default);

  /// <summary>Cierra la revisión de una merma pendiente. Nadie aprueba la suya.</summary>
  Task<LogisticsCommandResult> ApproveAsync(string rfc, long adjustmentId, string userName, CancellationToken ct = default);

  /// <summary>
  /// Corrige una merma con un documento compensatorio que devuelve la cantidad y el valor
  /// exactos. No se borra nada: el kardex es de sólo agregar.
  /// </summary>
  Task<LogisticsCommandResult> ReverseAsync(WasteReversalRequest request, string userName, CancellationToken ct = default);

  /// <summary>Evidencia del documento, para servirla fuera del circuito de Blazor.</summary>
  Task<WasteEvidenceDto?> GetEvidenceAsync(string rfc, long adjustmentId, CancellationToken ct = default);
}
