using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.SAT
{
  public interface ITransaccionQueryService
  {
    /// <summary>
    /// Replicates Access logic:
    /// - Transacciones.Fecha > DATEADD(DAY, -@DaysBack, @FechaXml)
    /// - ABS(Transacciones.Monto) = @MontoAbs
    /// - Left-joins to TRANSACTION_ATTACHMENT (count) and Transaccion_Comprobante/Comprobante
    /// </summary>
    Task<IReadOnlyList<TransaccionListItem>> GetCandidatesAsync(
        DateTime fechaXml,
        decimal montoAbs,
        int daysBack = 60,
        int top = 200,
        CancellationToken ct = default);
  }
}
