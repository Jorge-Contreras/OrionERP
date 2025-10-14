using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionERP.Application.SAT
{
  public record SatXmlProcessResult(string FileName, int AttachmentId, bool Success, string? Message);
  public interface ISatXmlInboxService
  {
    Task<int> EnsureInboxTransaccionAsync(CancellationToken ct = default); // now returns config ID (5505)
    Task<SatXmlProcessResult> SaveAndProcessAsync(Stream xmlStream, string fileName, CancellationToken ct = default);
  }
}
