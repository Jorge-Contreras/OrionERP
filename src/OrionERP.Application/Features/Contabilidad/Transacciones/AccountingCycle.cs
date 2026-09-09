namespace OrionERP.Application.Features.Contabilidad.Transacciones;

/// <summary>
/// Estados del ciclo contable formal. <c>NULL</c> en la base significa que la póliza
/// nunca entró al ciclo: es historia legacy y se sigue operando por <c>Estatus</c>.
/// El ciclo no convierte ni reinterpreta ese estado heredado.
/// </summary>
public static class AccountingCycleStates
{
  public const string Draft = "Draft";
  public const string Posted = "Posted";
  public const string Reversed = "Reversed";

  public static readonly IReadOnlySet<string> Known =
    new HashSet<string>(StringComparer.Ordinal) { Draft, Posted, Reversed };

  /// <summary>Publicada o reversada: el asiento ya no se edita ni se borra.</summary>
  public static bool IsImmutable(string? state)
    => string.Equals(state, Posted, StringComparison.Ordinal)
    || string.Equals(state, Reversed, StringComparison.Ordinal);
}

/// <summary>Cierre del libro mayor. Nada que ver con <c>fiscal.DeclaracionCierre</c>, que es declarativo.</summary>
public static class AccountingPeriodStates
{
  public const string Open = "Open";
  public const string Closed = "Closed";
}

/// <summary>
/// Estado del ciclo para una póliza. <paramref name="CycleEnabled"/> es la
/// habilitación por empresa: mientras esté apagada, la operación existente no cambia.
/// <paramref name="LegacyCompatible"/> marca la historia todavía no conciliada, que
/// queda fuera del ciclo aunque la empresa ya lo tenga encendido.
/// </summary>
public sealed record AccountingCycleStatus(
  int TransaccionId,
  long? CompanyId,
  string? State,
  DateTime Fecha,
  DateTime? PostedAtUtc,
  string? PostedBy,
  int? ReversalOfTransaccionId,
  int? ReversedByTransaccionId,
  string? ReversalReason,
  bool CycleEnabled,
  bool LegacyCompatible,
  bool PeriodClosed)
{
  public bool IsImmutable => AccountingCycleStates.IsImmutable(State);

  /// <summary>El ciclo sólo gobierna esta póliza si la empresa lo tiene encendido y no es historia compatible.</summary>
  public bool CycleApplies => CycleEnabled && !LegacyCompatible;

  public bool CanPost => CycleApplies && !IsImmutable && !PeriodClosed;
  public bool CanReverse => CycleApplies
    && string.Equals(State, AccountingCycleStates.Posted, StringComparison.Ordinal)
    && ReversedByTransaccionId is null;
}

public sealed class AccountingPostRequest
{
  public int TransaccionId { get; set; }
}

public sealed class AccountingReversalRequest
{
  public int TransaccionId { get; set; }
  /// <summary>Obligatorio: una reversa sin motivo no es auditable.</summary>
  public string Reason { get; set; } = string.Empty;
  /// <summary>Fecha del asiento inverso. Si es nula se usa la del original.</summary>
  public DateTime? Fecha { get; set; }
}

public sealed class AccountingReversalResult
{
  public bool Success { get; init; }
  public string? Message { get; init; }
  public int? ReversalTransaccionId { get; init; }

  public static AccountingReversalResult Ok(int reversalId, string message)
    => new() { Success = true, ReversalTransaccionId = reversalId, Message = message };
  public static AccountingReversalResult Fail(string message)
    => new() { Success = false, Message = message };
}

public interface IAccountingCycleService
{
  Task<AccountingCycleStatus?> GetStatusAsync(int transaccionId, CancellationToken ct = default);

  /// <summary>Publicación atómica: periodo abierto, asiento cuadrado, empresa consistente y permiso.</summary>
  Task<TransaccionCommandResult> PostAsync(AccountingPostRequest request, CancellationToken ct = default);

  /// <summary>Reversa ligada al original, que queda intacto. Una segunda reversa no pasa.</summary>
  Task<AccountingReversalResult> ReverseAsync(AccountingReversalRequest request, CancellationToken ct = default);
}
