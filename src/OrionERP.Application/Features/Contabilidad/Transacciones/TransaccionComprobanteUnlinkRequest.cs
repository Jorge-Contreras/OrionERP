namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed class TransaccionComprobanteUnlinkRequest
{
  public int CurrentTransaccionId { get; set; }
  public int TempTransaccionId { get; set; }
  public int ComprobanteId { get; set; }
}
