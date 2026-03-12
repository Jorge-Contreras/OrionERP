using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts
{
  public record SatXmlProcessResult(string FileName, int AttachmentId, bool Success, string? Message);
  public interface ISatXmlInboxService
  {
    Task<SatXmlProcessResult> SaveAndProcessAsync(Stream xmlStream, string fileName, CancellationToken ct = default);
  }
}
