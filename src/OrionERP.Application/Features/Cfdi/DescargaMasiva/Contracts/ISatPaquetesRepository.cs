using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts;

public interface ISatPaquetesRepository
{
  Task<IEnumerable<SatPaqueteDto>> ListBySolicitudAsync(int solicitudId, CancellationToken ct = default);
  Task MarkProcessedAsync(int solicitudId, string packageId, SatPackageProcessInfo info, CancellationToken ct = default);
}
