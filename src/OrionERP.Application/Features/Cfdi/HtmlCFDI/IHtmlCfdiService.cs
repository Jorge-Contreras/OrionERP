using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Cfdi.HtmlCFDI;

public interface IHtmlCfdiService
{
  Task<CfdiReadableDocument> GetHtmlCfdiAsync(int attachmentId, CancellationToken ct = default);
}
