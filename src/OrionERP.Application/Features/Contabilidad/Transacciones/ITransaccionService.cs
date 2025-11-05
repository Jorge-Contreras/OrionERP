namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public interface ITransaccionService
{
  Task<TransaccionHeaderDto?> GetHeaderAsync(int transaccionId, CancellationToken ct = default);
  Task<IReadOnlyList<TransaccionMovimientoDto>> GetMovimientosAsync(int transaccionId, CancellationToken ct = default);
  Task<MovimientoTotalsDto> GetMovimientoTotalsAsync(int transaccionId, CancellationToken ct = default);
  Task<IReadOnlyList<TransaccionAttachmentDto>> GetAttachmentsAsync(int transaccionId, CancellationToken ct = default);
  Task<TransaccionAttachmentContent?> GetAttachmentContentAsync(int attachmentId, CancellationToken ct = default);
  Task<IReadOnlyList<TransaccionComprobanteDto>> GetComprobantesAsync(int transaccionId, CancellationToken ct = default);
  Task ToggleComprobanteAsync(int transaccionId, int comprobanteId, bool vincular, CancellationToken ct = default);
  Task<TransaccionGuardarCerrarResult> GuardarYCerrarAsync(TransaccionGuardarCerrarRequest request, CancellationToken ct = default);
  Task<IReadOnlyList<LookupInt32Dto>> GetCategoriasAsync(string rfc, CancellationToken ct = default);
  Task<IReadOnlyList<LookupInt32Dto>> GetActividadesAsync(string rfc, CancellationToken ct = default);
  Task<IReadOnlyList<LookupInt32Dto>> GetComprasAsync(string rfc, CancellationToken ct = default);
  Task<IReadOnlyList<LookupInt32Dto>> SearchActividadesAsync(string rfc, string? term, int maxResults = 50, CancellationToken ct = default);
  Task<IReadOnlyList<LookupInt32Dto>> SearchComprasAsync(string rfc, string? term, int maxResults = 50, CancellationToken ct = default);
  Task<IReadOnlyList<LookupInt32Dto>> GetServiciosAsync(string rfc, CancellationToken ct = default);
  Task<IReadOnlyList<LookupStringDto>> GetReservacionesAsync(string rfc, CancellationToken ct = default);
  Task<IReadOnlyList<LookupInt32Dto>> GetNominasAsync(string rfc, CancellationToken ct = default);
  Task<IReadOnlyList<FormaPagoLookupDto>> GetFormasPagoAsync(CancellationToken ct = default);
}
