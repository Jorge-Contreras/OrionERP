using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrionERP.Application.Common;

namespace OrionERP.Infrastructure.Features.CapitalHumano.Workforce;

public sealed class WorkforceRetentionOptions
{
  public bool AttendanceEnabled { get; set; }
  public int GpsEvidenceRetentionDays { get; set; } = 730;
  public int CalculatedAttendanceRetentionDays { get; set; } = 1825;
  public int MaintenanceIntervalHours { get; set; } = 24;
}

public sealed record WorkforceRetentionResult(int ProtectedLocationsPurged, int CalculatedRecordsEligibleForReview);

public interface IWorkforceMaintenanceService
{
  Task<WorkforceRetentionResult> ApplyRetentionAsync(DateTime asOfUtc, CancellationToken ct = default);
}

public sealed class WorkforceMaintenanceService : IWorkforceMaintenanceService
{
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly WorkforceRetentionOptions _options;

  public WorkforceMaintenanceService(IDbConnectionFactory connectionFactory, IOptions<WorkforceRetentionOptions> options)
  {
    _connectionFactory = connectionFactory;
    _options = options.Value;
  }

  public async Task<WorkforceRetentionResult> ApplyRetentionAsync(DateTime asOfUtc, CancellationToken ct = default)
  {
    var gpsDays = Math.Clamp(_options.GpsEvidenceRetentionDays, 30, 3650);
    var calculatedDays = Math.Clamp(_options.CalculatedAttendanceRetentionDays, 365, 7300);
    using var connection = _connectionFactory.Create();
    connection.Open();
    await WorkforceServiceBase.ClearRfcScopeAsync(connection, null, ct);
    using var transaction = connection.BeginTransaction();
    var purged = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
      """
      DECLARE @Changed TABLE (Rfc varchar(50) NOT NULL);
      UPDATE rh.TimeEvent SET LocationProtected=NULL
        OUTPUT inserted.Rfc INTO @Changed(Rfc)
      WHERE LocationProtected IS NOT NULL AND OccurredAtUtc<@GpsCutoffUtc;
      DECLARE @Purged int=@@ROWCOUNT;
      INSERT INTO rh.AuditEvent(Rfc,EntityType,EntityId,EventType,Detail,CreatedBy)
      SELECT Rfc,'Retention','GPS', 'GPS_EVIDENCE_PURGED',CONCAT(COUNT(1),N' evidencias exactas anonimizadas.'),N'OrionERP maintenance'
      FROM @Changed GROUP BY Rfc;
      SELECT @Purged;
      """, new { GpsCutoffUtc = asOfUtc.AddDays(-gpsDays) }, transaction, cancellationToken: ct));
    var eligible = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
      """
      SELECT COUNT(1) FROM rh.AttendanceDay dayRecord
      WHERE dayRecord.WorkDate<CONVERT(date,@CalculatedCutoffUtc)
        AND NOT EXISTS(SELECT 1 FROM rh.PrenominaSnapshotLine snapshot
          INNER JOIN rh.PrenominaPeriod period ON period.Id=snapshot.PeriodId
          WHERE snapshot.EmployeeId=dayRecord.EmployeeId AND period.Rfc=dayRecord.Rfc
            AND dayRecord.WorkDate BETWEEN period.FromDate AND period.ToDate);
      """, new { CalculatedCutoffUtc = asOfUtc.AddDays(-calculatedDays) }, transaction, cancellationToken: ct));
    transaction.Commit();
    return new WorkforceRetentionResult(purged, eligible);
  }
}

public sealed class WorkforceMaintenanceHostedService : BackgroundService
{
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly WorkforceRetentionOptions _options;
  private readonly ILogger<WorkforceMaintenanceHostedService> _logger;

  public WorkforceMaintenanceHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkforceRetentionOptions> options,
    ILogger<WorkforceMaintenanceHostedService> logger)
  {
    _scopeFactory = scopeFactory;
    _options = options.Value;
    _logger = logger;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    if (!_options.AttendanceEnabled) return;
    var interval = TimeSpan.FromHours(Math.Clamp(_options.MaintenanceIntervalHours, 1, 168));
    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        using var scope = _scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWorkforceMaintenanceService>();
        var result = await service.ApplyRetentionAsync(DateTime.UtcNow, stoppingToken);
        if (result.ProtectedLocationsPurged > 0 || result.CalculatedRecordsEligibleForReview > 0)
          _logger.LogInformation("Workforce retention completed: {Purged} exact-location records purged; {Eligible} calculated records await controlled review.", result.ProtectedLocationsPurged, result.CalculatedRecordsEligibleForReview);
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
      catch (Exception exception)
      {
        _logger.LogError(exception, "Workforce retention maintenance failed.");
      }

      await Task.Delay(interval, stoppingToken);
    }
  }
}
