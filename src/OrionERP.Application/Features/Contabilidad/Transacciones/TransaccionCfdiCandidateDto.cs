using System;

namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed class TransaccionCfdiCandidateDto
{
  public long ComprobanteId { get; set; }
  public DateTime Fecha { get; set; }
  public string? Tipo { get; set; }
  public string? Serie { get; set; }
  public string? Folio { get; set; }
  public string? EmisorRfc { get; set; }
  public string? ReceptorRfc { get; set; }
  public string? Uuid { get; set; }
  public string? FormaPago { get; set; }
  public decimal Total { get; set; }
  public int Polizas { get; set; }
  public decimal Asignado { get; set; }
  public string? MetodoPago { get; set; }
  public string? UsoCfdi { get; set; }
  public string? Conceptos { get; set; }
  public int? XmlAttachmentId { get; set; }
}
