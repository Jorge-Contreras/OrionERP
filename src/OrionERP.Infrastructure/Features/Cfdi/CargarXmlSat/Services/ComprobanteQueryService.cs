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

  // “Pending” area: all invoices tied to the placeholder transacción.
  public Task<IReadOnlyList<ComprobanteListItem>> GetRecentFromPlaceholderAsync(
      string rfc,
      int placeholderTransaccionId = 5505,
      int top = 100,
      CancellationToken ct = default)
      => GetByTransaccionAsync(placeholderTransaccionId, rfc, top, ct);

  public async Task<IReadOnlyList<ComprobanteListItem>> GetByTransaccionAsync(
      int transaccionId,
      string rfc,
      int top = 100,
      CancellationToken ct = default)
  {
    const string sql = @"
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
LEFT JOIN cfdi.Transaccion_Comprobante tc  ON tc.Comprobante_ID = c.Comprobante_Id
WHERE tc.Transaccion_ID = @TransaccionId
  AND r.RFC = @Rfc
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
