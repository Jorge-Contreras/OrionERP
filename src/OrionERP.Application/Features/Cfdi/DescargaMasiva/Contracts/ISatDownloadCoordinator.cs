using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts;

public interface ISatDownloadCoordinator
{
  Task<int> CreateSolicitudAsync(SolicitudParams p, CancellationToken ct = default);
  Task<VerifyResultDto> VerifyAsync(int solicitudId, X509Certificate2 cert, CancellationToken ct = default);
  Task<ProcessSummary> DownloadAndProcessAsync(int solicitudId, X509Certificate2 cert, CancellationToken ct = default);
}
