namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed class TransaccionHeaderDto
{
  public int Id { get; set; }
  public string? Concepto { get; set; }
  public DateTime Fecha { get; set; }
  public decimal Monto { get; set; }
  public string? Cuenta { get; set; }
  public string? Rfc { get; set; }
  public int? ComprobanteId { get; set; }
  public decimal? ComprobanteMonto { get; set; }
}
