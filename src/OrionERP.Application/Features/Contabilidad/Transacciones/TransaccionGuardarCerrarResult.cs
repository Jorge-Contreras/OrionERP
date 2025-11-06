namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed class TransaccionGuardarCerrarResult
{
  private TransaccionGuardarCerrarResult(bool success, string? message, MovimientoTotalsDto? totals)
  {
    Success = success;
    Message = message;
    Totals = totals;
  }

  public bool Success { get; }
  public string? Message { get; }
  public MovimientoTotalsDto? Totals { get; }

  public static TransaccionGuardarCerrarResult Ok(MovimientoTotalsDto totals, string? message = null)
      => new(true, message, totals);

  public static TransaccionGuardarCerrarResult Fail(string message)
      => new(false, message, null);
}
