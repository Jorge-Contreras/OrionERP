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
    }

    public class DeclaracionRecibida
    {
      public int Comprobante_Id { get; set; }
      public string? D { get; set; }
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
      public DateTime? FechaPago { get; set; }    // if the SP returns payment date
      public string? Estatus { get; set; }
      public long? TransaccionVinculada { get; set; }  // if the SP returns linked transaction ID
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
      public int Transaccion_ID { get; set; }
      public DateTime Fecha { get; set; }
      public string? Concepto { get; set; }
      public decimal Monto { get; set; }
      public string? Cuenta { get; set; }
      public string? Tipo_Poliza { get; set; }
      public string? Forma_Pago { get; set; }
      public string? RFC { get; set; }
      public string? Observaciones { get; set; }
    }

    // Classes for Totals results (with only needed fields):
    public class DeclaracionTotales
    {
      public int CountCFDIs { get; set; }
      public string? SumSubTotal { get; set; }
      public string? SumDescuento { get; set; }
      public string? SumSubTotalDesc { get; set; }
      public string? SumActos16 { get; set; }
      public string? SumActos0 { get; set; }
      public string? SumIVA { get; set; }
      public string? SumIEPS { get; set; }
      public string? SumIVA_RETENIDO { get; set; }
      public string? SumISR_RETENIDO { get; set; }
      public string? SumIEPS_RETENIDO { get; set; }
      public string? SumTotal { get; set; }
    }

    public class DesfaseTotales
    {
      public int CountCFDIs { get; set; }
      public string? SumTotal { get; set; }
    }
  }
}
