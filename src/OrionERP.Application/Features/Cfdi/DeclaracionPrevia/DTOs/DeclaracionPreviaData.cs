using System;
using System.Collections.Generic;

namespace OrionERP.Application.Features.Cfdi.DeclaracionPrevia.DTOs
{
    public class DeclaracionPreviaData
    {
        public List<DeclaracionEmitida> Emitidas { get; set; } = new();
        public DeclaracionTotales EmitidasTotals { get; set; } = new();
        public List<DeclaracionEmitida> EmitidasNomina { get; set; } = new();
        public DeclaracionTotales EmitidasNominaTotals { get; set; } = new();
        public List<DeclaracionRecibida> Recibidas { get; set; } = new();
        public DeclaracionTotales RecibidasTotals { get; set; } = new();
        public List<DeclaracionRecibida> RecibidasNomina { get; set; } = new();
        public DeclaracionTotales RecibidasNominaTotals { get; set; } = new();
        public List<DeclaracionEmitida> TipoEEmitidas { get; set; } = new();
        public DeclaracionTotales TipoEEmitidasTotals { get; set; } = new();
        public List<DeclaracionRecibida> TipoERecibidas { get; set; } = new();
        public DeclaracionTotales TipoERecibidasTotals { get; set; } = new();
        public List<DesfaseItem> Desfase { get; set; } = new();
        public DesfaseTotales DesfaseTotals { get; set; } = new();
        public List<PolizaNoConsolidada> PolizasNoConsolidadas { get; set; } = new();
        public string ImpuestosSummary { get; set; } = "";
        public string BancosCajaSummary { get; set; } = "";
    }
}
