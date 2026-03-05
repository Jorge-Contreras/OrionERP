using System;

namespace OrionERP.Application.Features.Cfdi.DeclaracionPrevia;

public sealed class Pago20ResumenDetalleDto
{
  public int Comprobante_Id { get; set; }
  public string? ComprobanteUUID { get; set; }
  public string? EmisorRfc { get; set; }
  public string? ReceptorRfc { get; set; }
  public int? Pago_Id { get; set; }
  public DateTime? FechaPago { get; set; }
  public string? FormaDePagoP { get; set; }
  public string? MonedaP { get; set; }
  public decimal MontoPago { get; set; }
  public int DoctoRelacionado_Id { get; set; }
  public Guid? UUID_DoctoRelacionado { get; set; }
  public string? Folio { get; set; }
  public int? NumParcialidad { get; set; }
  public string? MonedaDR { get; set; }
  public decimal ImpSaldoAnt { get; set; }
  public decimal ImpPagado { get; set; }
  public decimal ImpSaldoInsoluto { get; set; }
  public int? Poliza { get; set; }
  public int? Polizas { get; set; }
  public decimal Comp_Actos16 { get; set; }
  public decimal Comp_IVA { get; set; }
  public int? XML_Attachment_ID { get; set; }
}
