using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts;

public interface ISatSolicitudesRepository
{
  Task<int> InsertAsync(SatSolicitudDto dto, string requestKey, CancellationToken ct = default);
  Task<SatSolicitudDto?> FindByRequestKeyAsync(string requestKey, CancellationToken ct = default);
  Task<SatSolicitudDto?> GetAsync(int id, CancellationToken ct = default);
  Task UpdateVerifySnapshotAsync(int id, SatVerifySnapshot snap, CancellationToken ct = default);
  Task UpsertPackageAsync(int solicitudId, string packageId, CancellationToken ct = default);
  Task<IEnumerable<SatSolicitudDto>> ListAsync(int? top = 100, CancellationToken ct = default);
  Task SetFolioAsync(int id, Guid folio, CancellationToken ct = default);
}
