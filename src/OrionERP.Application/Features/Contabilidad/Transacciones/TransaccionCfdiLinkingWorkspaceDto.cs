using System;
using System.Collections.Generic;

namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed class TransaccionCfdiLinkingWorkspaceDto
{
  public TransaccionCfdiLinkedDataDto Linked { get; } = new();
  public List<TransaccionRegularCfdiLinkCandidateDto> RegularCandidates { get; } = [];
  public List<TransaccionPago20LinkCandidateDto> Pago20Candidates { get; } = [];
}

public sealed class TransaccionRegularCfdiLinkCandidateDto
{
  public bool CanLink { get; set; } = true;
  public string? BlockReason { get; set; }
  public long ComprobanteId { get; set; }
  public DateTime Fecha { get; set; }
  public string? Tipo { get; set; }
  public string? Serie { get; set; }
  public string? Folio { get; set; }
  public string? EmisorRfc { get; set; }
  public string? ReceptorRfc { get; set; }
  public string? Direccion { get; set; }
  public string? Uuid { get; set; }
  public string? FormaPago { get; set; }
  public string? MetodoPago { get; set; }
  public string? UsoCfdi { get; set; }
  public string? Conceptos { get; set; }
  public decimal SubTotal { get; set; }
  public decimal Total { get; set; }
  public decimal Iva { get; set; }
  public decimal IvaRetenido { get; set; }
  public decimal IsrRetenido { get; set; }
  public decimal AsignadoCfdi { get; set; }
  public decimal Pendiente { get; set; }
  public decimal MontoSugerido { get; set; }
  public decimal DiferenciaObjetivo { get; set; }
  public int PolizasCount { get; set; }
  public int MatchScore { get; set; }
  public string? MatchStatus { get; set; }
  public decimal IvaEsperado { get; set; }
  public decimal IvaContable { get; set; }
  public decimal IvaDiferencia { get; set; }
  public string? IvaCuentaNivel1 { get; set; }
  public string? IvaStatus { get; set; }
  public int? XmlAttachmentId { get; set; }
}

public sealed class TransaccionPago20LinkCandidateDto
{
  public bool CanLink { get; set; } = true;
  public string? BlockReason { get; set; }
  public int DoctoRelacionadoId { get; set; }
  public long ComprobanteId { get; set; }
  public string? ComprobanteUuid { get; set; }
  public string? EmisorRfc { get; set; }
  public string? ReceptorRfc { get; set; }
  public string? Direccion { get; set; }
  public DateTime? FechaPago { get; set; }
  public string? FormaDePagoP { get; set; }
  public string? MonedaP { get; set; }
  public decimal MontoPago { get; set; }
  public Guid? UuidDoctoRelacionado { get; set; }
  public string? Folio { get; set; }
  public int? NumParcialidad { get; set; }
  public string? MonedaDr { get; set; }
  public decimal ImpPagado { get; set; }
  public decimal CompIva { get; set; }
  public decimal AsignadoComplemento { get; set; }
  public decimal Pendiente { get; set; }
  public decimal MontoSugerido { get; set; }
  public decimal DiferenciaObjetivo { get; set; }
  public int PolizasCount { get; set; }
  public int RelatedDocumentsCount { get; set; }
  public int MatchScore { get; set; }
  public string? MatchStatus { get; set; }
  public decimal IvaContable { get; set; }
  public decimal IvaDiferencia { get; set; }
  public string? IvaCuentaNivel1 { get; set; }
  public string? IvaStatus { get; set; }
  public int? XmlAttachmentId { get; set; }
}
