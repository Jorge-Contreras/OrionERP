using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.SAT;

namespace OrionERP.Infrastructure.SAT
{
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
        int placeholderTransaccionId = 5505, int top = 100, CancellationToken ct = default)
        => GetByTransaccionAsync(placeholderTransaccionId, top, ct);

    public async Task<IReadOnlyList<ComprobanteListItem>> GetByTransaccionAsync(
        int transaccionId, int top = 100, CancellationToken ct = default)
    {
      const string sql = @"
    SELECT TOP (@Top)
    c.Comprobante_Id        AS ComprobanteId,  
    c.Fecha                 AS Fecha,
    t.UUID                  AS Uuid,
    e.Nombre                AS EmisorNombre,
    r.Nombre                AS ReceptorNombre,
    CAST(c.Total AS decimal(18,4)) AS Total,    -- <— cast to decimal to avoid float/rounding
    tc.Transaccion_ID       AS TransaccionId
FROM dbo.Comprobante c
LEFT JOIN dbo.Emisor e                    ON e.Comprobante_ID = c.Comprobante_Id
LEFT JOIN dbo.Receptor r                  ON r.Comprobante_ID = c.Comprobante_Id
LEFT JOIN dbo.TimbreFiscalDigital t       ON t.Comprobante_ID = c.Comprobante_Id
LEFT JOIN dbo.Transaccion_Comprobante tc  ON tc.Comprobante_ID = c.Comprobante_Id
WHERE tc.Transaccion_ID = @TransaccionId
AND r.RFC = 'OHM191112Q26'  -- filter by your company RFC
ORDER BY c.Comprobante_Id DESC;";

      using var conn = new SqlConnection(_cs);
      var rows = await conn.QueryAsync<ComprobanteListItem>(
          new CommandDefinition(
              sql,
              new { TransaccionId = transaccionId, Top = top },
              commandType: CommandType.Text,
              cancellationToken: ct
          )
      );

      // Dapper returns IEnumerable; convert once to list
      return rows.AsList();
    }
  }
}
