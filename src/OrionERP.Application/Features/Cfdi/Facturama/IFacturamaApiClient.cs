using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Cfdi.Facturama;

public enum FacturamaIssuedDocumentType
{
  Xml,
  Pdf
}

public sealed record class FacturamaDocumentContent(string Extension, byte[] Bytes);

public interface IFacturamaApiClient
{
  Task<string> CreateIssuedCfdiAsync(string jsonPayload, CancellationToken ct = default);
  Task<FacturamaDocumentContent> DownloadIssuedDocumentAsync(
      string cfdiId,
      FacturamaIssuedDocumentType documentType,
      CancellationToken ct = default);
  Task<string?> FindIssuedCfdiIdByUuidAsync(string uuid, CancellationToken ct = default);
  Task CancelIssuedCfdiAsync(string cfdiId, string motive = "02", CancellationToken ct = default);
}
