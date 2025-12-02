using System;

namespace OrionERP.Application.Features.Contabilidad.Transacciones
{
    public class TransaccionFilter
    {
        public int? Id { get; set; }
        public string? Rfc { get; set; }
        public int? Year { get; set; }
        public int? Month { get; set; }
        public string? Concepto { get; set; }
        public decimal? Monto { get; set; }
        public string? TipoPoliza { get; set; }
        public string? FormaPago { get; set; }
        public string? SortBy { get; set; }
        public bool SortAsc { get; set; } = true;
    }
}
