using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts;

namespace OrionERP.Infrastructure.Features.Cfdi.CargarXmlSat.Services;

public sealed class ComprobanteQueryService : IComprobanteQueryService
{
  private readonly string _cs;

  public ComprobanteQueryService(IConfiguration cfg)
  {
    _cs = cfg.GetSection("ConnectionStrings")["OrionDb"]
         ?? throw new System.InvalidOperationException("Missing ConnectionStrings:OrionDb");
  }

  // Área de pendientes de ESTA empresa: alcanza el CFDI por emisor o por receptor,
  // y sigue pendiente mientras ella no lo haya ligado a una póliza suya. Que otra
  // empresa ya lo tenga asignado no lo resuelve aquí.
  public async Task<IReadOnlyList<ComprobanteListItem>> GetUnassignedAsync(
      string rfc,
      int top = 100,
      CancellationToken ct = default)
  {
    var sql = $@"
SELECT TOP (@Top)
    c.Comprobante_Id             AS ComprobanteId,
    c.Fecha                      AS Fecha,
    t.UUID                       AS Uuid,
    e.Nombre                     AS EmisorNombre,
    r.Nombre                     AS ReceptorNombre,
    CAST(c.Total AS decimal(18,4)) AS Total,
    CAST(NULL AS int)            AS TransaccionId
FROM cfdi.Comprobante c
LEFT JOIN cfdi.Emisor e               ON e.Comprobante_ID = c.Comprobante_Id
LEFT JOIN cfdi.Receptor r             ON r.Comprobante_ID = c.Comprobante_Id
LEFT JOIN cfdi.TimbreFiscalDigital t  ON t.Comprobante_ID = c.Comprobante_Id
WHERE {CfdiCompanyScope.AccessPredicateSql("c.Comprobante_Id")}
  AND NOT {CfdiCompanyScope.AssignedToCompanySql("c.Comprobante_Id")}
ORDER BY c.Comprobante_Id DESC;";

    using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<ComprobanteListItem>(
        new CommandDefinition(
            sql,
            new { Top = top, Rfc = rfc },
            commandType: CommandType.Text,
            cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<IReadOnlyList<ComprobanteListItem>> GetByTransaccionAsync(
      int transaccionId,
      string rfc,
      int top = 100,
      CancellationToken ct = default)
  {
    var sql = $@"
SELECT TOP (@Top)
    c.Comprobante_Id        AS ComprobanteId,
    c.Fecha                 AS Fecha,
    t.UUID                  AS Uuid,
    e.Nombre                AS EmisorNombre,
    r.Nombre                AS ReceptorNombre,
    CAST(c.Total AS decimal(18,4)) AS Total,
    tc.Transaccion_ID       AS TransaccionId
FROM cfdi.Comprobante c
LEFT JOIN cfdi.Emisor e                    ON e.Comprobante_ID = c.Comprobante_Id
LEFT JOIN cfdi.Receptor r                  ON r.Comprobante_ID = c.Comprobante_Id
LEFT JOIN cfdi.TimbreFiscalDigital t       ON t.Comprobante_ID = c.Comprobante_Id
LEFT JOIN dbo.Transaccion_Comprobante tc  ON tc.Comprobante_ID = c.Comprobante_Id
WHERE tc.Transaccion_ID = @TransaccionId
  AND {CfdiCompanyScope.AccessPredicateSql("c.Comprobante_Id")}
ORDER BY c.Comprobante_Id DESC;";

    using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<ComprobanteListItem>(
        new CommandDefinition(
            sql,
            new { TransaccionId = transaccionId, Top = top, Rfc = rfc },
            commandType: CommandType.Text,
            cancellationToken: ct));

    return rows.AsList();
  }
}
