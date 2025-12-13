using System;
using System.Collections.Generic;

namespace OrionERP.Application.Features.Cfdi.DeclaracionPrevia.DTOs
{
    public class DeclaracionCfdiBase
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
        public int? SumaPolizas { get; set; }
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
        public string Consolidado { get; set; } = "";
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
}
