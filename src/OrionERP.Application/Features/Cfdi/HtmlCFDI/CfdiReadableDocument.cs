namespace OrionERP.Application.Features.Cfdi.HtmlCFDI;

public sealed class CfdiReadableDocument
{
  public string TipoDeComprobante { get; set; } = string.Empty;
  public string? Version { get; set; }
  public string? Serie { get; set; }
  public string? Folio { get; set; }
  public string? Fecha { get; set; }
  public string? LugarExpedicion { get; set; }
  public string? Moneda { get; set; }
  public string? TipoCambio { get; set; }
  public string? MetodoPago { get; set; }
  public string? FormaPago { get; set; }
  public string? SubTotal { get; set; }
  public string? Descuento { get; set; }
  public string? Total { get; set; }
  public string? Exportacion { get; set; }
  public string? CondicionesDePago { get; set; }

  public CfdiParty? Emisor { get; set; }
  public CfdiParty? Receptor { get; set; }
  public List<CfdiConcepto> Conceptos { get; } = new();
  public CfdiImpuestos? Impuestos { get; set; }
  public CfdiTimbre? Timbre { get; set; }
  public CfdiPago20Data? Pago20 { get; set; }
}

public sealed class CfdiParty
{
  public string? Rfc { get; set; }
  public string? Nombre { get; set; }
  public string? RegimenFiscal { get; set; }
  public string? RegimenFiscalReceptor { get; set; }
  public string? UsoCfdi { get; set; }
  public string? DomicilioFiscalReceptor { get; set; }
}

public sealed class CfdiConcepto
{
  public string? ClaveProdServ { get; set; }
  public string? NoIdentificacion { get; set; }
  public string? Cantidad { get; set; }
  public string? ClaveUnidad { get; set; }
  public string? Unidad { get; set; }
  public string? Descripcion { get; set; }
  public string? ValorUnitario { get; set; }
  public string? Importe { get; set; }
  public string? Descuento { get; set; }
  public string? ObjetoImp { get; set; }
  public List<CfdiImpuestoDetalle> Traslados { get; } = new();
  public List<CfdiImpuestoDetalle> Retenciones { get; } = new();
}

public sealed class CfdiImpuestos
{
  public string? TotalTrasladados { get; set; }
  public string? TotalRetenidos { get; set; }
  public List<CfdiImpuestoDetalle> Traslados { get; } = new();
  public List<CfdiImpuestoDetalle> Retenciones { get; } = new();
}

public sealed class CfdiImpuestoDetalle
{
  public string? Impuesto { get; set; }
  public string? TipoFactor { get; set; }
  public string? TasaOCuota { get; set; }
  public string? Base { get; set; }
  public string? Importe { get; set; }
}

public sealed class CfdiTimbre
{
  public string? Uuid { get; set; }
  public string? FechaTimbrado { get; set; }
  public string? NoCertificadoSat { get; set; }
  public string? RfcProvCertif { get; set; }
  public string? Leyenda { get; set; }
  public string? SelloCfd { get; set; }
  public string? SelloSat { get; set; }
}
