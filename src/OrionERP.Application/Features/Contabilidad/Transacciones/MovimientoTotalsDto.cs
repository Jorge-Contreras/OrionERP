namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed class MovimientoTotalsDto
{
  public decimal Debe { get; set; }
  public decimal Haber { get; set; }
  public decimal Balance => Debe - Haber;
}
