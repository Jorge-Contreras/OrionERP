namespace OrionERP.Application.Features.OrdenesTrabajo;

public interface IOrdenTrabajoService
{
  Task<IReadOnlyList<OrdenTrabajoCategoriaDto>> GetCategoriesAsync(CancellationToken ct = default);
  Task<IReadOnlyList<OrdenTrabajoLookupDto>> GetActiveEmployeeOptionsAsync(string? rfc = null, CancellationToken ct = default);
  Task<IReadOnlyList<OrdenTrabajoLookupDto>> GetRoomOptionsAsync(CancellationToken ct = default);
  Task<OrdenTrabajoDashboardDto> GetDashboardAsync(OrdenTrabajoDashboardFilter filter, CancellationToken ct = default);
  Task<IReadOnlyList<OrdenTrabajoListItemDto>> SearchWorkOrdersAsync(OrdenTrabajoSearchFilter filter, CancellationToken ct = default);
  Task<OrdenTrabajoDetailDto?> GetWorkOrderDetailAsync(int id, CancellationToken ct = default);
  Task<OrdenTrabajoCommandResult> CreateManualAsync(OrdenTrabajoCreateRequest request, CancellationToken ct = default);
  Task<OrdenTrabajoCalendarCreateResult> CreateCleaningFromCalendarAsync(OrdenTrabajoCalendarCreateRequest request, CancellationToken ct = default);
  Task<OrdenTrabajoCommandResult> UpdateWorkOrderAsync(int id, OrdenTrabajoUpdateRequest request, CancellationToken ct = default);
  Task<OrdenTrabajoCommandResult> ReplaceWorkOrderStepsAsync(int id, OrdenTrabajoStepsSaveRequest request, CancellationToken ct = default);
  Task<OrdenTrabajoCommandResult> CancelWorkOrderAsync(int id, string reason, string actor, CancellationToken ct = default);
  Task<OrdenTrabajoCommandResult> StartWorkOrderAsync(int id, string actor, int? actorEmployeeId = null, CancellationToken ct = default);
  Task<OrdenTrabajoCommandResult> UpdateStepAsync(int workOrderId, int stepId, OrdenTrabajoStepUpdateRequest request, CancellationToken ct = default);
  Task<OrdenTrabajoCommandResult> AddStepEvidenceAsync(int workOrderId, int stepId, OrdenTrabajoEvidenceCreateRequest request, CancellationToken ct = default);
  Task<OrdenTrabajoCommandResult> RemoveStepEvidenceAsync(int workOrderId, int stepId, int evidenceId, string actor, int? actorEmployeeId = null, CancellationToken ct = default);
  Task<OrdenTrabajoBinaryContent?> GetEvidenceContentAsync(int evidenceId, bool thumbnail = false, CancellationToken ct = default);
  Task<OrdenTrabajoCommandResult> SubmitForReviewAsync(int id, string actor, int? actorEmployeeId = null, CancellationToken ct = default);
  Task<OrdenTrabajoCommandResult> ApproveAsync(int id, string actor, CancellationToken ct = default);
  Task<OrdenTrabajoCommandResult> RejectAsync(int id, string reason, string actor, CancellationToken ct = default);
  Task<IReadOnlyList<OrdenTrabajoTransactionSearchItemDto>> SearchTransactionsAsync(int workOrderId, string? search, CancellationToken ct = default);
  Task<OrdenTrabajoCommandResult> LinkTransactionAsync(int workOrderId, int transaccionId, string actor, CancellationToken ct = default);
  Task<OrdenTrabajoCommandResult> UnlinkTransactionAsync(int workOrderId, int transaccionId, string actor, CancellationToken ct = default);
  Task<IReadOnlyList<OrdenTrabajoTemplateSummaryDto>> GetTemplatesAsync(string? rfc = null, string? categoryCode = null, CancellationToken ct = default);
  Task<OrdenTrabajoTemplateDetailDto?> GetTemplateDetailAsync(int id, CancellationToken ct = default);
  Task<OrdenTrabajoCommandResult> SaveTemplateDraftAsync(OrdenTrabajoTemplateSaveRequest request, CancellationToken ct = default);
  Task<OrdenTrabajoCommandResult> PublishTemplateAsync(int templateId, string actor, CancellationToken ct = default);
  Task<OrdenTrabajoCommandResult> MapRoomTemplateAsync(int roomId, int templateId, string actor, CancellationToken ct = default);
  Task<OrdenTrabajoCommandResult> SeedCleaningTemplatesFromLegacyAsync(string rfc, string actor, CancellationToken ct = default);
  Task<OrdenTrabajoCommandResult> SeedChecklistTemplatesFromLegacyAsync(string rfc, string actor, int asignacion = 36, CancellationToken ct = default);
  Task<IReadOnlyList<OrdenTrabajoCalendarBadgeDto>> GetCalendarBadgesAsync(DateTime startDate, DateTime endDateExclusive, CancellationToken ct = default);
}
