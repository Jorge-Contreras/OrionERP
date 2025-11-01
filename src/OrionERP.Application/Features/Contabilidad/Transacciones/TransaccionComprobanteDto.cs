namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed class TransaccionComprobanteDto
{
  public int ComprobanteId { get; set; }
  public string? Serie { get; set; }
  public string? Folio { get; set; }
  public DateTime Fecha { get; set; }
  public decimal Total { get; set; }
  public bool Vinculado { get; set; }
}
