using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public interface ITransaccionService
{
  Task<TransaccionHeaderDto?> GetHeaderAsync(int transaccionId, CancellationToken ct = default);
  Task<IReadOnlyList<TransaccionMovimientoDto>> GetMovimientosAsync(int transaccionId, CancellationToken ct = default);
  Task<MovimientoTotalsDto> GetMovimientoTotalsAsync(int transaccionId, CancellationToken ct = default);
  Task<IReadOnlyList<TransaccionAttachmentDto>> GetAttachmentsAsync(int transaccionId, CancellationToken ct = default);
  Task<TransaccionAttachmentContent?> GetAttachmentContentAsync(int attachmentId, CancellationToken ct = default);
  Task<TransaccionAttachmentDto> AddAttachmentAsync(TransaccionAttachmentCreateRequest request, CancellationToken ct = default);
  Task DeleteAttachmentAsync(int attachmentId, CancellationToken ct = default);
  Task<int> GetComprobanteIdByXmlAttachmentAsync(int attachmentId, CancellationToken ct = default);
  Task<bool> IsComprobanteLinkedToTransaccionAsync(int transaccionId, int comprobanteId, CancellationToken ct = default);
  Task SetAttachmentTransaccionAsync(int attachmentId, int? transaccionId, CancellationToken ct = default);
  Task<TransaccionCommandResult> LinkCfdiAndRelinkAttachmentAsync(int transaccionId, int comprobanteId, decimal monto, CancellationToken ct = default);
  Task<TransaccionCommandResult> InsertTransaccionComprobanteAsync(int transaccionId, int comprobanteId, decimal monto, CancellationToken ct = default);
  Task<TransaccionCommandResult> UpdateComprobanteMontoAsync(int transaccionId, int comprobanteId, decimal monto, CancellationToken ct = default);
  Task<IReadOnlyList<TransaccionComprobanteDto>> GetComprobantesAsync(int transaccionId, CancellationToken ct = default);
  Task<IReadOnlyList<TransaccionReservacionLinkDto>> GetReservacionLinksAsync(int transaccionId, CancellationToken ct = default);
  Task<IReadOnlyList<TransaccionReservacionSearchItemDto>> SearchReservacionesAsync(string? search, CancellationToken ct = default);
  Task<TransaccionCommandResult> UpsertReservacionLinkAsync(TransaccionReservacionLinkUpsertRequest request, CancellationToken ct = default);
  Task<TransaccionCommandResult> DeleteReservacionLinkAsync(int transaccionId, int reservationId, CancellationToken ct = default);
  Task ToggleComprobanteAsync(int transaccionId, int comprobanteId, bool vincular, CancellationToken ct = default);
  Task<TransaccionCommandResult> UnlinkComprobanteAsync(TransaccionComprobanteUnlinkRequest request, CancellationToken ct = default);
  Task<TransaccionGuardarCerrarResult> GuardarYCerrarAsync(TransaccionGuardarCerrarRequest request, CancellationToken ct = default);
  Task<IReadOnlyList<TransaccionListItem>> GetCandidatesAsync(
      DateTime fechaXml,
      decimal montoAbs,
      string rfc,
      int daysBack = 60,
      int top = 200,
      CancellationToken ct = default);
  Task<IReadOnlyList<TransaccionCfdiCandidateDto>> GetCfdiCandidatesAsync(TransaccionCfdiSearchRequest request, CancellationToken ct = default);
  Task<IReadOnlyList<long>> GetLinkedCfdiIdsAsync(int transaccionId, CancellationToken ct = default);
  Task<TransaccionCfdiLinkedDataDto> GetLinkedCfdiSummaryAsync(int transaccionId, CancellationToken ct = default);
  Task<CfdiPolizaLinkingWorkspaceDto> GetCfdiPolizaLinkingWorkspaceAsync(int comprobanteId, string? rfc, TransaccionFilter filter, CancellationToken ct = default);
  Task<Pago20PolizaLinkingWorkspaceDto> GetPago20PolizaLinkingWorkspaceAsync(int doctoRelacionadoId, string? rfc, TransaccionFilter filter, CancellationToken ct = default);
  Task<TransaccionCommandResult> LinkCfdiAsync(TransaccionCfdiLinkRequest request, CancellationToken ct = default);
  Task<TransaccionCommandResult> ApplyCategoriaPlantillaAsync(
      int transaccionId,
      int categoriaId,
      CancellationToken ct = default);
  Task<TransaccionCommandResult> TimbrarCfdiPublicoAsync(
      TransaccionTimbrarPublicoRequest request,
      CancellationToken ct = default);
  Task<TransaccionCommandResult> ProcessSatXmlAsync(
      int attachmentId,
      int transaccionId,
      CancellationToken ct = default);
  Task<TransaccionCommandResult> RegenerarPolizaDesdeComprobanteEnTransaccionAsync(
      int transaccionId,
      long comprobanteId,
      CancellationToken ct = default);
  Task<TransaccionCommandResult> RegenerarPolizaDesdeComplementoEnTransaccionAsync(
      int transaccionId,
      long comprobanteId,
      CancellationToken ct = default);
  Task<IReadOnlyList<LookupInt32Dto>> GetCategoriasAsync(string rfc, CancellationToken ct = default);
  Task<IReadOnlyList<LookupInt32Dto>> GetActividadesAsync(string rfc, CancellationToken ct = default);
  Task<IReadOnlyList<LookupInt32Dto>> SearchActividadesAsync(string rfc, string? search, int top = 25, CancellationToken ct = default);
  Task<LookupInt32Dto?> GetActividadByIdAsync(string rfc, int actividadId, CancellationToken ct = default);
  Task<IReadOnlyList<LookupInt32Dto>> GetComprasAsync(string rfc, CancellationToken ct = default);
  Task<IReadOnlyList<LookupInt32Dto>> GetServiciosAsync(string rfc, CancellationToken ct = default);
  Task<IReadOnlyList<LookupInt32Dto>> GetNominasAsync(string rfc, CancellationToken ct = default);
  Task<IReadOnlyList<FormaPagoLookupDto>> GetFormasPagoAsync(CancellationToken ct = default);
  Task DeleteMovimientoAsync(int transaccionId, int movimientoId, CancellationToken ct = default);
  Task<TransaccionCommandResult> DeleteTransaccionAsync(int transaccionId, CancellationToken ct = default);
  Task<TransaccionCreateResult> CreateTransaccionAsync(TransaccionCreateRequest request, CancellationToken ct = default);
  Task<IReadOnlyList<TransaccionListItemDto>> GetTransaccionesListAsync(TransaccionFilter filter, CancellationToken ct = default);
  Task<IReadOnlyList<TransaccionListItemDto>> GetTransaccionesByUuidAsync(string uuid, CancellationToken ct = default);
  Task<IReadOnlyList<TransaccionListItemDto>> GetTransaccionesByComprobanteIdAsync(int comprobanteId, CancellationToken ct = default);
  Task<IReadOnlyList<TransaccionListItemDto>> GetTransaccionesByDoctoRelacionadoIdAsync(int doctoRelacionadoId, CancellationToken ct = default);
  Task<TransaccionCommandResult> InsertTransaccionDoctoRelacionadoAsync(int transaccionId, int doctoRelacionadoId, decimal monto, CancellationToken ct = default);
  Task<TransaccionCommandResult> UpdateDoctoRelacionadoMontoAsync(int transaccionId, int doctoRelacionadoId, decimal monto, CancellationToken ct = default);
  Task<TransaccionCommandResult> GuardarMovimientosAsync(TransaccionMovimientosUpdateRequest request, CancellationToken ct = default);
}
