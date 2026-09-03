using System.Data;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Identity;
using OrionERP.Application.Common;
using OrionERP.Application.Features.CapitalHumano.Workforce;

namespace OrionERP.Infrastructure.Features.CapitalHumano.Workforce;

public sealed class KioskAttendanceService : IKioskAttendanceService
{
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly IAttendanceRecorder _recorder;
  private readonly PasswordHasher<object> _passwordHasher = new();

  public KioskAttendanceService(IDbConnectionFactory connectionFactory, IAttendanceRecorder recorder)
  {
    _connectionFactory = connectionFactory;
    _recorder = recorder;
  }

  public async Task<KioskPairResult> PairAsync(string pairingCode, CancellationToken ct = default)
  {
    var normalized = pairingCode?.Trim() ?? string.Empty;
    if (normalized.Length != 8 || normalized.Any(character => !char.IsDigit(character)))
      return Failure("El codigo de vinculacion no es valido.");

    var codeHash = Hash(normalized);
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
    var device = await connection.QuerySingleOrDefaultAsync<DeviceRow>(new CommandDefinition(
      """
      SELECT p.Id PairingId,d.Id DeviceId,d.Rfc,d.SiteId,d.[Name] DeviceName
      FROM rh.KioskPairingCode p WITH (UPDLOCK,HOLDLOCK)
      INNER JOIN rh.KioskDevice d ON d.Id=p.KioskDeviceId
      WHERE p.CodeHash=@CodeHash AND p.UsedAtUtc IS NULL AND p.ExpiresAtUtc>SYSUTCDATETIME();
      """, new { CodeHash = codeHash }, transaction, cancellationToken: ct));
    if (device is null)
    {
      transaction.Rollback();
      return Failure("El codigo expiro o ya fue utilizado.");
    }

    await WorkforceServiceBase.PinRfcScopeAsync(connection, transaction, device.Rfc, ct);
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    await connection.ExecuteAsync(new CommandDefinition(
      """
      UPDATE rh.KioskPairingCode SET UsedAtUtc=SYSUTCDATETIME() WHERE Id=@PairingId;
      UPDATE rh.KioskDevice SET DeviceTokenHash=@TokenHash,IsActive=1,PairedAtUtc=SYSUTCDATETIME(),LastSeenAtUtc=SYSUTCDATETIME() WHERE Id=@DeviceId;
      """, new { device.PairingId, device.DeviceId, TokenHash = Hash(token) }, transaction, cancellationToken: ct));
    await WorkforceServiceBase.WriteAuditAsync(connection, transaction, device.Rfc, null, "KioskDevice", device.DeviceId,
      "PAIRED", device.DeviceName, "Kiosk pairing", ct);
    transaction.Commit();
    return new KioskPairResult
    {
      Success = true,
      Message = "Kiosco vinculado correctamente.",
      DeviceToken = token,
      DeviceName = device.DeviceName
    };
  }

  public async Task<AttendancePunchResult> PunchAsync(string deviceToken, KioskPunchRequest request, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(deviceToken)) return PunchFailure("Este dispositivo no esta vinculado.");
    var badge = request.BadgeToken?.Trim() ?? string.Empty;
    if (badge.Length == 0 || string.IsNullOrWhiteSpace(request.Pin)) return PunchFailure("Gafete y PIN son obligatorios.");

    DeviceRow? device;
    CredentialRow? credential;
    using (var connection = CreateOpenConnection())
    using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
    {
      device = await connection.QuerySingleOrDefaultAsync<DeviceRow>(new CommandDefinition(
        """
        SELECT Id DeviceId,Rfc,SiteId,[Name] DeviceName
        FROM rh.KioskDevice WITH (UPDLOCK,HOLDLOCK)
        WHERE DeviceTokenHash=@TokenHash AND IsActive=1;
        """, new { TokenHash = Hash(deviceToken) }, transaction, cancellationToken: ct));
      if (device is null)
      {
        transaction.Rollback();
        return PunchFailure("Este dispositivo no esta vinculado o fue desactivado.");
      }

      await WorkforceServiceBase.PinRfcScopeAsync(connection, transaction, device.Rfc, ct);
      credential = await connection.QuerySingleOrDefaultAsync<CredentialRow>(new CommandDefinition(
        """
        SELECT Id,EmployeeId,PinHash,FailedAttempts,LockedUntilUtc
        FROM rh.EmployeeKioskCredential WITH (UPDLOCK,HOLDLOCK)
        WHERE Rfc=@Rfc AND BadgeTokenHash=@BadgeHash AND IsActive=1;
        """, new { device.Rfc, BadgeHash = Hash(badge) }, transaction, cancellationToken: ct));
      if (credential is null)
      {
        transaction.Rollback();
        return PunchFailure("Credencial no reconocida.");
      }
      if (credential.LockedUntilUtc is not null && credential.LockedUntilUtc > DateTime.UtcNow)
      {
        transaction.Rollback();
        return PunchFailure("Credencial bloqueada temporalmente. Solicite apoyo a Capital Humano.");
      }

      var verification = _passwordHasher.VerifyHashedPassword(new object(), credential.PinHash, request.Pin);
      if (verification == PasswordVerificationResult.Failed)
      {
        var failures = credential.FailedAttempts + 1;
        await connection.ExecuteAsync(new CommandDefinition(
          "UPDATE rh.EmployeeKioskCredential SET FailedAttempts=@Failures,LockedUntilUtc=CASE WHEN @Failures>=5 THEN DATEADD(minute,15,SYSUTCDATETIME()) ELSE NULL END WHERE Id=@Id;",
          new { credential.Id, Failures = failures }, transaction, cancellationToken: ct));
        transaction.Commit();
        return PunchFailure(failures >= 5 ? "Credencial bloqueada por 15 minutos." : "PIN incorrecto.");
      }

      await connection.ExecuteAsync(new CommandDefinition(
        """
        UPDATE rh.EmployeeKioskCredential SET FailedAttempts=0,LockedUntilUtc=NULL WHERE Id=@CredentialId;
        UPDATE rh.KioskDevice SET LastSeenAtUtc=SYSUTCDATETIME() WHERE Id=@DeviceId;
        """, new { CredentialId = credential.Id, device.DeviceId }, transaction, cancellationToken: ct));
      transaction.Commit();
    }

    return await _recorder.RecordAsync(new AttendanceRecordCommand(
      device.Rfc,
      credential.EmployeeId,
      request.EventType,
      AttendanceSources.Kiosk,
      request.IdempotencyKey,
      request.Location,
      $"Kiosk:{device.DeviceId}",
      device.DeviceId,
      device.SiteId), ct);
  }

  private IDbConnection CreateOpenConnection()
  {
    var connection = _connectionFactory.Create();
    if (connection.State != ConnectionState.Open) connection.Open();
    return connection;
  }

  private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
  private static KioskPairResult Failure(string message) => new() { Message = message };
  private static AttendancePunchResult PunchFailure(string message) => new() { Message = message };

  private sealed class DeviceRow
  {
    public long PairingId { get; set; }
    public int DeviceId { get; set; }
    public string Rfc { get; set; } = string.Empty;
    public int SiteId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
  }
  private sealed class CredentialRow
  {
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public string PinHash { get; set; } = string.Empty;
    public int FailedAttempts { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
  }
}
