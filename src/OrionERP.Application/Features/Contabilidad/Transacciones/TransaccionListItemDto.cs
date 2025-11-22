using System;

namespace OrionERP.Application.Features.Contabilidad.Transacciones
{
    public class TransaccionListItemDto
    {
        public int Id { get; set; }
        public DateTime Fecha { get; set; }
        public string Concepto { get; set; }
        public decimal Monto { get; set; }
        public string TipoPoliza { get; set; }
        public string FormaPago { get; set; }
    }
}
