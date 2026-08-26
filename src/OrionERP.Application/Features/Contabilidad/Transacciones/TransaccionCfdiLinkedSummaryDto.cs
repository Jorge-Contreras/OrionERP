using System;
using System.Collections.Generic;

namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed class TransaccionCfdiLinkedSummaryDto
{
  public long ComprobanteId { get; set; }
  public DateTime Fecha { get; set; }
  public string? Tipo { get; set; }
  public string? Serie { get; set; }
  public string? Folio { get; set; }
  public string? Emisor { get; set; }
  public string? EmisorRfc { get; set; }
  public string? Receptor { get; set; }
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
  public decimal TransaccionMonto { get; set; }
  public decimal TransaccionAsignado { get; set; }
  public decimal IvaEsperado { get; set; }
  public decimal IvaContable { get; set; }
  public decimal IvaDiferencia { get; set; }
  public string? TotalCfdiStatus { get; set; }
  public string? TransaccionAsignacionStatus { get; set; }
  public string? IvaStatus { get; set; }
  public int PolizasCount { get; set; }
  public int? XmlAttachmentId { get; set; }
  public List<TransaccionCfdiLinkedPolizaDto> Polizas { get; } = [];
}

public sealed class TransaccionCfdiLinkedPolizaDto
{
  public long ComprobanteId { get; set; }
  public int? DoctoRelacionadoId { get; set; }
  public int TransaccionId { get; set; }
  public DateTime Fecha { get; set; }
  public string? Concepto { get; set; }
  public decimal TransaccionMonto { get; set; }
  public decimal MontoAsignado { get; set; }
  public string? TipoPoliza { get; set; }
  public string? FormaPago { get; set; }
  public decimal ProporcionCfdi { get; set; }
  public decimal IvaEsperado { get; set; }
  public decimal IvaContable { get; set; }
  public decimal IvaDiferencia { get; set; }
  public string? IvaCuentaNivel1 { get; set; }
  public string? IvaStatus { get; set; }
}

public sealed class TransaccionPago20LinkedSummaryDto
{
  public long ComprobanteId { get; set; }
  public string? ComprobanteUuid { get; set; }
  public string? EmisorRfc { get; set; }
  public string? ReceptorRfc { get; set; }
  public string? Direccion { get; set; }
  public DateTime? FechaPago { get; set; }
  public string? FormaDePagoP { get; set; }
  public string? MonedaP { get; set; }
  public decimal MontoPago { get; set; }
  public decimal ImpPagado { get; set; }
  public decimal MontoAsignado { get; set; }
  public decimal CompIva { get; set; }
  public decimal IvaContable { get; set; }
  public decimal IvaDiferencia { get; set; }
  public string? IvaStatus { get; set; }
  public int PolizasCount { get; set; }
  public int RelatedDocumentsCount { get; set; }
  public int? XmlAttachmentId { get; set; }
  public List<TransaccionPago20DoctoRelacionadoDto> Documentos { get; } = [];
  public List<TransaccionCfdiLinkedPolizaDto> Polizas { get; } = [];
}

public sealed class TransaccionPago20DoctoRelacionadoDto
{
  public long ComprobanteId { get; set; }
  public int PagoId { get; set; }
  public int DoctoRelacionadoId { get; set; }
  public Guid? UuidDoctoRelacionado { get; set; }
  public string? Folio { get; set; }
  public int? NumParcialidad { get; set; }
  public string? MonedaDr { get; set; }
  public string? MonedaP { get; set; }
  public DateTime? FechaPago { get; set; }
  public string? FormaDePagoP { get; set; }
  public decimal MontoPago { get; set; }
  public decimal ImpSaldoAnt { get; set; }
  public decimal ImpPagado { get; set; }
  public decimal ImpSaldoInsoluto { get; set; }
  public decimal CompIva { get; set; }
  public decimal MontoAsignado { get; set; }
  public decimal IvaEsperado { get; set; }
  public int PolizasCount { get; set; }
  public List<TransaccionCfdiLinkedPolizaDto> Polizas { get; } = [];
}

public sealed class TransaccionPago20LegacyLinkDto
{
  public long ComprobanteId { get; set; }
  public string? ComprobanteUuid { get; set; }
  public string? EmisorRfc { get; set; }
  public string? ReceptorRfc { get; set; }
  public decimal MontoAsignado { get; set; }
  public int RelatedDocumentsCount { get; set; }
  public string? LegacyReason { get; set; }
  public int? XmlAttachmentId { get; set; }
}

public sealed class TransaccionCfdiLinkedDataDto
{
  public List<TransaccionCfdiLinkedSummaryDto> Comprobantes { get; } = [];
  public List<TransaccionPago20LinkedSummaryDto> ComplementosPago { get; } = [];
  public List<TransaccionPago20LegacyLinkDto> LegacyComplementosPago { get; } = [];
}
