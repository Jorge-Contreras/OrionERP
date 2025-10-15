using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts;

namespace OrionERP.Infrastructure.Features.Cfdi.CargarXmlSat.Services
{
  public sealed class ConciliacionService : IConciliacionService
  {
    private readonly string _cs;

    public ConciliacionService(IConfiguration cfg)
    {
      _cs = cfg.GetSection("ConnectionStrings")["OrionDb"]
           ?? throw new InvalidOperationException("Missing ConnectionStrings:OrionDb");
    }

    public async Task<ConciliacionResult> ConciliarAsync(int comprobanteId, int transaccionId, CancellationToken ct = default)
    {
      using var conn = new SqlConnection(_cs);
      await conn.OpenAsync(ct);
      using var tx = await conn.BeginTransactionAsync(ct) as SqlTransaction;

      try
      {
        // 1) Get Comprobante total (as decimal)
        const string sqlGetTotal = @"
SELECT CAST(c.Total AS decimal(18,4)) 
FROM dbo.Comprobante c
WHERE c.Comprobante_Id = @ComprobanteId;";

        var total = await conn.ExecuteScalarAsync<decimal?>(
            new CommandDefinition(sqlGetTotal, new { ComprobanteId = comprobanteId }, tx, cancellationToken: ct)
        );

        if (total is null)
        {
          await tx!.RollbackAsync(ct);
          return ConciliacionResult.Fail(comprobanteId, transaccionId, "Comprobante no encontrado.");
        }

        // 2) Upsert Transaccion_Comprobante
        //    If exists for this Comprobante, update; else insert.
        const string sqlExists = @"
SELECT Transaccion_ID 
FROM dbo.Transaccion_Comprobante 
WHERE Comprobante_ID = @ComprobanteId;";

        var existingTransId = await conn.ExecuteScalarAsync<int?>(
            new CommandDefinition(sqlExists, new { ComprobanteId = comprobanteId }, tx, cancellationToken: ct)
        );

        if (existingTransId is null)
        {
          const string sqlInsert = @"
INSERT INTO dbo.Transaccion_Comprobante (Transaccion_ID, Comprobante_ID, Monto)
VALUES (@TransaccionId, @ComprobanteId, @Monto);";

          await conn.ExecuteAsync(
              new CommandDefinition(sqlInsert,
                  new { TransaccionId = transaccionId, ComprobanteId = comprobanteId, Monto = total.Value },
                  tx,
                  cancellationToken: ct)
          );
        }
        else
        {
          const string sqlUpdate = @"
UPDATE dbo.Transaccion_Comprobante
SET Transaccion_ID = @TransaccionId, Monto = @Monto
WHERE Comprobante_ID = @ComprobanteId;";

          await conn.ExecuteAsync(
              new CommandDefinition(sqlUpdate,
                  new { TransaccionId = transaccionId, ComprobanteId = comprobanteId, Monto = total.Value },
                  tx,
                  cancellationToken: ct)
          );
        }

        await tx!.CommitAsync(ct);
        return ConciliacionResult.Ok(comprobanteId, transaccionId, total.Value);
      }
      catch (Exception ex)
      {
        try { await tx!.RollbackAsync(ct); } catch { /* ignore */ }
        return ConciliacionResult.Fail(comprobanteId, transaccionId, $"Error al conciliar: {ex.Message}");
      }
    }
  }
}
