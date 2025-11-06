namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed class TransaccionHeaderDto
{
  public int Id { get; set; }
  public string? Concepto { get; set; }
  public DateTime Fecha { get; set; }
  public decimal Monto { get; set; }
  public string? Cuenta { get; set; }
  public string? Rfc { get; set; }
  public int Categoria { get; set; }
  public bool? Facturado { get; set; }
  public string? Referencia { get; set; }
  public string? Memo { get; set; }
  public int? ProyectoId { get; set; }
  public int? CompraId { get; set; }
  public int? ServicioId { get; set; }
  public string? ReservacionId { get; set; }
  public int? NominaId { get; set; }
  public string? TipoPoliza { get; set; }
  public string? FormaPago { get; set; }
  public int? ComprobanteId { get; set; }
  public decimal? ComprobanteMonto { get; set; }
}
