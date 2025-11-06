namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed class TransaccionMovimientoDto
{
  public int Id { get; set; }
  public int TransaccionId { get; set; }
  public string? NombreCuenta { get; set; }
  public string? Concepto { get; set; }
  public decimal Debe { get; set; }
  public decimal Haber { get; set; }
}
