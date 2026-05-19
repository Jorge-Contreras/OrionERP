namespace OrionERP.Application.Features.CuentasPorPagar.Recurrentes;

public interface IRecurrentApService
{
  Task<RecurrentApWorkspaceDto> GetWorkspaceAsync(RecurrentApFilter filter, CancellationToken ct = default);
  Task<RecurrentApPayableSummaryDto?> GetPayableAsync(int payableId, string rfc, CancellationToken ct = default);
  Task<int> SavePayableAsync(RecurrentApUpsertRequest request, string? savedBy, CancellationToken ct = default);
  Task DeactivatePayableAsync(int payableId, string rfc, string? updatedBy, CancellationToken ct = default);
  Task<int> GenerateMissingOccurrencesAsync(string rfc, DateTime throughDate, CancellationToken ct = default);
  Task SetOccurrenceStatusAsync(RecurrentApOccurrenceStatusRequest request, string? updatedBy, CancellationToken ct = default);
  Task LinkTransactionAsync(RecurrentApTransactionLinkRequest request, string? linkedBy, CancellationToken ct = default);
  Task UnlinkTransactionAsync(int paymentId, string rfc, string? unlinkedBy, CancellationToken ct = default);
  Task<IReadOnlyList<RecurrentApTransactionLinkDto>> GetOccurrenceTransactionLinksAsync(int occurrenceId, string rfc, CancellationToken ct = default);
  Task<IReadOnlyList<RecurrentApTransactionCandidateDto>> SearchTransactionsAsync(string rfc, string? search, int top = 25, CancellationToken ct = default);
  Task<IReadOnlyList<RecurrentApTransactionLinkDto>> GetTransactionLinksAsync(int transaccionId, CancellationToken ct = default);
  Task<IReadOnlyList<RecurrentApOccurrenceListItemDto>> SearchOpenOccurrencesAsync(string rfc, string? search, int top = 25, CancellationToken ct = default);
  Task<RecurrentApAttachmentDto> AddAttachmentAsync(RecurrentApAttachmentCreateRequest request, CancellationToken ct = default);
  Task<IReadOnlyList<RecurrentApAttachmentDto>> GetAttachmentsAsync(int occurrenceId, string rfc, CancellationToken ct = default);
  Task<RecurrentApAttachmentContent?> GetAttachmentContentAsync(int attachmentId, string rfc, CancellationToken ct = default);
  Task DeleteAttachmentAsync(int attachmentId, string rfc, string? deletedBy, CancellationToken ct = default);
}
