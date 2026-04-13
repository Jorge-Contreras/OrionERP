using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts
{
  public record SatXmlProcessResult(string FileName, int AttachmentId, bool Success, string? Message);
  public interface ISatXmlInboxService
  {
    Task<SatXmlProcessResult> SaveAndProcessAsync(byte[] xmlBytes, string fileName, CancellationToken ct = default);
    Task<SatXmlProcessResult> SaveAndProcessAsync(Stream xmlStream, string fileName, CancellationToken ct = default);
  }
}
