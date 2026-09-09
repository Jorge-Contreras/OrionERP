using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Contabilidad.Transacciones;

namespace OrionERP.Infrastructure.Features.Contabilidad.Transacciones;

/// <summary>
/// Implementación de la bandeja durable sobre <c>contabilidad.AccountingOutbox</c>.
/// La idempotencia no la decide este código: la decide el índice único sobre
/// (CompanyId, SourceModule, OperationKey).
/// </summary>
public sealed class AccountingOutboxService : IAccountingOutbox
{
  private readonly AccountingConnectionFactory _connections;
  private readonly ICurrentCompanyContext _company;
  private readonly ICurrentUserAccessor? _currentUser;

  public AccountingOutboxService(
    AccountingConnectionFactory connections,
    ICurrentCompanyContext company,
    ICurrentUserAccessor? currentUser = null)
  {
    _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    _company = company ?? throw new ArgumentNullException(nameof(company));
    _currentUser = currentUser;
  }

  public async Task<AccountingOperation> ClaimAsync(
    string sourceModule,
    string operationKey,
    string payload,
    CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(sourceModule)) throw new ArgumentException("Falta el módulo de origen.", nameof(sourceModule));
    if (string.IsNullOrWhiteSpace(operationKey)) throw new ArgumentException("Falta la identidad de la operación.", nameof(operationKey));
    if (string.IsNullOrWhiteSpace(payload)) throw new ArgumentException("Falta el contenido de la operación.", nameof(payload));

    var rfc = _company.RequireRfc();
    var companyId = await _company.RequireCompanyIdAsync(ct);
    var actor = _currentUser is null ? null : await _currentUser.GetUserNameAsync(ct);

    await using var conn = await _connections.OpenAsync(ct);
    await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      // El MERGE bajo HOLDLOCK y el índice único son lo que hace que dos reintentos
      // concurrentes no puedan crear dos operaciones para la misma identidad.
      var row = await conn.QuerySingleAsync<OperationRow>(new CommandDefinition("""
        DECLARE @Claimed TABLE
        (
          Id bigint, CompanyId bigint, Rfc varchar(50), SourceModule varchar(30),
          OperationKey varchar(200), Payload nvarchar(max), [Status] varchar(20),
          TransaccionId int, Attempts int
        );

        MERGE contabilidad.AccountingOutbox WITH (HOLDLOCK) AS target
        USING (SELECT @CompanyId AS CompanyId, @SourceModule AS SourceModule, @OperationKey AS OperationKey) AS source
          ON target.CompanyId = source.CompanyId
         AND target.SourceModule = source.SourceModule
         AND target.OperationKey = source.OperationKey
        WHEN MATCHED AND target.[Status] <> 'Completed' THEN
          UPDATE SET [Status] = 'Claimed', Attempts = target.Attempts + 1,
                     ClaimedAtUtc = SYSUTCDATETIME(), ClaimedBy = @Actor,
                     UpdatedAtUtc = SYSUTCDATETIME()
        WHEN NOT MATCHED THEN
          INSERT (CompanyId, Rfc, SourceModule, OperationKey, Payload, [Status], Attempts, ClaimedAtUtc, ClaimedBy)
          VALUES (@CompanyId, @Rfc, @SourceModule, @OperationKey, @Payload, 'Claimed', 1, SYSUTCDATETIME(), @Actor)
        OUTPUT inserted.Id, inserted.CompanyId, inserted.Rfc, inserted.SourceModule,
               inserted.OperationKey, inserted.Payload, inserted.[Status],
               inserted.TransaccionId, inserted.Attempts
        INTO @Claimed;

        SELECT Id, CompanyId, Rfc, SourceModule, OperationKey, Payload, [Status], TransaccionId, Attempts
        FROM @Claimed
        UNION ALL
        SELECT Id, CompanyId, Rfc, SourceModule, OperationKey, Payload, [Status], TransaccionId, Attempts
        FROM contabilidad.AccountingOutbox
        WHERE CompanyId = @CompanyId AND SourceModule = @SourceModule AND OperationKey = @OperationKey
          AND NOT EXISTS (SELECT 1 FROM @Claimed);
        """,
        new { CompanyId = companyId, Rfc = rfc, SourceModule = sourceModule, OperationKey = operationKey, Payload = payload, Actor = actor },
        tx, cancellationToken: ct));

      await tx.CommitAsync(ct);
      return row.ToOperation();
    }
    catch { await RollbackQuietlyAsync(tx, ct); throw; }
  }

  public Task RecordPolicyAsync(long operationId, int transaccionId, CancellationToken ct = default)
    => ExecuteAsync("""
      UPDATE contabilidad.AccountingOutbox
      SET TransaccionId = @TransaccionId, UpdatedAtUtc = SYSUTCDATETIME()
      WHERE Id = @OperationId AND [Status] <> 'Completed';
      """, new { OperationId = operationId, TransaccionId = transaccionId }, ct);

  public Task CompleteAsync(long operationId, CancellationToken ct = default)
    => ExecuteAsync("""
      UPDATE contabilidad.AccountingOutbox
      SET [Status] = 'Completed', LinkedAtUtc = SYSUTCDATETIME(),
          CompletedAtUtc = SYSUTCDATETIME(), LastError = NULL, UpdatedAtUtc = SYSUTCDATETIME()
      WHERE Id = @OperationId AND TransaccionId IS NOT NULL;
      """, new { OperationId = operationId }, ct);

  public Task FailAsync(long operationId, string reason, CancellationToken ct = default)
    => ExecuteAsync("""
      UPDATE contabilidad.AccountingOutbox
      SET [Status] = 'Pending', LastError = LEFT(@Reason, 2000), UpdatedAtUtc = SYSUTCDATETIME()
      WHERE Id = @OperationId AND [Status] <> 'Completed';
      """, new { OperationId = operationId, Reason = reason ?? "Sin detalle." }, ct);

  public Task ForgetPolicyAsync(long operationId, CancellationToken ct = default)
    => ExecuteAsync("""
      UPDATE contabilidad.AccountingOutbox
      SET TransaccionId = NULL, UpdatedAtUtc = SYSUTCDATETIME()
      WHERE Id = @OperationId AND [Status] <> 'Completed';
      """, new { OperationId = operationId }, ct);

  private async Task ExecuteAsync(string sql, object parameters, CancellationToken ct)
  {
    await using var conn = await _connections.OpenAsync(ct);
    await conn.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
  }

  private static async Task RollbackQuietlyAsync(SqlTransaction tx, CancellationToken ct)
  {
    try { await tx.RollbackAsync(ct); } catch { /* la transacción ya estaba cerrada */ }
  }

  private sealed class OperationRow
  {
    public long Id { get; set; }
    public long CompanyId { get; set; }
    public string Rfc { get; set; } = "";
    public string SourceModule { get; set; } = "";
    public string OperationKey { get; set; } = "";
    public string Payload { get; set; } = "";
    public string Status { get; set; } = "";
    public int? TransaccionId { get; set; }
    public int Attempts { get; set; }

    public AccountingOperation ToOperation()
      => new(Id, CompanyId, Rfc, SourceModule, OperationKey, Payload, Status, TransaccionId, Attempts);
  }
}
