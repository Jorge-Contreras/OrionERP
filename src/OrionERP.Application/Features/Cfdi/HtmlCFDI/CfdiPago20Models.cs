namespace OrionERP.Application.Features.Cfdi.HtmlCFDI;

public sealed class CfdiPago20Data
{
  public string? Version { get; set; }
  public CfdiPago20Totales? Totales { get; set; }
  public List<CfdiPago20Pago> Pagos { get; } = new();
}

public sealed class CfdiPago20Totales
{
  public string? MontoTotalPagos { get; set; }
  public string? TotalTrasladosBaseIva16 { get; set; }
  public string? TotalTrasladosImpuestoIva16 { get; set; }
}

public sealed class CfdiPago20Pago
{
  public string? FechaPago { get; set; }
  public string? FormaDePagoP { get; set; }
  public string? MonedaP { get; set; }
  public string? TipoCambioP { get; set; }
  public string? Monto { get; set; }
  public List<CfdiPago20Docto> Documentos { get; } = new();
  public List<CfdiPago20Traslado> Traslados { get; } = new();
}

public sealed class CfdiPago20Docto
{
  public string? IdDocumento { get; set; }
  public string? Serie { get; set; }
  public string? Folio { get; set; }
  public string? MonedaDr { get; set; }
  public string? NumParcialidad { get; set; }
  public string? ImpSaldoAnt { get; set; }
  public string? ImpPagado { get; set; }
  public string? ImpSaldoInsoluto { get; set; }
  public string? EquivalenciaDr { get; set; }
  public string? ObjetoImpDr { get; set; }
  public List<CfdiPago20Traslado> Traslados { get; } = new();
}

public sealed class CfdiPago20Traslado
{
  public string? Base { get; set; }
  public string? Impuesto { get; set; }
  public string? TipoFactor { get; set; }
  public string? TasaOCuota { get; set; }
  public string? Importe { get; set; }
}
