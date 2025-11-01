namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed class TransaccionGuardarCerrarRequest
{
  public int TransaccionId { get; set; }
  public string? Concepto { get; set; }
  public DateTime Fecha { get; set; }
  public string? Cuenta { get; set; }
  public decimal Monto { get; set; }
}
