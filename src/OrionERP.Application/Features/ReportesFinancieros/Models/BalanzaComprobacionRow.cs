using System;

namespace OrionERP.Application.Features.ReportesFinancieros.Models
{
    public class BalanzaComprobacionRow
    {
        public string ModoReporte { get; set; } = string.Empty;
        public int Anio { get; set; }
        public int? Mes { get; set; }
        public DateTime PeriodoInicio { get; set; }
        public DateTime PeriodoFin { get; set; }
        public string RFC { get; set; } = string.Empty;
        public string Nivel1 { get; set; } = string.Empty;
        public string? Nivel2 { get; set; }
        public string? Nivel3 { get; set; }
        public string? Nivel1Descripcion { get; set; }
        public string? Nivel2Descripcion { get; set; }
        public string? Nivel3Descripcion { get; set; }
        public string? Nombre_Cuenta { get; set; }
        public decimal Debe_Ant { get; set; }
        public decimal Haber_Ant { get; set; }
        public decimal Saldo_Inicial { get; set; }
        public decimal Debe_Mes { get; set; }
        public decimal Haber_Mes { get; set; }
        public decimal Saldo_Mes { get; set; }
        public decimal Saldo_Final { get; set; }
        public int NivelJerarquia { get; set; }
        public string SortNivel2 { get; set; } = string.Empty;
        public string SortNivel3 { get; set; } = string.Empty;
    }
}
