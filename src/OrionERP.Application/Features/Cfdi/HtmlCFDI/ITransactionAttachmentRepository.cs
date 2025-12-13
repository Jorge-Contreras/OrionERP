using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Cfdi.HtmlCFDI;

public interface ITransactionAttachmentRepository
{
  Task<TransactionAttachment?> GetAttachmentAsync(int attachmentId, CancellationToken ct = default);
}
