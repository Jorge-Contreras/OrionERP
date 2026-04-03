using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts
{
  public record class ComprobanteListItem
  {
    public int ComprobanteId { get; init; }
    public DateTime Fecha { get; init; }
    public string? Uuid { get; init; }
    public string? EmisorNombre { get; init; }
    public string? ReceptorNombre { get; init; }
    public decimal Total { get; init; }        // keep as decimal for money
    public int? TransaccionId { get; init; }   // nullable (safe if you later reuse without WHERE)
  }

  public interface IComprobanteQueryService
  {
    Task<IReadOnlyList<ComprobanteListItem>> GetByTransaccionAsync(
        int transaccionId,
        string rfc,
        int top = 100,
        CancellationToken ct = default);

    Task<IReadOnlyList<ComprobanteListItem>> GetUnassignedAsync(
        string rfc,
        int top = 100,
        CancellationToken ct = default);
  }
}
