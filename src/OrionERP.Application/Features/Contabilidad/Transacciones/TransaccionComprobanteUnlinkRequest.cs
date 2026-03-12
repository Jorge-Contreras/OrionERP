namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed class TransaccionComprobanteUnlinkRequest
{
  public int CurrentTransaccionId { get; set; }
  public long ComprobanteId { get; set; }
  public string? Tipo { get; set; }
}
