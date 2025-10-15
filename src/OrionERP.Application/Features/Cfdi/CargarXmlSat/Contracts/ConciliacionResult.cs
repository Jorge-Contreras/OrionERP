namespace OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts
{
  public sealed class ConciliacionResult
  {
    public bool Success { get; init; }
    public string? Message { get; init; }
    public int ComprobanteId { get; init; }
    public int TransaccionId { get; init; }
    public decimal Monto { get; init; }

    public static ConciliacionResult Ok(int compId, int tranId, decimal monto) =>
        new ConciliacionResult { Success = true, ComprobanteId = compId, TransaccionId = tranId, Monto = monto, Message = "Conciliado" };

    public static ConciliacionResult Fail(int compId, int tranId, string msg) =>
        new ConciliacionResult { Success = false, ComprobanteId = compId, TransaccionId = tranId, Message = msg };
  }
}
