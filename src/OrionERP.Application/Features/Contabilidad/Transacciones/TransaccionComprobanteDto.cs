namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed class TransaccionComprobanteDto
{
  public decimal? PolizaMonto { get; set; }
  public int ComprobanteId { get; set; }
  public string? D { get; set; }
  public DateTime Fecha { get; set; }
  public string? MesGlobal { get; set; }
  public int? AnioGlobal { get; set; }
  public string? Emisor { get; set; }
  public decimal? SubTotal { get; set; }
  public decimal? Descuento { get; set; }
  public decimal? SubTotalDesc { get; set; }
  public decimal? Actos16 { get; set; }
  public decimal? Actos0 { get; set; }
  public decimal? Iva { get; set; }
  public decimal? Ieps { get; set; }
  public decimal? IvaRetenido { get; set; }
  public decimal? IsrRetenido { get; set; }
  public decimal? IepsRetenido { get; set; }
  public decimal? Total { get; set; }
  public string? FolioFiscal { get; set; }
  public string? FormaPago { get; set; }
  public string? TipoDeComprobante { get; set; }
  public string? MetodoPago { get; set; }
  public string? UsoCfdi { get; set; }
  public DateTime? FechaCancelacion { get; set; }
  public string? Estatus { get; set; }
  public int TransaccionId { get; set; }
  public bool Vinculado { get; set; }
}
