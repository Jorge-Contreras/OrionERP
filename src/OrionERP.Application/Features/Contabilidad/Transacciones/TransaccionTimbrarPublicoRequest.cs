namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed class TransaccionTimbrarPublicoRequest
{
  public int TransaccionId { get; set; }
  public decimal Monto { get; set; }
  public string? FormaPago { get; set; }
  public string GlobalMes { get; set; } = string.Empty;
  public int GlobalAnio { get; set; }
}
