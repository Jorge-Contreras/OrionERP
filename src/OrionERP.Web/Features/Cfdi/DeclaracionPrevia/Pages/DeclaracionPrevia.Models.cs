using System;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia
  {
    public class DeclaracionEmitida
    {
      public int Comprobante_Id { get; set; }
      public string? D { get; set; }            // "✓" or "X"
      public DateTime Fecha { get; set; }
      public string? MES_GLOBAL { get; set; }
      public string?  ANIO_GLOBAL { get; set; }
      public string? RECEPTOR { get; set; }
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
      public string?  TipoDeComprobante { get; set; }
      public string? MetodoPago { get; set; }
      public string? UsoCFDI { get; set; }
      public DateTime? FechaCancelacion { get; set; }
      public string? Estatus { get; set; }
      public string? fechastransacciones { get; set; }
      public string? Poliza { get; set; }
      public int? SumaPolizas { get; set; }
      public int? XML_Attachment_ID { get; set; }
    }

    public class DeclaracionRecibida
    {
      public int Comprobante_Id { get; set; }
      public string? D { get; set; }            // "✓" or "X"
      public DateTime Fecha { get; set; }
      public string? MES_GLOBAL { get; set; }
      public string? ANIO_GLOBAL { get; set; }
      public string? EMISOR { get; set; }
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
      public int? SumaPolizas { get; set; }
      public int? XML_Attachment_ID { get; set; }
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
      public int ID { get; set; }                       // t.ID
      public DateTime Fecha { get; set; }               // t.Fecha
      public string? PolizaConcepto { get; set; }       // t.Concepto AS PolizaConcepto
      public decimal Debe { get; set; }                 // rc.Debe
      public decimal Haber { get; set; }                // rc.Haber
      public string? CuentaDebe { get; set; }           // CASE ... AS CuentaDebe
      public string? CuentaHaber { get; set; }          // CASE ... AS CuentaHaber
      public decimal Total_Comprobantes { get; set; }   // ISNULL(cs.Total_Comprobantes, 0) AS Total_Comprobantes
      public int ComprobanteCount { get; set; }         // ISNULL(cs.ComprobanteCount, 0) AS ComprobanteCount
      public bool Revisado { get; set; }                // t.Facturado AS Revisado (bit -> bool)
      public string? Memo { get; set; }                 // t.Memo
      public string Consolidado { get; set; } = "";     // 'TRUE'/'FALSE'/'ERROR' from CASE AS Consolidado
    }

    // Classes for Totals results (with only needed fields):
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
  }
}
