namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public static class AccountingOutboxModules
{
  public const string Restaurant = "RESTAURANT";
  public const string Hospitality = "HOSPITALITY";
}

public static class AccountingOutboxStates
{
  public const string Pending = "Pending";
  public const string Claimed = "Claimed";
  public const string Completed = "Completed";
  public const string Failed = "Failed";
}

/// <summary>
/// Una operación contable reclamada. <see cref="TransaccionId"/> viene poblado cuando
/// un intento anterior alcanzó a crear la póliza pero no a vincularla: entonces el
/// reintento retoma desde el vínculo en vez de crear una póliza huérfana.
/// </summary>
public sealed record AccountingOperation(
  long Id,
  long CompanyId,
  string Rfc,
  string SourceModule,
  string OperationKey,
  string Payload,
  string State,
  int? TransaccionId,
  int Attempts,
  bool WasClaimed = true)
{
  public bool AlreadyCompleted => string.Equals(State, AccountingOutboxStates.Completed, StringComparison.Ordinal);
  /// <summary>
  /// Otro consumidor conserva una reclamación reciente. Este intento no puede
  /// continuar: hacerlo permitiría que dos solicitudes crearan dos pólizas.
  /// </summary>
  public bool InProgress => !AlreadyCompleted && !WasClaimed;
  /// <summary>La póliza ya existe de un intento previo; falta cerrar el vínculo.</summary>
  public bool ResumesFromExistingPolicy => TransaccionId is > 0 && !AlreadyCompleted;
}

/// <summary>
/// Bandeja contable durable. Es propia del contrato contable y no interfiere con
/// <c>restaurante.EventOutbox</c>, que alimenta SignalR.
/// </summary>
public interface IAccountingOutbox
{
  /// <summary>
  /// Registra la operación si no existe y la reclama. Dos intentos concurrentes con
  /// la misma identidad no producen dos contabilizaciones: la identidad es única en
  /// la base. Devuelve la operación ya completada cuando corresponde, para que el
  /// llamador no repita el trabajo.
  /// </summary>
  Task<AccountingOperation> ClaimAsync(
    string sourceModule,
    string operationKey,
    string payload,
    CancellationToken ct = default);

  /// <summary>
  /// Graba la póliza recién creada antes de intentar el vínculo. Es el rastro que
  /// convierte un fallo entre pasos en algo recuperable.
  /// </summary>
  Task RecordPolicyAsync(long operationId, int transaccionId, CancellationToken ct = default);

  /// <summary>Cierra la operación: póliza creada y vínculo confirmado.</summary>
  Task CompleteAsync(long operationId, CancellationToken ct = default);

  /// <summary>
  /// Deja la operación pendiente con su motivo. No la completa ni la descarta: un
  /// descuadre no publica, y el mensaje sigue ahí para el siguiente intento.
  /// </summary>
  Task FailAsync(long operationId, string reason, CancellationToken ct = default);

  /// <summary>Suelta el rastro de una póliza que ya no existe, para poder recrearla.</summary>
  Task ForgetPolicyAsync(long operationId, CancellationToken ct = default);
}
