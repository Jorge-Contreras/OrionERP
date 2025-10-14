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
  public sealed class TransaccionQueryService : ITransaccionQueryService
  {
    private readonly string _cs;

    public TransaccionQueryService(IConfiguration cfg)
    {
      _cs = cfg.GetSection("ConnectionStrings")["OrionDb"]
           ?? throw new System.InvalidOperationException("Missing ConnectionStrings:OrionDb");
    }

    public async Task<IReadOnlyList<TransaccionListItem>> GetCandidatesAsync(
        DateTime fechaXml,
        decimal montoAbs,
        int daysBack = 60,
        int top = 200,
        CancellationToken ct = default)
    {
      // Mirrors your Access SQL:
      // - WHERE t.Fecha > DATEADD(DAY, -@DaysBack, @FechaXml)
      // - AND ABS(t.Monto) = @MontoAbs
      // - LEFT JOIN TRANSACTION_ATTACHMENT to COUNT() as Adjuntos
      // - LEFT JOIN Transaccion_Comprobante -> Comprobante (Comprobante_Id)
      // - GROUP BY fields, ORDER BY Fecha
      const string sql = @"
SELECT TOP (@Top)
    t.ID                                    AS Id,
    t.Concepto                              AS Concepto,
    t.Fecha                                 AS Fecha,
    ABS(CONVERT(decimal(18,4), t.Monto))    AS Monto1,
    t.Cuenta                                AS Cuenta,
    COUNT(ta.ID)                            AS Adjuntos,
    c.Comprobante_Id                        AS ComprobanteId
FROM dbo.Transacciones t
LEFT JOIN dbo.TRANSACTION_ATTACHMENT ta
       ON ta.TranID = t.ID
LEFT JOIN dbo.Transaccion_Comprobante tc
       ON tc.Transaccion_ID = t.ID
LEFT JOIN dbo.Comprobante c
       ON c.Comprobante_Id = tc.Comprobante_ID
WHERE t.Fecha > DATEADD(DAY, -@DaysBack, @FechaXml)
  AND ABS(CONVERT(decimal(18,4), t.Monto)) = @MontoAbs
GROUP BY t.ID, t.Concepto, t.Fecha, t.Monto, t.Cuenta, c.Comprobante_Id
ORDER BY t.Fecha;";

      using var conn = new SqlConnection(_cs);
      var rows = await conn.QueryAsync<TransaccionListItem>(
          new CommandDefinition(
              sql,
              new
              {
                FechaXml = fechaXml,
                DaysBack = daysBack,
                MontoAbs = montoAbs,
                Top = top
              },
              commandType: CommandType.Text,
              cancellationToken: ct
          )
      );
      return rows.AsList();
    }
  }
}
