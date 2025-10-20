using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts;
using OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Dapper;

namespace OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Services;

public sealed class SatMetadataIngestService : ISatMetadataIngestService
{
  private readonly SqlConnectionFactory _factory;
  public SatMetadataIngestService(SqlConnectionFactory factory) => _factory = factory;

  public async Task IngestAsync(string metaText, CancellationToken ct = default)
  {
    using var cn = _factory.Create();
    var p = new DynamicParameters();
    p.Add("@MetaData", metaText, DbType.String);
    await cn.ExecuteAsync("dbo.Procesar_SAT_Meta", p, commandType: CommandType.StoredProcedure);
  }
}
