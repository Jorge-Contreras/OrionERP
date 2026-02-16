namespace OrionERP.Application.Features.ReportesFinancieros.Models
{
    public class EstadoPerdidasGananciasRow
    {
        public int ID { get; set; }
        public string DESCRIPCION { get; set; } = string.Empty;
        public decimal PRIMERO { get; set; }
        public decimal SEGUNDO { get; set; }
        public decimal TERCERO { get; set; }
        public decimal CUARTO { get; set; }
    }
}
