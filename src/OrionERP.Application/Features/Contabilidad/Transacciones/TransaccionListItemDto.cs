using System;

namespace OrionERP.Application.Features.Contabilidad.Transacciones
{
    public class TransaccionListItemDto
    {
        public int Id { get; set; }
        public DateTime Fecha { get; set; }
        public string Concepto { get; set; } = string.Empty;
        public decimal Monto { get; set; }
        public decimal MontoAsignado { get; set; }
        public string TipoPoliza { get; set; } = string.Empty;
        public string FormaPago { get; set; } = string.Empty;
    }
}
