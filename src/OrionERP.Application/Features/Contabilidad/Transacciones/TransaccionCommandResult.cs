namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed record class TransaccionCommandResult(bool Success, string Message)
{
  public static TransaccionCommandResult Ok(string message)
    => new(true, message);

  public static TransaccionCommandResult Fail(string message)
    => new(false, message);
}
