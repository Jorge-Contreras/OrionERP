namespace OrionERP.Application.Features.Reservaciones.Accounting;

public interface IReservationAccountingService
{
  /// <summary>
  /// Genera una sola póliza durable por empresa, sede y reservación. Un reintento
  /// retoma la póliza registrada en la bandeja en vez de crear otra.
  /// </summary>
  Task<ReservationAccountingResult> GeneratePolicyAsync(
    int reservationId,
    CancellationToken ct = default);
}

public sealed record ReservationAccountingResult(bool Success, string Message, int? TransaccionId)
{
  public static ReservationAccountingResult Ok(string message, int transaccionId)
    => new(true, message, transaccionId);

  public static ReservationAccountingResult Fail(string message, int? transaccionId = null)
    => new(false, message, transaccionId);
}
