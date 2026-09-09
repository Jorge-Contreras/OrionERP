using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Contabilidad.Transacciones;

namespace OrionERP.Infrastructure.Features.Contabilidad.Transacciones;

/// <summary>
/// Ciclo contable formal. Se entrega apagado: si la empresa no está activada, ni
/// publicar ni reversar proceden, y la operación existente no cambia.
/// </summary>
public sealed class AccountingCycleService : IAccountingCycleService
{
  private readonly AccountingConnectionFactory _connections;
  private readonly AccountingCycleSessionGuard _guard;
  private readonly ICurrentCompanyContext _company;
  private readonly ILogger<AccountingCycleService> _logger;

  public AccountingCycleService(
    AccountingConnectionFactory connections,
    AccountingCycleSessionGuard guard,
    ICurrentCompanyContext company,
    ILogger<AccountingCycleService> logger)
  {
    _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    _guard = guard ?? throw new ArgumentNullException(nameof(guard));
    _company = company ?? throw new ArgumentNullException(nameof(company));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  public async Task<AccountingCycleStatus?> GetStatusAsync(int transaccionId, CancellationToken ct = default)
  {
    var companyId = await _company.RequireCompanyIdAsync(ct);
    await using var conn = await _connections.OpenAsync(ct);
    if (!await IsInstalledAsync(conn, null, ct)) return null;
    var row = await conn.QuerySingleOrDefaultAsync<StatusRow>(new CommandDefinition(
      AccountingCycleSql.Status(), new { TransaccionId = transaccionId }, cancellationToken: ct));
    if (row is null) return null;
    // Una póliza de otra empresa no se describe: no existe para esta sesión.
    if (row.CompanyId is not null && row.CompanyId != companyId) return null;
    return row.ToStatus();
  }

  public async Task<TransaccionCommandResult> PostAsync(AccountingPostRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var (companyId, _, actor) = await _guard.RequireAsync(ct);

    await using var conn = await _connections.OpenAsync(ct);
    await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      if (!await IsInstalledAsync(conn, tx, ct))
        return await FailAsync(tx, "El ciclo contable no está instalado en esta base de datos.", ct);
      var status = await LockedStatusAsync(conn, tx, request.TransaccionId, ct);
      if (status is null)
        return await FailAsync(tx, "La póliza no existe.", ct);
      if (status.CompanyId != companyId)
        return await FailAsync(tx, "La póliza no pertenece a la empresa de la sesión.", ct);
      if (!status.CycleEnabled)
        return await FailAsync(tx, "El ciclo contable no está activado para esta empresa.", ct);
      if (status.LegacyCompatible)
        return await FailAsync(tx, "La póliza es historia anterior al inicio del ciclo y sigue operando en modo compatible.", ct);
      if (status.IsImmutable)
        return await FailAsync(tx, $"La póliza ya está {status.State}.", ct);
      if (status.PeriodClosed)
        return await FailAsync(tx, "El periodo contable de esta póliza está cerrado.", ct);

      var movements = (await conn.QueryAsync<TransaccionMovimientoUpdateItem>(new CommandDefinition("""
        SELECT ID AS Id, Nivel1, Nivel2, Nivel3, Nombre_Cuenta AS NombreCuenta, Concepto, Debe, Haber
        FROM dbo.Registro_Contable WITH (UPDLOCK, HOLDLOCK)
        WHERE TransaccionID = @TransaccionId;
        """, new { TransaccionId = request.TransaccionId }, tx, cancellationToken: ct))).AsList();
      if (movements.Count == 0)
        return await FailAsync(tx, "Una póliza sin movimientos no se publica.", ct);
      // El mismo validador que ya usa GuardarMovimientosAsync: una sola regla de cuadre.
      var imbalance = MovimientosCuadreValidator.Validate(movements);
      if (imbalance is not null)
        return await FailAsync(tx, imbalance, ct);

      var affected = await conn.ExecuteAsync(new CommandDefinition("""
        UPDATE dbo.Transacciones
        SET CycleState = 'Posted', PostedAtUtc = SYSUTCDATETIME(), PostedBy = @Actor
        WHERE ID = @TransaccionId
          AND CompanyId = @CompanyId
          AND (CycleState IS NULL OR CycleState = 'Draft');
        """, new { request.TransaccionId, CompanyId = companyId, Actor = actor }, tx, cancellationToken: ct));
      if (affected != 1)
        return await FailAsync(tx, "Otra operación publicó esta póliza primero.", ct);

      await tx.CommitAsync(ct);
      return TransaccionCommandResult.Ok($"Póliza {request.TransaccionId} publicada.");
    }
    catch (SqlException ex) when (ex.Number is 51993 or 51994 or 51995 or 51996)
    {
      await RollbackQuietlyAsync(tx, ct);
      return TransaccionCommandResult.Fail(ex.Message);
    }
    catch (Exception ex)
    {
      await RollbackQuietlyAsync(tx, ct);
      _logger.LogError(ex, "No se pudo publicar la póliza {TransaccionId}", request.TransaccionId);
      return TransaccionCommandResult.Fail($"No se pudo publicar la póliza: {ex.Message}");
    }
  }

  public async Task<AccountingReversalResult> ReverseAsync(AccountingReversalRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var reason = request.Reason?.Trim();
    if (string.IsNullOrWhiteSpace(reason))
      return AccountingReversalResult.Fail("La reversa exige un motivo.");
    var (companyId, companyRfc, actor) = await _guard.RequireAsync(ct);

    await using var conn = await _connections.OpenAsync(ct);
    await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      if (!await IsInstalledAsync(conn, tx, ct))
        return await FailReversalAsync(tx, "El ciclo contable no está instalado en esta base de datos.", ct);
      var status = await LockedStatusAsync(conn, tx, request.TransaccionId, ct);
      if (status is null)
        return await FailReversalAsync(tx, "La póliza no existe.", ct);
      if (status.CompanyId != companyId)
        return await FailReversalAsync(tx, "La póliza no pertenece a la empresa de la sesión.", ct);
      if (!status.CycleEnabled)
        return await FailReversalAsync(tx, "El ciclo contable no está activado para esta empresa.", ct);
      if (!string.Equals(status.State, AccountingCycleStates.Posted, StringComparison.Ordinal))
        return await FailReversalAsync(tx, "Sólo una póliza publicada se reversa.", ct);
      if (status.ReversedByTransaccionId is { } existing)
        return await FailReversalAsync(tx, $"La póliza ya fue reversada por la {existing}.", ct);

      var fecha = request.Fecha?.Date ?? status.Fecha;
      var closed = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
        SELECT CONVERT(bit, CASE WHEN EXISTS
        (
          SELECT 1 FROM contabilidad.AccountingPeriod WITH (UPDLOCK, HOLDLOCK)
          WHERE CompanyId = @CompanyId AND PeriodYear = YEAR(@Fecha)
            AND PeriodMonth = MONTH(@Fecha) AND [State] = 'Closed'
        ) THEN 1 ELSE 0 END);
        """, new { CompanyId = companyId, Fecha = fecha }, tx, cancellationToken: ct));
      if (closed)
        return await FailReversalAsync(tx, "El periodo contable de la reversa está cerrado.", ct);

      // El asiento original queda intacto: la reversa es una póliza nueva con los
      // movimientos invertidos. El índice único sobre ReversalOfTransaccionId es lo que
      // impide una segunda reversa, incluso si dos operaciones llegan a la vez.
      var reversalId = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
        INSERT INTO dbo.Transacciones
          (RFC, CompanyId, Fecha, Concepto, Monto, Tipo_Poliza, Forma_Pago, Facturado, Memo, Cuenta,
           CycleState, ReversalOfTransaccionId, ReversalReason)
        SELECT original.RFC, original.CompanyId, @Fecha,
               LEFT(CONCAT(N'REVERSA ', original.ID, N' ', original.Concepto), 1000),
               original.Monto, original.Tipo_Poliza, original.Forma_Pago, original.Facturado,
               LEFT(CONCAT(N'Reversa de la póliza ', original.ID, N'. Motivo: ', @Reason), 4000),
               original.Cuenta, 'Draft', original.ID, @Reason
        FROM dbo.Transacciones AS original
        WHERE original.ID = @TransaccionId;
        SELECT CONVERT(int, SCOPE_IDENTITY());
        """, new { request.TransaccionId, Fecha = fecha, Reason = reason }, tx, cancellationToken: ct));
      if (reversalId <= 0)
        return await FailReversalAsync(tx, "No se pudo crear la póliza de reversa.", ct);

      var lines = await conn.ExecuteAsync(new CommandDefinition("""
        INSERT INTO dbo.Registro_Contable
          (CompanyId, TransaccionID, Nivel1, Nivel2, Nivel3, Nombre_Cuenta, Concepto, Debe, Haber,
           Referencia, CuentaContableID)
        SELECT original.CompanyId, @ReversalId, original.Nivel1, original.Nivel2, original.Nivel3,
               original.Nombre_Cuenta,
               LEFT(CONCAT(N'REVERSA ', ISNULL(original.Concepto, N'')), 200),
               original.Haber, original.Debe, original.Referencia, original.CuentaContableID
        FROM dbo.Registro_Contable AS original
        WHERE original.TransaccionID = @TransaccionId;
        """, new { request.TransaccionId, ReversalId = reversalId }, tx, cancellationToken: ct));
      if (lines == 0)
        return await FailReversalAsync(tx, "La póliza original no tiene movimientos que reversar.", ct);

      var reversalMovements = (await conn.QueryAsync<TransaccionMovimientoUpdateItem>(new CommandDefinition("""
        SELECT ID AS Id, Nivel1, Nivel2, Nivel3, Nombre_Cuenta AS NombreCuenta, Concepto, Debe, Haber
        FROM dbo.Registro_Contable WHERE TransaccionID = @ReversalId;
        """, new { ReversalId = reversalId }, tx, cancellationToken: ct))).AsList();
      var imbalance = MovimientosCuadreValidator.Validate(reversalMovements);
      if (imbalance is not null)
        return await FailReversalAsync(tx, $"La reversa no cuadra: {imbalance}", ct);

      // La reversa nace Draft para que sus propios movimientos puedan insertarse, y se
      // publica al final; el trigger de inmutabilidad no la bloquea porque sólo actúa
      // sobre filas que ya estaban publicadas.
      var posted = await conn.ExecuteAsync(new CommandDefinition("""
        UPDATE dbo.Transacciones
        SET CycleState = 'Posted', PostedAtUtc = SYSUTCDATETIME(), PostedBy = @Actor
        WHERE ID = @ReversalId AND CycleState = 'Draft';
        """, new { ReversalId = reversalId, Actor = actor }, tx, cancellationToken: ct));
      if (posted != 1)
        return await FailReversalAsync(tx, "No se pudo publicar la reversa.", ct);

      var marked = await conn.ExecuteAsync(new CommandDefinition("""
        UPDATE dbo.Transacciones
        SET CycleState = 'Reversed', ReversedAtUtc = SYSUTCDATETIME(), ReversedBy = @Actor
        WHERE ID = @TransaccionId AND CompanyId = @CompanyId AND CycleState = 'Posted';
        """, new { request.TransaccionId, CompanyId = companyId, Actor = actor }, tx, cancellationToken: ct));
      if (marked != 1)
        return await FailReversalAsync(tx, "Otra operación cambió el estado de la póliza original.", ct);

      await tx.CommitAsync(ct);
      _logger.LogInformation(
        "Póliza {TransaccionId} reversada por {Reversal} en {CompanyRfc}",
        request.TransaccionId, reversalId, companyRfc);
      return AccountingReversalResult.Ok(reversalId, $"Se generó la reversa {reversalId}; el asiento original queda intacto.");
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
      await RollbackQuietlyAsync(tx, ct);
      return AccountingReversalResult.Fail("La póliza ya tiene una reversa registrada.");
    }
    catch (SqlException ex) when (ex.Number is 51993 or 51994 or 51995 or 51996)
    {
      await RollbackQuietlyAsync(tx, ct);
      return AccountingReversalResult.Fail(ex.Message);
    }
    catch (Exception ex)
    {
      await RollbackQuietlyAsync(tx, ct);
      _logger.LogError(ex, "No se pudo reversar la póliza {TransaccionId}", request.TransaccionId);
      return AccountingReversalResult.Fail($"No se pudo reversar la póliza: {ex.Message}");
    }
  }

  private static Task<bool> IsInstalledAsync(SqlConnection conn, SqlTransaction? tx, CancellationToken ct)
    => conn.ExecuteScalarAsync<bool>(new CommandDefinition(
        AccountingCycleSql.InstalledSql, transaction: tx, cancellationToken: ct));

  private static async Task<AccountingCycleStatus?> LockedStatusAsync(
    SqlConnection conn, SqlTransaction tx, int transaccionId, CancellationToken ct)
  {
    var row = await conn.QuerySingleOrDefaultAsync<StatusRow>(new CommandDefinition(
      AccountingCycleSql.LockedStatus(), new { TransaccionId = transaccionId }, tx, cancellationToken: ct));
    return row?.ToStatus();
  }

  private static async Task<TransaccionCommandResult> FailAsync(SqlTransaction tx, string message, CancellationToken ct)
  {
    await tx.RollbackAsync(ct);
    return TransaccionCommandResult.Fail(message);
  }

  private static async Task<AccountingReversalResult> FailReversalAsync(SqlTransaction tx, string message, CancellationToken ct)
  {
    await tx.RollbackAsync(ct);
    return AccountingReversalResult.Fail(message);
  }

  private static async Task RollbackQuietlyAsync(SqlTransaction tx, CancellationToken ct)
  {
    try { await tx.RollbackAsync(ct); } catch { /* la transacción ya estaba cerrada */ }
  }

  private sealed class StatusRow
  {
    public int TransaccionId { get; set; }
    public long? CompanyId { get; set; }
    public string? State { get; set; }
    public DateTime Fecha { get; set; }
    public DateTime? PostedAtUtc { get; set; }
    public string? PostedBy { get; set; }
    public int? ReversalOfTransaccionId { get; set; }
    public int? ReversedByTransaccionId { get; set; }
    public string? ReversalReason { get; set; }
    public bool CycleEnabled { get; set; }
    public bool LegacyCompatible { get; set; }
    public bool PeriodClosed { get; set; }

    public AccountingCycleStatus ToStatus() => new(
      TransaccionId, CompanyId, State, Fecha, PostedAtUtc, PostedBy,
      ReversalOfTransaccionId, ReversedByTransaccionId, ReversalReason,
      CycleEnabled, LegacyCompatible, PeriodClosed);
  }
}
