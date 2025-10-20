using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts;

namespace OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Dapper;

public  sealed class SatPaquetesRepository : ISatPaquetesRepository
{
  private readonly SqlConnectionFactory _factory;
  public SatPaquetesRepository(SqlConnectionFactory factory) => _factory = factory;

  public async Task<IEnumerable<SatPaqueteDto>> ListBySolicitudAsync(int solicitudId, CancellationToken ct = default)
  {
    const string sql = "select * from dbo.SatPaquetes where SolicitudId = @solicitudId order by Id";
    using var cn = _factory.Create();
    return await cn.QueryAsync<SatPaqueteDto>(sql, new { solicitudId });
  }

  public async Task MarkProcessedAsync(int solicitudId, string packageId, SatPackageProcessInfo info, CancellationToken ct = default)
  {
    const string sql = @"
update dbo.SatPaquetes set
  DownloadedAtUtc = case when DownloadedAtUtc is null then SYSUTCDATETIME() else DownloadedAtUtc end,
  ZipSizeBytes    = @ZipSizeBytes,
  Processed       = 1,
  ProcessedAtUtc  = SYSUTCDATETIME(),
  XmlCount        = @XmlCount,
  SuccessCount    = @SuccessCount,
  FailureCount    = @FailureCount,
  ErrorMessage    = @ErrorMessage
where SolicitudId = @SolicitudId and PackageId = @PackageId";
    using var cn = _factory.Create();
    await cn.ExecuteAsync(sql, new
    {
      SolicitudId = solicitudId,
      PackageId = packageId,
      info.ZipSizeBytes,
      info.XmlCount,
      info.SuccessCount,
      info.FailureCount,
      info.ErrorMessage
    });
  }
}
