namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed class TransaccionMovimientoDto
{
    public int Id { get; set; }
    public int TransaccionId { get; set; }
    public string Nivel1 { get; set; } = string.Empty;
    public string Nivel2 { get; set; } = string.Empty;
    public string Nivel3 { get; set; } = string.Empty;
    public string? NombreCuenta { get; set; }
    public string? Concepto { get; set; }
    public decimal Debe { get; set; }
    public decimal Haber { get; set; }
}
