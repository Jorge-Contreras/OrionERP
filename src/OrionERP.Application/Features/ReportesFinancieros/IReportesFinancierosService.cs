using System.Collections.Generic;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.ReportesFinancieros
{
    public interface IReportesFinancierosService
    {
        Task<List<HojaTrabajoDto>> GetHojaTrabajoAsync(int anio, string rfc);
    }

    public class HojaTrabajoDto
    {
        public string? Descripcion { get; set; }
        public decimal ENERO { get; set; }
        public decimal FEBRERO { get; set; }
        public decimal MARZO { get; set; }
        public decimal ABRIL { get; set; }
        public decimal MAYO { get; set; }
        public decimal JUNIO { get; set; }
        public decimal JULIO { get; set; }
        public decimal AGOSTO { get; set; }
        public decimal SEPTIEMBRE { get; set; }
        public decimal OCTUBRE { get; set; }
        public decimal NOVIEMBRE { get; set; }
        public decimal DICIEMBRE { get; set; }
    }
}
