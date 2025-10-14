using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.SAT
{
  public interface IConciliacionService
  {
    /// <summary>
    /// Links a Comprobante to a Transacción (upsert dbo.Transaccion_Comprobante) and sets Monto = Comprobante.Total.
    /// Does not move attachments (handled next step).
    /// </summary>
    Task<ConciliacionResult> ConciliarAsync(int comprobanteId, int transaccionId, CancellationToken ct = default);
  }
}
