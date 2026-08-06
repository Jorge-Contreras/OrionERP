using System.Data;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.CapitalHumano.Workforce;

namespace OrionERP.Infrastructure.Features.CapitalHumano.Workforce;

public sealed class PrenominaService : WorkforceServiceBase, IPrenominaService
{
  public PrenominaService(IDbConnectionFactory connectionFactory, ICurrentEmployeeAccessor currentEmployeeAccessor)
    : base(connectionFactory, currentEmployeeAccessor) { }

  public async Task<IReadOnlyList<PrenominaPeriodDto>> GetPeriodsAsync(string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin", "CapitalHumanoNomina");
    using var connection = CreateOpenConnection();
    return (await connection.QueryAsync<PrenominaPeriodDto>(new CommandDefinition(
      """
      SELECT p.Id,p.Rfc,p.PayGroupId,pg.[Name] PayGroupName,p.FromDate,p.ToDate,p.[Status],p.Version,
        (SELECT COUNT(1) FROM rh.PrenominaEmployeeApproval a WHERE a.PeriodId=p.Id) EmployeeCount,
        (SELECT COUNT(1) FROM rh.AttendanceException x WHERE x.Rfc=p.Rfc AND x.WorkDate BETWEEN p.FromDate AND p.ToDate AND x.[Status] IN ('PENDING','RETURNED')) PendingExceptions,
        (SELECT COUNT(1) FROM rh.PrenominaEmployeeApproval a WHERE a.PeriodId=p.Id AND a.[Status]<>'APPROVED') PendingApprovals,
        p.LockedAtUtc
      FROM rh.PrenominaPeriod p INNER JOIN rh.PayGroup pg ON pg.Id=p.PayGroupId
      WHERE p.Rfc=@Rfc ORDER BY p.FromDate DESC,p.Version DESC;
      """, new { Rfc = normalizedRfc }, cancellationToken: ct))).AsList();
  }

  public async Task<IReadOnlyList<PrenominaEmployeeApprovalDto>> GetPendingApprovalsAsync(string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin", "CapitalHumanoSupervisor", "CapitalHumanoNomina");
    var elevated = actor.IsInRole("CapitalHumanoAdmin", "CapitalHumanoNomina");
    if (!elevated && !actor.EmployeeId.HasValue)
      throw new UnauthorizedAccessException("El supervisor debe estar ligado a un empleado para consultar su equipo.");
    using var connection = CreateOpenConnection();
    return (await connection.QueryAsync<PrenominaEmployeeApprovalDto>(new CommandDefinition(
      """
      SELECT approval.PeriodId,approval.EmployeeId,
        COALESCE(NULLIF(employee.NombreCorto,''),CONCAT(employee.Nombre,' ',employee.ApellidoPaterno)) EmployeeName,
        payGroup.[Name] PayGroupName,period.FromDate,period.ToDate,approval.[Status]
      FROM rh.PrenominaEmployeeApproval approval
      INNER JOIN rh.PrenominaPeriod period ON period.Id=approval.PeriodId
      INNER JOIN rh.PayGroup payGroup ON payGroup.Id=period.PayGroupId
      INNER JOIN dbo.Capital_Humano employee ON employee.ID=approval.EmployeeId
      WHERE period.Rfc=@Rfc AND period.[Status] IN ('OPEN','READY') AND approval.[Status]<>'APPROVED'
        AND (@SupervisorId IS NULL OR EXISTS(SELECT 1 FROM rh.SupervisorAssignment scope
          WHERE scope.Rfc=period.Rfc AND scope.EmployeeId=approval.EmployeeId AND scope.SupervisorEmployeeId=@SupervisorId
            AND scope.EffectiveFrom<=period.ToDate AND (scope.EffectiveTo IS NULL OR scope.EffectiveTo>=period.FromDate)))
      ORDER BY period.ToDate,EmployeeName;
      """, new { Rfc = normalizedRfc, SupervisorId = elevated ? (int?)null : actor.EmployeeId }, cancellationToken: ct))).AsList();
  }

  public async Task<IReadOnlyList<PrenominaLineDto>> GetLinesAsync(long periodId, string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin", "CapitalHumanoNomina");
    using var connection = CreateOpenConnection();
    var period = await GetPeriodAsync(connection, null, periodId, normalizedRfc, false, ct)
      ?? throw new KeyNotFoundException("El periodo no existe.");
    var snapshotCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
      "SELECT COUNT(1) FROM rh.PrenominaSnapshotLine WHERE PeriodId=@PeriodId;", new { PeriodId = periodId }, cancellationToken: ct));
    if (snapshotCount > 0)
    {
      return (await connection.QueryAsync<PrenominaLineDto>(new CommandDefinition(
        """
        SELECT EmployeeId,EmployeeName,ScheduledMinutes,WorkedMinutes,OvertimeApprovedMinutes,
          PaidLeaveDays,UnpaidLeaveDays,ExceptionCount
        FROM rh.PrenominaSnapshotLine WHERE PeriodId=@PeriodId ORDER BY EmployeeName,EmployeeId;
        """, new { PeriodId = periodId }, cancellationToken: ct))).AsList();
    }
    return await QueryLiveLinesAsync(connection, null, period, ct);
  }

  public async Task<WorkforceCommandResult> CreatePeriodAsync(PrenominaPeriodCreateRequest request, CancellationToken ct = default)
  {
    var rfc = NormalizeRfc(request.Rfc);
    var actor = await RequireActorAsync(rfc, false, ct, "CapitalHumanoAdmin", "CapitalHumanoNomina");
    if (request.ToDate < request.FromDate) return WorkforceCommandResult.Fail("El rango del periodo no es valido.");
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
    try
    {
      var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
        """
        IF NOT EXISTS (SELECT 1 FROM rh.PayGroup WHERE Id=@PayGroupId AND Rfc=@Rfc AND IsActive=1)
          THROW 51000,'El grupo de pago no pertenece al RFC.',1;
        INSERT INTO rh.PrenominaPeriod (Rfc,PayGroupId,FromDate,ToDate,CreatedBy)
        VALUES (@Rfc,@PayGroupId,@FromDate,@ToDate,@Actor);
        DECLARE @Id bigint=CAST(SCOPE_IDENTITY() AS bigint);
        INSERT INTO rh.PrenominaEmployeeApproval (PeriodId,EmployeeId)
        SELECT DISTINCT @Id,wa.EmployeeId FROM rh.EmployeeWorkAssignment wa
        WHERE wa.Rfc=@Rfc AND wa.PayGroupId=@PayGroupId AND wa.EffectiveFrom<=@ToDate
          AND (wa.EffectiveTo IS NULL OR wa.EffectiveTo>=@FromDate);

        ;WITH Calendar AS
        (
          SELECT @FromDate WorkDate
          UNION ALL SELECT DATEADD(day,1,WorkDate) FROM Calendar WHERE WorkDate<@ToDate
        ), Scheduled AS
        (
          SELECT a.EmployeeId,c.WorkDate,wa.SiteId,wa.ScheduleTemplateId,wa.AttendancePolicyId,
            CASE WHEN sd.StartTime IS NULL OR sd.EndTime IS NULL THEN 0 ELSE
              DATEDIFF(minute,CAST(sd.StartTime AS datetime2),
                CASE WHEN sd.EndTime<=sd.StartTime THEN DATEADD(day,1,CAST(sd.EndTime AS datetime2)) ELSE CAST(sd.EndTime AS datetime2) END)
              - sd.UnpaidBreakMinutes END ScheduledMinutes,
            CAST(CASE WHEN EXISTS(SELECT 1 FROM rh.Holiday holiday WHERE holiday.Rfc=@Rfc
              AND holiday.HolidayDate=c.WorkDate AND (holiday.SiteId IS NULL OR holiday.SiteId=wa.SiteId)) THEN 1
              ELSE ISNULL((SELECT SUM(l.RequestedDays/NULLIF(DATEDIFF(day,l.StartDate,l.EndDate)+1,0))
                FROM rh.LeaveRequest l WHERE l.Rfc=@Rfc AND l.EmployeeId=a.EmployeeId
                  AND l.[Status]='APPROVED' AND c.WorkDate BETWEEN l.StartDate AND l.EndDate),0) END AS decimal(8,4)) ExcusedFraction
          FROM rh.PrenominaEmployeeApproval a
          CROSS JOIN Calendar c
          INNER JOIN rh.EmployeeWorkAssignment wa ON wa.Rfc=@Rfc AND wa.EmployeeId=a.EmployeeId AND wa.PayGroupId=@PayGroupId
            AND wa.EffectiveFrom<=c.WorkDate AND (wa.EffectiveTo IS NULL OR wa.EffectiveTo>=c.WorkDate)
          INNER JOIN rh.ScheduleDay sd ON sd.ScheduleTemplateId=wa.ScheduleTemplateId
            AND sd.DayOfWeek=((DATEDIFF(day,'19000107',c.WorkDate)%7)+7)%7 AND sd.IsWorkingDay=1
          WHERE a.PeriodId=@Id
        )
        INSERT INTO rh.AttendanceDay
          (Rfc,EmployeeId,WorkDate,SiteId,ScheduleTemplateId,AttendancePolicyId,ScheduledMinutes,
           WorkedMinutes,BreakMinutes,AbsenceMinutes,LateMinutes,EarlyDepartureMinutes,OvertimeCandidateMinutes,[Status],HasExceptions)
        SELECT @Rfc,s.EmployeeId,s.WorkDate,s.SiteId,s.ScheduleTemplateId,s.AttendancePolicyId,s.ScheduledMinutes,
          0,0,CASE WHEN s.ExcusedFraction>=1 THEN 0 ELSE s.ScheduledMinutes-CONVERT(int,ROUND(s.ScheduledMinutes*s.ExcusedFraction,0)) END,0,0,0,
          CASE WHEN s.ExcusedFraction>=1 THEN 'READY' ELSE 'EXCEPTION' END,CASE WHEN s.ExcusedFraction>=1 THEN 0 ELSE 1 END
        FROM Scheduled s
        WHERE NOT EXISTS(SELECT 1 FROM rh.AttendanceDay d WHERE d.Rfc=@Rfc AND d.EmployeeId=s.EmployeeId AND d.WorkDate=s.WorkDate)
          AND NOT EXISTS(SELECT 1 FROM rh.TimeEvent e WHERE e.Rfc=@Rfc AND e.EmployeeId=s.EmployeeId AND e.WorkDate=s.WorkDate)
        OPTION (MAXRECURSION 370);

        INSERT INTO rh.AttendanceException (Rfc,EmployeeId,WorkDate,AttendanceDayId,ExceptionType,Detail)
        SELECT @Rfc,d.EmployeeId,d.WorkDate,d.Id,'ABSENCE',N'No se recibieron registros para una jornada programada.'
        FROM rh.AttendanceDay d
        WHERE d.Rfc=@Rfc AND d.WorkDate BETWEEN @FromDate AND @ToDate AND d.AbsenceMinutes>0
          AND EXISTS(SELECT 1 FROM rh.PrenominaEmployeeApproval a WHERE a.PeriodId=@Id AND a.EmployeeId=d.EmployeeId)
          AND NOT EXISTS(SELECT 1 FROM rh.AttendanceException x WHERE x.AttendanceDayId=d.Id AND x.ExceptionType='ABSENCE');
        SELECT @Id;
        """, new { Rfc = rfc, request.PayGroupId, request.FromDate, request.ToDate, Actor = actor.UserName }, transaction, cancellationToken: ct));
      await WriteAuditAsync(connection, transaction, rfc, null, "PrenominaPeriod", id, "CREATED", $"{request.FromDate:yyyy-MM-dd}/{request.ToDate:yyyy-MM-dd}", actor.UserName, ct);
      transaction.Commit();
      return WorkforceCommandResult.Ok("Periodo de pre-nomina creado.", id);
    }
    catch
    {
      transaction.Rollback();
      throw;
    }
  }

  public async Task<PrenominaValidationDto> ValidateAsync(long periodId, string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin", "CapitalHumanoNomina");
    using var connection = CreateOpenConnection();
    var period = await GetPeriodAsync(connection, null, periodId, normalizedRfc, false, ct);
    if (period is null) return new PrenominaValidationDto { Errors = ["El periodo no existe."] };
    return await ValidateCoreAsync(connection, null, period, null, ct);
  }

  public async Task<WorkforceCommandResult> ApproveEmployeeAsync(long periodId, int employeeId, string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin", "CapitalHumanoSupervisor");
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
    var period = await GetPeriodAsync(connection, transaction, periodId, normalizedRfc, true, ct);
    if (period is null || period.Status is not (PrenominaStatuses.Open or PrenominaStatuses.Ready))
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail("El periodo no esta abierto.");
    }
    if (!await CanManageEmployeeAsync(connection, transaction, actor, normalizedRfc, employeeId, period.ToDate, ct))
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail("El empleado no pertenece a su equipo.");
    }
    var validation = await ValidateCoreAsync(connection, transaction, period, employeeId, ct);
    if (!validation.CanLock)
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail(string.Join(" ", validation.Errors));
    }
    var changed = await connection.ExecuteAsync(new CommandDefinition(
      "UPDATE rh.PrenominaEmployeeApproval SET [Status]='APPROVED',ApprovedAtUtc=SYSUTCDATETIME(),ApprovedBy=@Actor WHERE PeriodId=@PeriodId AND EmployeeId=@EmployeeId;",
      new { PeriodId = periodId, EmployeeId = employeeId, Actor = actor.UserName }, transaction, cancellationToken: ct));
    await WriteAuditAsync(connection, transaction, normalizedRfc, employeeId, "PrenominaApproval", periodId, "APPROVED", null, actor.UserName, ct);
    transaction.Commit();
    return changed > 0 ? WorkforceCommandResult.Ok("Asistencia del empleado aprobada.", periodId) : WorkforceCommandResult.Fail("El empleado no pertenece al periodo.");
  }

  public async Task<WorkforceCommandResult> LockAsync(long periodId, string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoNomina");
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
    var period = await GetPeriodAsync(connection, transaction, periodId, normalizedRfc, true, ct);
    if (period is null || period.Status is not (PrenominaStatuses.Open or PrenominaStatuses.Ready))
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail("El periodo no esta disponible para cierre.");
    }
    var validation = await ValidateCoreAsync(connection, transaction, period, null, ct);
    await SaveValidationAsync(connection, transaction, periodId, validation, actor.UserName, ct);
    if (!validation.CanLock)
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail(string.Join(" ", validation.Errors));
    }
    var lines = await QueryLiveLinesAsync(connection, transaction, period, ct);
    foreach (var line in lines)
    {
      await connection.ExecuteAsync(new CommandDefinition(
        """
        INSERT INTO rh.PrenominaSnapshotLine
          (PeriodId,EmployeeId,EmployeeName,ScheduledMinutes,WorkedMinutes,OvertimeApprovedMinutes,PaidLeaveDays,UnpaidLeaveDays,ExceptionCount)
        VALUES (@PeriodId,@EmployeeId,@EmployeeName,@ScheduledMinutes,@WorkedMinutes,@OvertimeApprovedMinutes,@PaidLeaveDays,@UnpaidLeaveDays,@ExceptionCount);
        """, new { PeriodId = periodId, line.EmployeeId, line.EmployeeName, line.ScheduledMinutes, line.WorkedMinutes, line.OvertimeApprovedMinutes, line.PaidLeaveDays, line.UnpaidLeaveDays, line.ExceptionCount }, transaction, cancellationToken: ct));
    }
    await connection.ExecuteAsync(new CommandDefinition(
      "UPDATE rh.PrenominaPeriod SET [Status]='LOCKED',LockedAtUtc=SYSUTCDATETIME(),LockedBy=@Actor WHERE Id=@Id;",
      new { Id = periodId, Actor = actor.UserName }, transaction, cancellationToken: ct));
    await WriteAuditAsync(connection, transaction, normalizedRfc, null, "PrenominaPeriod", periodId, "LOCKED", $"Snapshot de {lines.Count} empleados", actor.UserName, ct);
    transaction.Commit();
    return WorkforceCommandResult.Ok("Periodo bloqueado y snapshot inmutable creado.", periodId);
  }

  public async Task<WorkforceCommandResult> ReopenAsync(long periodId, string rfc, string reason, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoNomina");
    if (string.IsNullOrWhiteSpace(reason)) return WorkforceCommandResult.Fail("La razon de reapertura es obligatoria.");
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
    var period = await GetPeriodAsync(connection, transaction, periodId, normalizedRfc, true, ct);
    if (period is null || period.Status is not (PrenominaStatuses.Locked or PrenominaStatuses.Exported))
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail("Solo se puede reabrir un periodo bloqueado o exportado.");
    }
    var newId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
      """
      UPDATE rh.PrenominaPeriod SET [Status]='REOPENED',ReopenReason=@Reason WHERE Id=@Id;
      INSERT INTO rh.PrenominaPeriod (Rfc,PayGroupId,FromDate,ToDate,[Status],Version,ParentPeriodId,ReopenReason,CreatedBy)
      VALUES (@Rfc,@PayGroupId,@FromDate,@ToDate,'OPEN',@Version+1,@Id,@Reason,@Actor);
      DECLARE @NewId bigint=CAST(SCOPE_IDENTITY() AS bigint);
      INSERT INTO rh.PrenominaEmployeeApproval (PeriodId,EmployeeId)
      SELECT @NewId,EmployeeId FROM rh.PrenominaEmployeeApproval WHERE PeriodId=@Id;
      SELECT @NewId;
      """, new { Id = periodId, period.Rfc, period.PayGroupId, period.FromDate, period.ToDate, period.Version, Reason = reason.Trim(), Actor = actor.UserName }, transaction, cancellationToken: ct));
    await WriteAuditAsync(connection, transaction, normalizedRfc, null, "PrenominaPeriod", newId, "REOPENED_VERSION", $"Origen {periodId}; {reason}", actor.UserName, ct);
    transaction.Commit();
    return WorkforceCommandResult.Ok($"Nueva version {period.Version + 1} abierta; el snapshot anterior permanece intacto.", newId);
  }

  private static async Task<PrenominaValidationDto> ValidateCoreAsync(IDbConnection connection, IDbTransaction? transaction, PeriodRow period, int? employeeId, CancellationToken ct)
  {
    var args = new { period.Rfc, period.Id, period.PayGroupId, period.FromDate, period.ToDate, EmployeeId = employeeId };
    var issues = await connection.QuerySingleAsync<ValidationCounts>(new CommandDefinition(
      """
      SELECT
        (SELECT COUNT(1) FROM rh.PrenominaEmployeeApproval a WHERE a.PeriodId=@Id AND (@EmployeeId IS NULL OR a.EmployeeId=@EmployeeId)) Employees,
        (SELECT COUNT(1) FROM rh.PrenominaEmployeeApproval a WHERE a.PeriodId=@Id AND a.[Status]<>'APPROVED' AND @EmployeeId IS NULL) PendingApprovals,
        (SELECT COUNT(1) FROM rh.AttendanceException x WHERE x.Rfc=@Rfc AND x.WorkDate BETWEEN @FromDate AND @ToDate AND x.[Status] IN ('PENDING','RETURNED') AND (@EmployeeId IS NULL OR x.EmployeeId=@EmployeeId)
          AND EXISTS(SELECT 1 FROM rh.PrenominaEmployeeApproval a WHERE a.PeriodId=@Id AND a.EmployeeId=x.EmployeeId)) PendingExceptions,
        (SELECT COUNT(1) FROM rh.AttendanceDay d WHERE d.Rfc=@Rfc AND d.WorkDate BETWEEN @FromDate AND @ToDate AND d.HasExceptions=1 AND (@EmployeeId IS NULL OR d.EmployeeId=@EmployeeId)
          AND EXISTS(SELECT 1 FROM rh.PrenominaEmployeeApproval a WHERE a.PeriodId=@Id AND a.EmployeeId=d.EmployeeId)) UnreconciledDays,
        (SELECT COUNT(1) FROM rh.LeaveRequest l WHERE l.Rfc=@Rfc AND l.[Status]='PENDING' AND l.StartDate<=@ToDate AND l.EndDate>=@FromDate AND (@EmployeeId IS NULL OR l.EmployeeId=@EmployeeId)
          AND EXISTS(SELECT 1 FROM rh.PrenominaEmployeeApproval a WHERE a.PeriodId=@Id AND a.EmployeeId=l.EmployeeId)) PendingLeave,
        (SELECT COUNT(1) FROM rh.AttendanceCorrectionRequest correction WHERE correction.Rfc=@Rfc
          AND correction.[Status] IN ('PENDING','RETURNED') AND CAST(correction.RequestedAtUtc AS date) BETWEEN @FromDate AND @ToDate
          AND (@EmployeeId IS NULL OR correction.EmployeeId=@EmployeeId)
          AND EXISTS(SELECT 1 FROM rh.PrenominaEmployeeApproval a WHERE a.PeriodId=@Id AND a.EmployeeId=correction.EmployeeId)) PendingCorrections,
        (SELECT COUNT(1) FROM rh.AttendanceDay d WHERE d.Rfc=@Rfc AND d.WorkDate BETWEEN @FromDate AND @ToDate
          AND d.OvertimeCandidateMinutes>0 AND (@EmployeeId IS NULL OR d.EmployeeId=@EmployeeId)
          AND EXISTS(SELECT 1 FROM rh.PrenominaEmployeeApproval a WHERE a.PeriodId=@Id AND a.EmployeeId=d.EmployeeId)
          AND NOT EXISTS(SELECT 1 FROM rh.OvertimeDecision decision WHERE decision.AttendanceDayId=d.Id)) PendingOvertime,
        (SELECT COUNT(1) FROM rh.PrenominaEmployeeApproval a WHERE a.PeriodId=@Id AND (@EmployeeId IS NULL OR a.EmployeeId=@EmployeeId)
          AND NOT EXISTS(SELECT 1 FROM rh.EmployeeWorkAssignment wa WHERE wa.Rfc=@Rfc AND wa.EmployeeId=a.EmployeeId AND wa.PayGroupId=@PayGroupId AND wa.EffectiveFrom<=@ToDate AND (wa.EffectiveTo IS NULL OR wa.EffectiveTo>=@FromDate))) MissingAssignments;
      """, args, transaction, cancellationToken: ct));
    var errors = new List<string>();
    var warnings = new List<string>();
    if (issues.Employees == 0) errors.Add("El periodo no contiene empleados.");
    if (issues.MissingAssignments > 0) errors.Add($"{issues.MissingAssignments} empleados no tienen una asignacion valida.");
    if (issues.PendingExceptions > 0) errors.Add($"Hay {issues.PendingExceptions} excepciones pendientes.");
    if (issues.UnreconciledDays > 0) errors.Add($"Hay {issues.UnreconciledDays} dias sin conciliar.");
    if (issues.PendingLeave > 0) errors.Add($"Hay {issues.PendingLeave} solicitudes de ausencia pendientes.");
    if (issues.PendingCorrections > 0) errors.Add($"Hay {issues.PendingCorrections} correcciones pendientes o devueltas.");
    if (issues.PendingOvertime > 0) errors.Add($"Hay {issues.PendingOvertime} dias con tiempo extra sin decision.");
    if (issues.PendingApprovals > 0) errors.Add($"Faltan {issues.PendingApprovals} aprobaciones de supervisor.");
    return new PrenominaValidationDto { CanLock = errors.Count == 0, Errors = errors, Warnings = warnings };
  }

  private static async Task SaveValidationAsync(IDbConnection connection, IDbTransaction transaction, long periodId, PrenominaValidationDto validation, string actor, CancellationToken ct)
  {
    await connection.ExecuteAsync(new CommandDefinition(
      """
      INSERT INTO rh.PrenominaValidationResult (PeriodId,IsValid,ErrorsJson,WarningsJson,ValidatedBy)
      VALUES (@PeriodId,@IsValid,@Errors,@Warnings,@Actor);
      """, new { PeriodId = periodId, IsValid = validation.CanLock, Errors = System.Text.Json.JsonSerializer.Serialize(validation.Errors), Warnings = System.Text.Json.JsonSerializer.Serialize(validation.Warnings), Actor = actor }, transaction, cancellationToken: ct));
  }

  internal static async Task<IReadOnlyList<PrenominaLineDto>> QueryLiveLinesAsync(IDbConnection connection, IDbTransaction? transaction, PeriodRow period, CancellationToken ct)
    => (await connection.QueryAsync<PrenominaLineDto>(new CommandDefinition(
      """
      SELECT a.EmployeeId,COALESCE(NULLIF(ch.NombreCorto,''),CONCAT(ch.Nombre,' ',ch.ApellidoPaterno)) EmployeeName,
        ISNULL((SELECT SUM(d.ScheduledMinutes) FROM rh.AttendanceDay d WHERE d.Rfc=@Rfc AND d.EmployeeId=a.EmployeeId AND d.WorkDate BETWEEN @FromDate AND @ToDate),0) ScheduledMinutes,
        ISNULL((SELECT SUM(d.WorkedMinutes) FROM rh.AttendanceDay d WHERE d.Rfc=@Rfc AND d.EmployeeId=a.EmployeeId AND d.WorkDate BETWEEN @FromDate AND @ToDate),0) WorkedMinutes,
        ISNULL((SELECT SUM(d.OvertimeApprovedMinutes) FROM rh.AttendanceDay d WHERE d.Rfc=@Rfc AND d.EmployeeId=a.EmployeeId AND d.WorkDate BETWEEN @FromDate AND @ToDate),0) OvertimeApprovedMinutes,
        ISNULL((SELECT SUM(l.RequestedDays) FROM rh.LeaveRequest l INNER JOIN rh.LeaveType t ON t.Id=l.LeaveTypeId WHERE l.Rfc=@Rfc AND l.EmployeeId=a.EmployeeId AND l.[Status]='APPROVED' AND t.IsPaid=1 AND l.StartDate<=@ToDate AND l.EndDate>=@FromDate),0) PaidLeaveDays,
        ISNULL((SELECT SUM(l.RequestedDays) FROM rh.LeaveRequest l INNER JOIN rh.LeaveType t ON t.Id=l.LeaveTypeId WHERE l.Rfc=@Rfc AND l.EmployeeId=a.EmployeeId AND l.[Status]='APPROVED' AND t.IsPaid=0 AND l.StartDate<=@ToDate AND l.EndDate>=@FromDate),0) UnpaidLeaveDays,
        ISNULL((SELECT COUNT(1) FROM rh.AttendanceException x WHERE x.Rfc=@Rfc AND x.EmployeeId=a.EmployeeId AND x.WorkDate BETWEEN @FromDate AND @ToDate),0) ExceptionCount
      FROM rh.PrenominaEmployeeApproval a INNER JOIN dbo.Capital_Humano ch ON ch.ID=a.EmployeeId
      WHERE a.PeriodId=@Id ORDER BY EmployeeName,a.EmployeeId;
      """, period, transaction, cancellationToken: ct))).AsList();

  internal static Task<PeriodRow?> GetPeriodAsync(IDbConnection connection, IDbTransaction? transaction, long periodId, string rfc, bool forUpdate, CancellationToken ct)
    => connection.QuerySingleOrDefaultAsync<PeriodRow>(new CommandDefinition(
      $"SELECT Id,Rfc,PayGroupId,FromDate,ToDate,[Status],Version FROM rh.PrenominaPeriod{(forUpdate ? " WITH(UPDLOCK,HOLDLOCK)" : string.Empty)} WHERE Id=@Id AND Rfc=@Rfc;",
      new { Id = periodId, Rfc = rfc }, transaction, cancellationToken: ct));

  internal sealed class PeriodRow
  {
    public long Id { get; set; }
    public string Rfc { get; set; } = string.Empty;
    public int PayGroupId { get; set; }
    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public int Version { get; set; }
  }
  private sealed class ValidationCounts { public int Employees { get; set; } public int PendingApprovals { get; set; } public int PendingExceptions { get; set; } public int UnreconciledDays { get; set; } public int PendingLeave { get; set; } public int PendingCorrections { get; set; } public int PendingOvertime { get; set; } public int MissingAssignments { get; set; } }
}
