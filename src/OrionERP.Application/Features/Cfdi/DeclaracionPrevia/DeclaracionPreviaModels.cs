using System;

namespace OrionERP.Application.Features.Cfdi.DeclaracionPrevia;

public interface IDeclaracionComprobanteItem
{
  int Comprobante_Id { get; }
  string? Poliza { get; set; }
  int? XML_Attachment_ID { get; set; }
}

public class DeclaracionCfdiBase : IDeclaracionComprobanteItem
{
  public int Comprobante_Id { get; set; }
  public string? D { get; set; }
  public DateTime Fecha { get; set; }
  public string? MES_GLOBAL { get; set; }
  public string? ANIO_GLOBAL { get; set; }
  public string? EMISOR { get; set; }
  public string? RFC_EMISOR { get; set; }
  public string? RECEPTOR { get; set; }
  public string? RFC_RECEPTOR { get; set; }
  public decimal SubTotal { get; set; }
  public decimal Descuento { get; set; }
  public decimal SubTotal_Desc { get; set; }
  public decimal Actos_16 { get; set; }
  public decimal Actos_0 { get; set; }
  public decimal IVA { get; set; }
  public decimal IEPS { get; set; }
  public decimal IVA_RETENIDO { get; set; }
  public decimal ISR_RETENIDO { get; set; }
  public decimal IEPS_RETENIDO { get; set; }
  public decimal Total { get; set; }
  public string? FOLIO_FISCAL { get; set; }
  public string? FormaPago { get; set; }
  public string? TipoDeComprobante { get; set; }
  public string? MetodoPago { get; set; }
  public string? UsoCFDI { get; set; }
  public DateTime? FechaCancelacion { get; set; }
  public string? Estatus { get; set; }
  public string? fechastransacciones { get; set; }
  public string? Poliza { get; set; }
  public decimal? SumaPolizas { get; set; }
  public decimal? IvaEsperado { get; set; }
  public decimal? IvaContable { get; set; }
  public decimal? IvaDiferencia { get; set; }
  public string? TotalCfdiStatus { get; set; }
  public string? TransaccionAsignacionStatus { get; set; }
  public string? IvaStatus { get; set; }
  public int? XML_Attachment_ID { get; set; }
  public bool EsEmitida { get; set; }
  public bool EsRecibida { get; set; }

  protected void CopyFrom(DeclaracionCfdiBase source)
  {
    if (source == null)
    {
      return;
    }

    Comprobante_Id = source.Comprobante_Id;
    D = source.D;
    Fecha = source.Fecha;
    MES_GLOBAL = source.MES_GLOBAL;
    ANIO_GLOBAL = source.ANIO_GLOBAL;
    EMISOR = source.EMISOR;
    RFC_EMISOR = source.RFC_EMISOR;
    RECEPTOR = source.RECEPTOR;
    RFC_RECEPTOR = source.RFC_RECEPTOR;
    SubTotal = source.SubTotal;
    Descuento = source.Descuento;
    SubTotal_Desc = source.SubTotal_Desc;
    Actos_16 = source.Actos_16;
    Actos_0 = source.Actos_0;
    IVA = source.IVA;
    IEPS = source.IEPS;
    IVA_RETENIDO = source.IVA_RETENIDO;
    ISR_RETENIDO = source.ISR_RETENIDO;
    IEPS_RETENIDO = source.IEPS_RETENIDO;
    Total = source.Total;
    FOLIO_FISCAL = source.FOLIO_FISCAL;
    FormaPago = source.FormaPago;
    TipoDeComprobante = source.TipoDeComprobante;
    MetodoPago = source.MetodoPago;
    UsoCFDI = source.UsoCFDI;
    FechaCancelacion = source.FechaCancelacion;
    Estatus = source.Estatus;
    fechastransacciones = source.fechastransacciones;
    Poliza = source.Poliza;
    SumaPolizas = source.SumaPolizas;
    IvaEsperado = source.IvaEsperado;
    IvaContable = source.IvaContable;
    IvaDiferencia = source.IvaDiferencia;
    TotalCfdiStatus = source.TotalCfdiStatus;
    TransaccionAsignacionStatus = source.TransaccionAsignacionStatus;
    IvaStatus = source.IvaStatus;
    XML_Attachment_ID = source.XML_Attachment_ID;
    EsEmitida = source.EsEmitida;
    EsRecibida = source.EsRecibida;
  }
}

public class DeclaracionEmitida : DeclaracionCfdiBase
{
  public DeclaracionEmitida()
  {
  }

  public DeclaracionEmitida(DeclaracionCfdiBase source)
  {
    CopyFrom(source);
  }
}

public class DeclaracionRecibida : DeclaracionCfdiBase
{
  public DeclaracionRecibida()
  {
  }

  public DeclaracionRecibida(DeclaracionCfdiBase source)
  {
    CopyFrom(source);
  }
}

public class DesfaseItem
{
  public int Comprobante_Id { get; set; }
  public int? Transaccion_Id { get; set; }
  public DateTime? FechaComprobante { get; set; }
  public string? MesComprobante { get; set; }
  public string? AnioComprobante { get; set; }
  public string? RFC_Emisor { get; set; }
  public string? RFC_Receptor { get; set; }
  public decimal? TotalComprobante { get; set; }
  public string? CuentaPago { get; set; }
  public DateTime? FechaTransaccion { get; set; }
  public string? Observaciones { get; set; }
}

public class PolizaNoConsolidada
{
  public int ID { get; set; }
  public DateTime Fecha { get; set; }
  public string? PolizaConcepto { get; set; }
  public decimal Debe { get; set; }
  public decimal Haber { get; set; }
  public string? CuentaDebe { get; set; }
  public string? CuentaHaber { get; set; }
  public decimal Total_Comprobantes { get; set; }
  public int ComprobanteCount { get; set; }
  public bool Revisado { get; set; }
  public string? Memo { get; set; }
  public string Consolidado { get; set; } = string.Empty;
}

public class DeclaracionTotales
{
  public int CountCFDIs { get; set; }
  public decimal SumSubTotal { get; set; }
  public decimal SumDescuento { get; set; }
  public decimal SumSubTotalDesc { get; set; }
  public decimal SumActos16 { get; set; }
  public decimal SumActos0 { get; set; }
  public decimal SumIVA { get; set; }
  public decimal SumIEPS { get; set; }
  public decimal SumIVA_RETENIDO { get; set; }
  public decimal SumISR_RETENIDO { get; set; }
  public decimal SumIEPS_RETENIDO { get; set; }
  public decimal SumTotal { get; set; }
}

public class DesfaseTotales
{
  public int CountCFDIs { get; set; }
  public string? SumTotal { get; set; }
}

public class PagoComplementoResumen
{
  public int Pago_Id { get; set; }
  public string? Folio { get; set; }
  public DateTime FechaPago { get; set; }
  public string? FormaDePagoP { get; set; }
  public string? MonedaP { get; set; }
  public decimal MontoPago { get; set; }
  public int NumParcialidad { get; set; }
  public string? MonedaDR { get; set; }
  public decimal ImpSaldoAnt { get; set; }
  public decimal ImpPagado { get; set; }
  public decimal ImpSaldoInsoluto { get; set; }
  public decimal Tot_P_Traslados { get; set; }
  public decimal Tot_P_Retenciones { get; set; }
  public decimal Tot_DR_Traslados { get; set; }
  public decimal Tot_DR_Retenciones { get; set; }
}

public class DeclaracionComplementoBase : IDeclaracionComprobanteItem
{
  public int Comprobante_Id { get; set; }
  public string? Folio { get; set; }
  public string D { get; set; } = string.Empty;
  public string? Poliza { get; set; }
  public int? Polizas { get; set; }
  public DateTime? FechaPago { get; set; }
  public string? MES_GLOBAL { get; set; }
  public string? ANIO_GLOBAL { get; set; }
  public int? NumParcialidad { get; set; }
  public decimal? ImpSaldoAnt { get; set; }
  public decimal? ImpPagado { get; set; }
  public decimal? ImpSaldoInsoluto { get; set; }
  public decimal? Comp_Actos16 { get; set; }
  public decimal? Comp_IVA { get; set; }
  public decimal? MontoPago { get; set; }
  public decimal? AsignadoComplemento { get; set; }
  public decimal? IvaContable { get; set; }
  public decimal? IvaDiferencia { get; set; }
  public string? TotalComplementoStatus { get; set; }
  public string? IvaStatus { get; set; }
  public string? ComprobanteUUID { get; set; }
  public string? EmisorRfc { get; set; }
  public string? ReceptorRfc { get; set; }
  public int? Pago_Id { get; set; }
  public string? FormaDePagoP { get; set; }
  public string? MonedaP { get; set; }
  public int? DoctoRelacionado_Id { get; set; }
  public Guid? UUID_DoctoRelacionado { get; set; }
  public string? MonedaDR { get; set; }
  public bool EsEmitida { get; set; }
  public bool EsRecibida { get; set; }
  public int? XML_Attachment_ID { get; set; }

  protected void CopyFrom(DeclaracionComplementoBase source)
  {
    if (source == null)
    {
      return;
    }

    Comprobante_Id = source.Comprobante_Id;
    Folio = source.Folio;
    D = source.D;
    Poliza = source.Poliza;
    Polizas = source.Polizas;
    FechaPago = source.FechaPago;
    MES_GLOBAL = source.MES_GLOBAL;
    ANIO_GLOBAL = source.ANIO_GLOBAL;
    NumParcialidad = source.NumParcialidad;
    ImpSaldoAnt = source.ImpSaldoAnt;
    ImpPagado = source.ImpPagado;
    ImpSaldoInsoluto = source.ImpSaldoInsoluto;
    Comp_Actos16 = source.Comp_Actos16;
    Comp_IVA = source.Comp_IVA;
    MontoPago = source.MontoPago;
    AsignadoComplemento = source.AsignadoComplemento;
    IvaContable = source.IvaContable;
    IvaDiferencia = source.IvaDiferencia;
    TotalComplementoStatus = source.TotalComplementoStatus;
    IvaStatus = source.IvaStatus;
    ComprobanteUUID = source.ComprobanteUUID;
    EmisorRfc = source.EmisorRfc;
    ReceptorRfc = source.ReceptorRfc;
    Pago_Id = source.Pago_Id;
    FormaDePagoP = source.FormaDePagoP;
    MonedaP = source.MonedaP;
    DoctoRelacionado_Id = source.DoctoRelacionado_Id;
    UUID_DoctoRelacionado = source.UUID_DoctoRelacionado;
    MonedaDR = source.MonedaDR;
    EsEmitida = source.EsEmitida;
    EsRecibida = source.EsRecibida;
    XML_Attachment_ID = source.XML_Attachment_ID;
  }
}

public class DeclaracionComplementoEmitido : DeclaracionComplementoBase
{
  public DeclaracionComplementoEmitido()
  {
  }

  public DeclaracionComplementoEmitido(DeclaracionComplementoBase source)
  {
    CopyFrom(source);
  }
}

public class DeclaracionComplementoRecibido : DeclaracionComplementoBase
{
  public DeclaracionComplementoRecibido()
  {
  }

  public DeclaracionComplementoRecibido(DeclaracionComplementoBase source)
  {
    CopyFrom(source);
  }
}
