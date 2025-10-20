using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts;

public interface ISatMetadataIngestService
{
  /// <summary>
  /// Sends the full metadata text to SQL (dbo.Procesar_SAT_Meta).
  /// </summary>
  Task IngestAsync(string metaText, CancellationToken ct = default);
}
