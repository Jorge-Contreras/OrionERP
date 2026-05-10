using System;
using System.Collections.Generic;

namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed class CfdiPolizaLinkingWorkspaceDto
{
  public CfdiPolizaLinkingSummaryDto? Summary { get; set; }
  public List<CfdiPolizaLinkedPolizaDto> Polizas { get; } = [];
  public List<CfdiPolizaCandidateDto> Candidates { get; } = [];
}

public sealed class CfdiPolizaLinkingSummaryDto
{
  public int ComprobanteId { get; set; }
  public DateTime Fecha { get; set; }
  public string? Tipo { get; set; }
  public string? Serie { get; set; }
  public string? Folio { get; set; }
  public string? EmisorRfc { get; set; }
  public string? ReceptorRfc { get; set; }
  public string? Emisor { get; set; }
  public string? Receptor { get; set; }
  public string? Direccion { get; set; }
  public string? Uuid { get; set; }
  public string? FormaPago { get; set; }
  public string? MetodoPago { get; set; }
  public string? UsoCfdi { get; set; }
  public decimal SubTotal { get; set; }
  public decimal Total { get; set; }
  public decimal Iva { get; set; }
  public decimal IvaRetenido { get; set; }
  public decimal IsrRetenido { get; set; }
  public decimal AsignadoCfdi { get; set; }
  public decimal Pendiente { get; set; }
  public decimal IvaEsperado { get; set; }
  public decimal IvaContable { get; set; }
  public decimal IvaDiferencia { get; set; }
  public string? TotalCfdiStatus { get; set; }
  public string? IvaStatus { get; set; }
  public int PolizasCount { get; set; }
  public int? XmlAttachmentId { get; set; }
}

public sealed class CfdiPolizaLinkedPolizaDto
{
  public int TransaccionId { get; set; }
  public DateTime Fecha { get; set; }
  public string? Concepto { get; set; }
  public decimal TransaccionMonto { get; set; }
  public decimal MontoAsignado { get; set; }
  public string? TipoPoliza { get; set; }
  public string? FormaPago { get; set; }
  public decimal IvaEsperado { get; set; }
  public decimal IvaContable { get; set; }
  public decimal IvaDiferencia { get; set; }
  public string? IvaCuentaNivel1 { get; set; }
  public string? IvaStatus { get; set; }
}

public sealed class CfdiPolizaCandidateDto
{
  public int Id { get; set; }
  public DateTime Fecha { get; set; }
  public string? Concepto { get; set; }
  public decimal Monto { get; set; }
  public decimal MontoAsignado { get; set; }
  public decimal Disponible { get; set; }
  public decimal MontoSugerido { get; set; }
  public decimal DiferenciaObjetivo { get; set; }
  public string? TipoPoliza { get; set; }
  public string? FormaPago { get; set; }
  public int MatchScore { get; set; }
  public string? MatchStatus { get; set; }
  public decimal IvaContable { get; set; }
  public decimal IvaDiferencia { get; set; }
  public string? IvaStatus { get; set; }
}

public sealed class Pago20PolizaLinkingWorkspaceDto
{
  public Pago20PolizaLinkingSummaryDto? Summary { get; set; }
  public List<Pago20PolizaDoctoRelacionadoDto> Documentos { get; } = [];
  public List<CfdiPolizaLinkedPolizaDto> Polizas { get; } = [];
  public List<CfdiPolizaCandidateDto> Candidates { get; } = [];
}

public sealed class Pago20PolizaLinkingSummaryDto
{
  public int DoctoRelacionadoId { get; set; }
  public int ComprobanteId { get; set; }
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
  public decimal IvaContable { get; set; }
  public decimal IvaDiferencia { get; set; }
  public string? TotalComplementoStatus { get; set; }
  public string? IvaStatus { get; set; }
  public int PolizasCount { get; set; }
  public int RelatedDocumentsCount { get; set; }
  public int? XmlAttachmentId { get; set; }
}

public sealed class Pago20PolizaDoctoRelacionadoDto
{
  public int DoctoRelacionadoId { get; set; }
  public Guid? UuidDoctoRelacionado { get; set; }
  public string? Folio { get; set; }
  public int? NumParcialidad { get; set; }
  public string? MonedaDr { get; set; }
  public decimal ImpSaldoAnt { get; set; }
  public decimal ImpPagado { get; set; }
  public decimal ImpSaldoInsoluto { get; set; }
  public decimal CompIva { get; set; }
}
