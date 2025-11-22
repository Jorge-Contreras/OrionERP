using System;

namespace OrionERP.Application.Features.Contabilidad.Transacciones
{
    public class TransaccionFilter
    {
        public int? Id { get; set; }
        public DateTime? Fecha { get; set; }
        public string? Texto { get; set; }
        public string? Rfc { get; set; }
    }
}
