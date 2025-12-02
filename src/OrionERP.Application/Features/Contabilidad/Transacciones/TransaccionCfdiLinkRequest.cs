namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed class TransaccionCfdiLinkRequest
{
  public int TransaccionId { get; init; }
  public long ComprobanteId { get; init; }
  public decimal Monto { get; init; }
  public bool UseDoctoRelacionadoTable { get; init; }
}
