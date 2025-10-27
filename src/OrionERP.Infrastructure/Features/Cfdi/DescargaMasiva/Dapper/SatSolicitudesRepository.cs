using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts;

namespace OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Dapper;

public sealed class SatSolicitudesRepository : ISatSolicitudesRepository
{
  private readonly SqlConnectionFactory _factory;
  private readonly ICurrentRfcAccessor _rfcAccessor;

  public SatSolicitudesRepository(SqlConnectionFactory factory, ICurrentRfcAccessor rfcAccessor)
  {
    _factory = factory;
    _rfcAccessor = rfcAccessor;
  }

  public async Task<int> InsertAsync(SatSolicitudDto dto, string requestKey, CancellationToken ct = default)
  {
    const string sql = @"
insert into dbo.SatSolicitudes
(Folio,RfcSolicitante,Issued,TipoSolicitud,EstadoComprobante,RfcEmisor,RfcReceptor,FechaInicialUtc,FechaFinalUtc,
 EstadoSolicitud,CodigoEstadoSolicitud,CodEstatus,Mensaje,NumeroCfdis,PackageCount,RequestKey)
values (@Folio,@RfcSolicitante,@Issued,@TipoSolicitud,@EstadoComprobante,@RfcEmisor,@RfcReceptor,@FechaInicialUtc,@FechaFinalUtc,
        @EstadoSolicitud,@CodigoEstadoSolicitud,@CodEstatus,@Mensaje,@NumeroCfdis,@PackageCount,@RequestKey);
select cast(SCOPE_IDENTITY() as int);";
    using var cn = _factory.Create();
    var id = await cn.ExecuteScalarAsync<int>(sql, new
    {
      dto.Folio,
      dto.RfcSolicitante,
      dto.Issued,
      dto.TipoSolicitud,
      dto.EstadoComprobante,
      dto.RfcEmisor,
      dto.RfcReceptor,
      dto.FechaInicialUtc,
      dto.FechaFinalUtc,
      dto.EstadoSolicitud,
      dto.CodigoEstadoSolicitud,
      dto.CodEstatus,
      dto.Mensaje,
      dto.NumeroCfdis,
      dto.PackageCount,
      RequestKey = requestKey
    });
    return id;
  }

  public async Task<SatSolicitudDto?> FindByRequestKeyAsync(string requestKey, CancellationToken ct = default)
  {
    const string sql = "select top 1 * from dbo.SatSolicitudes where RequestKey = @requestKey";
    using var cn = _factory.Create();
    return await cn.QueryFirstOrDefaultAsync<SatSolicitudDto>(sql, new { requestKey });
  }

  public async Task<SatSolicitudDto?> GetAsync(int id, CancellationToken ct = default)
  {
    const string sql = "select * from dbo.SatSolicitudes where Id = @id";
    using var cn = _factory.Create();
    return await cn.QueryFirstOrDefaultAsync<SatSolicitudDto>(sql, new { id });
  }

  public async Task UpdateVerifySnapshotAsync(int id, SatVerifySnapshot snap, CancellationToken ct = default)
  {
    
    const string sql = @"
update dbo.SatSolicitudes set
  EstadoSolicitud = @Estado,
  CodigoEstadoSolicitud = @CodigoEstadoSolicitud,
  CodEstatus = @CodEstatus,
  Mensaje = @Mensaje,
  NumeroCfdis = @NumeroCfdis,
  LastCheckedAtUtc = SYSUTCDATETIME(),
  TerminatedAtUtc = case when @IsTerminated = 1 and TerminatedAtUtc is null then SYSUTCDATETIME() else TerminatedAtUtc end
where Id = @Id";
    using var cn = _factory.Create();
    await cn.ExecuteAsync(sql, new
    {
      Id = id,
      Estado = (int)snap.Estado,
      snap.CodigoEstadoSolicitud,
      snap.CodEstatus,
      snap.Mensaje,
      snap.NumeroCfdis,
      IsTerminated = snap.IsTerminated ? 1 : 0
    });

    if (snap.PackageIds is not null)
    {
      foreach (var pid in snap.PackageIds)
        await UpsertPackageAsync(id, pid, ct);
    }
  }

  public async Task UpsertPackageAsync(int solicitudId, string packageId, CancellationToken ct = default)
  {
    const string check = @"select 1 from dbo.SatPaquetes where SolicitudId = @solicitudId and PackageId = @packageId";
    const string ins = @"insert into dbo.SatPaquetes (SolicitudId, PackageId) values (@solicitudId, @packageId);
                             update dbo.SatSolicitudes set PackageCount = PackageCount + 1 where Id = @solicitudId;";
    using var cn = _factory.Create();
    var exists = await cn.ExecuteScalarAsync<int?>(check, new { solicitudId, packageId });
    if (exists is null)
      await cn.ExecuteAsync(ins, new { solicitudId, packageId });
  }

  public async Task<IEnumerable<SatSolicitudDto>> ListAsync(int? top = 100, CancellationToken ct = default)
  {
    var currentRfc = _rfcAccessor.CurrentRfc;
    if (string.IsNullOrWhiteSpace(currentRfc))
      return Array.Empty<SatSolicitudDto>();

    var topClause = top.HasValue ? $"top ({top.Value}) " : string.Empty;
    var sql = $"select {topClause}* from dbo.SatSolicitudes where RfcSolicitante = @rfc order by Id desc";
    using var cn = _factory.Create();
    var command = new CommandDefinition(sql, new { rfc = currentRfc }, cancellationToken: ct);
    return await cn.QueryAsync<SatSolicitudDto>(command);
  }

  public async Task SetFolioAsync(int id, Guid folio, CancellationToken ct = default)
{
    const string sql = "update dbo.SatSolicitudes set Folio = @folio where Id = @id";
    using var cn = _factory.Create();
    await cn.ExecuteAsync(sql, new { id, folio
});
}

}
