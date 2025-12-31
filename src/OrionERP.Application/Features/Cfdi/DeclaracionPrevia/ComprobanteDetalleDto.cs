using System;

namespace OrionERP.Application.Features.Cfdi.DeclaracionPrevia;

public sealed class ComprobanteDetalleDto
{
  public int Comprobante_Id { get; set; }
  public string? UsoCFDI { get; set; }
  public string? RECEPTOR { get; set; }
  public string? EMISOR { get; set; }
  public string? FOLIO_FISCAL { get; set; }
  public DateTime Fecha { get; set; }
  public decimal SubTotal { get; set; }
  public decimal SubTotal_Desc { get; set; }
  public decimal IVA { get; set; }
  public decimal IEPS { get; set; }
  public decimal IVA_RETENIDO { get; set; }
  public decimal ISR_RETENIDO { get; set; }
  public decimal IEPS_RETENIDO { get; set; }
  public decimal Actos_16 { get; set; }
  public decimal Actos_0 { get; set; }
  public decimal Total { get; set; }
}
