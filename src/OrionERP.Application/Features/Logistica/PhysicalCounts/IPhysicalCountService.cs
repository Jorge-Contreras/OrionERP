using System.Threading;
using System.Threading.Tasks;
using OrionERP.Application.Features.Logistica.Shared;

namespace OrionERP.Application.Features.Logistica.PhysicalCounts;

public interface IPhysicalCountService
{
  Task<IReadOnlyList<PhysicalCountSessionSummaryDto>> GetSessionsAsync(CancellationToken ct = default);
  Task<PhysicalCountSessionDetailDto?> GetSessionAsync(int sessionId, CancellationToken ct = default);
  Task<LogisticsCommandResult> CreateSessionAsync(PhysicalCountSessionCreateRequest request, CancellationToken ct = default);
  Task<LogisticsCommandResult> CaptureLineAsync(PhysicalCountLineCaptureRequest request, CancellationToken ct = default);
  Task<LogisticsCommandResult> DeleteDraftSessionAsync(int sessionId, CancellationToken ct = default);
  Task<LogisticsCommandResult> SubmitSessionAsync(int sessionId, string submittedBy, CancellationToken ct = default);
  Task<LogisticsCommandResult> ApproveSessionAsync(int sessionId, string approvedBy, CancellationToken ct = default);
  Task<LogisticsCommandResult> PostSessionAsync(int sessionId, string postedBy, CancellationToken ct = default);
  Task<LogisticsBinaryContent?> GetAttachmentContentAsync(int attachmentId, CancellationToken ct = default);
}
