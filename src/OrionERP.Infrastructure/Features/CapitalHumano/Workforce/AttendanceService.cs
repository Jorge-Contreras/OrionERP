using System.Data;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.CapitalHumano.Workforce;

namespace OrionERP.Infrastructure.Features.CapitalHumano.Workforce;

public sealed record AttendanceRecordCommand(
  string Rfc,
  int EmployeeId,
  string EventType,
  string Source,
  string IdempotencyKey,
  LocationEvidenceDto Location,
  string Actor,
  int? KioskDeviceId = null,
  int? RequiredSiteId = null,
  DateTime? OccurredAtUtc = null,
  bool IsAdjustment = false,
  string? Reason = null);

public interface IAttendanceRecorder
{
  Task<AttendancePunchResult> RecordAsync(AttendanceRecordCommand command, CancellationToken ct = default);
  Task RecalculateAsync(IDbConnection connection, IDbTransaction transaction, string rfc, int employeeId, DateOnly workDate, CancellationToken ct);
}

public sealed class AttendanceRecorder : IAttendanceRecorder
{
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly IGpsLocationProtector _gpsProtector;

  public AttendanceRecorder(IDbConnectionFactory connectionFactory, IGpsLocationProtector gpsProtector)
  {
    _connectionFactory = connectionFactory;
    _gpsProtector = gpsProtector;
  }

  public async Task<AttendancePunchResult> RecordAsync(AttendanceRecordCommand command, CancellationToken ct = default)
  {
    var eventType = command.EventType.Trim().ToUpperInvariant();
    if (!AttendanceEventTypes.All.Contains(eventType))
      return Failure("El tipo de registro no es valido.");
    if (string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Length > 100)
      return Failure("La clave de idempotencia no es valida.");

    using var connection = _connectionFactory.Create();
    if (connection.State != ConnectionState.Open) connection.Open();
    await WorkforceServiceBase.PinRfcScopeAsync(connection, null, command.Rfc, ct);
    using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
    try
    {
      var nowUtc = command.OccurredAtUtc ?? DateTime.UtcNow;
      var assignment = await connection.QuerySingleOrDefaultAsync<AssignmentRow>(new CommandDefinition(
        AssignmentSql,
        new { command.Rfc, command.EmployeeId, EffectiveDate = DateOnly.FromDateTime(nowUtc) },
        transaction,
        cancellationToken: ct));
      if (assignment is null)
      {
        transaction.Rollback();
        return Failure("El empleado no tiene una asignacion de trabajo vigente.");
      }
      if (command.RequiredSiteId.HasValue && assignment.SiteId != command.RequiredSiteId.Value)
      {
        transaction.Rollback();
        return Failure("Este kiosco no pertenece al sitio asignado al empleado.");
      }

      var zone = ResolveTimeZone(assignment.TimeZoneId);
      var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone);
      var workDate = DateOnly.FromDateTime(localNow);

      var openEvent = await connection.QuerySingleOrDefaultAsync<EventStateRow>(new CommandDefinition(
        """
        SELECT TOP (1) EventType, WorkDate
        FROM rh.TimeEvent WITH (UPDLOCK, HOLDLOCK)
        WHERE Rfc=@Rfc AND EmployeeId=@EmployeeId AND OccurredAtUtc BETWEEN DATEADD(hour, -30, @NowUtc) AND @NowUtc
        ORDER BY OccurredAtUtc DESC, Id DESC;
        """,
        new { command.Rfc, command.EmployeeId, NowUtc = nowUtc }, transaction, cancellationToken: ct));
      if (openEvent is not null && !string.Equals(openEvent.EventType, AttendanceEventTypes.Out, StringComparison.OrdinalIgnoreCase))
        workDate = openEvent.WorkDate;

      var existing = await connection.QuerySingleOrDefaultAsync<ExistingEventRow>(new CommandDefinition(
        """
        SELECT Id, EventType, LocationStatus, DistanceMeters
        FROM rh.TimeEvent WITH (UPDLOCK, HOLDLOCK)
        WHERE Rfc=@Rfc AND EmployeeId=@EmployeeId AND IdempotencyKey=@IdempotencyKey;
        """,
        command,
        transaction,
        cancellationToken: ct));
      if (existing is not null)
      {
        transaction.Commit();
        return new AttendancePunchResult
        {
          Success = true,
          Message = "El registro ya habia sido recibido.",
          EventId = existing.Id,
          NextEventType = AttendanceTransitionRules.GetNextEventType(existing.EventType),
          LocationStatus = existing.LocationStatus,
          DistanceMeters = existing.DistanceMeters
        };
      }

      var lastType = openEvent?.EventType;
      if (!AttendanceTransitionRules.IsAllowed(lastType, eventType))
      {
        transaction.Rollback();
        return Failure($"La secuencia no permite {eventType}. El siguiente registro esperado es {AttendanceTransitionRules.GetNextEventType(lastType)}.");
      }

      var site = new WorkSiteDto
      {
        Id = assignment.SiteId,
        Rfc = command.Rfc,
        Name = assignment.SiteName,
        Latitude = assignment.Latitude,
        Longitude = assignment.Longitude,
        RadiusMeters = assignment.RadiusMeters,
        MaxAccuracyMeters = assignment.MaxAccuracyMeters
      };
      var location = GeofenceEvaluator.Evaluate(site, command.Location, assignment.LocationRequired && !command.IsAdjustment);
      var protectedLocation = _gpsProtector.Protect(command.Location);
      var eventId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
        """
        INSERT INTO rh.TimeEvent
          (Rfc, EmployeeId, SiteId, WorkDate, EventType, [Source], OccurredAtUtc,
           ClientCapturedAtUtc, IdempotencyKey, LocationProtected, LocationStatus,
           DistanceMeters, AccuracyMeters, KioskDeviceId, IsAdjustment, Reason, CreatedBy)
        VALUES
          (@Rfc, @EmployeeId, @SiteId, @WorkDate, @EventType, @Source, @OccurredAtUtc,
           @ClientCapturedAtUtc, @IdempotencyKey, @LocationProtected, @LocationStatus,
           @DistanceMeters, @AccuracyMeters, @KioskDeviceId, @IsAdjustment, @Reason, @Actor);
        SELECT CAST(SCOPE_IDENTITY() AS bigint);
        """,
        new
        {
          command.Rfc,
          command.EmployeeId,
          assignment.SiteId,
          WorkDate = workDate,
          EventType = eventType,
          command.Source,
          OccurredAtUtc = nowUtc,
          ClientCapturedAtUtc = command.Location.CapturedAt?.UtcDateTime,
          command.IdempotencyKey,
          LocationProtected = protectedLocation,
          LocationStatus = location.Status,
          location.DistanceMeters,
          command.Location.AccuracyMeters,
          command.KioskDeviceId,
          command.IsAdjustment,
          command.Reason,
          command.Actor
        }, transaction, cancellationToken: ct));

      if (location.RequiresReview)
      {
        await connection.ExecuteAsync(new CommandDefinition(
          """
          INSERT INTO rh.AttendanceException
            (Rfc, EmployeeId, WorkDate, TimeEventId, ExceptionType, Detail)
          VALUES (@Rfc, @EmployeeId, @WorkDate, @TimeEventId, @ExceptionType, @Detail);
          """,
          new
          {
            command.Rfc,
            command.EmployeeId,
            WorkDate = workDate,
            TimeEventId = eventId,
            ExceptionType = $"LOCATION_{location.Status}",
            location.Detail
          }, transaction, cancellationToken: ct));
      }

      await RecalculateAsync(connection, transaction, command.Rfc, command.EmployeeId, workDate, ct);
      await WorkforceServiceBase.WriteAuditAsync(connection, transaction, command.Rfc, command.EmployeeId,
        "TimeEvent", eventId, "PUNCH_RECORDED", $"{eventType};{command.Source};{location.Status}", command.Actor, ct);
      transaction.Commit();
      return new AttendancePunchResult
      {
        Success = true,
        Message = location.RequiresReview
          ? "Registro guardado. La evidencia de ubicacion requiere revision del supervisor."
          : "Registro de asistencia guardado.",
        EventId = eventId,
        NextEventType = AttendanceTransitionRules.GetNextEventType(eventType),
        LocationStatus = location.Status,
        DistanceMeters = location.DistanceMeters,
        RequiresReview = location.RequiresReview
      };
    }
    catch
    {
      transaction.Rollback();
      throw;
    }
  }

  public async Task RecalculateAsync(IDbConnection connection, IDbTransaction transaction, string rfc, int employeeId, DateOnly workDate, CancellationToken ct)
  {
    var row = await connection.QuerySingleAsync<CalculationRow>(new CommandDefinition(
      """
      SELECT TOP (1) wa.SiteId, wa.ScheduleTemplateId, wa.AttendancePolicyId,
        sd.StartTime, sd.EndTime, sd.UnpaidBreakMinutes,
        ap.GraceMinutes, ap.RoundingMinutes, site.TimeZoneId,
        CAST(CASE WHEN EXISTS(SELECT 1 FROM rh.Holiday holiday WHERE holiday.Rfc=@Rfc
          AND holiday.HolidayDate=@WorkDate AND (holiday.SiteId IS NULL OR holiday.SiteId=wa.SiteId)) THEN 1 ELSE 0 END AS bit) IsHoliday,
        ISNULL((SELECT SUM(leaveRequest.RequestedDays/NULLIF(DATEDIFF(day,leaveRequest.StartDate,leaveRequest.EndDate)+1,0))
          FROM rh.LeaveRequest leaveRequest WHERE leaveRequest.Rfc=@Rfc AND leaveRequest.EmployeeId=@EmployeeId
            AND leaveRequest.[Status]='APPROVED' AND @WorkDate BETWEEN leaveRequest.StartDate AND leaveRequest.EndDate),0) ApprovedLeaveFraction
      FROM rh.EmployeeWorkAssignment wa
      INNER JOIN rh.AttendancePolicy ap ON ap.Id=wa.AttendancePolicyId
      INNER JOIN rh.WorkSite site ON site.Id=wa.SiteId
      LEFT JOIN rh.ScheduleDay sd ON sd.ScheduleTemplateId=wa.ScheduleTemplateId AND sd.DayOfWeek=@DayOfWeek
      WHERE wa.Rfc=@Rfc AND wa.EmployeeId=@EmployeeId
        AND wa.EffectiveFrom<=@WorkDate AND (wa.EffectiveTo IS NULL OR wa.EffectiveTo>=@WorkDate)
      ORDER BY wa.EffectiveFrom DESC, wa.Id DESC;
      """,
      new { Rfc = rfc, EmployeeId = employeeId, WorkDate = workDate, DayOfWeek = (int)workDate.DayOfWeek },
      transaction, cancellationToken: ct));
    var events = (await connection.QueryAsync<CalculationEventRow>(new CommandDefinition(
      """
      SELECT EventType, OccurredAtUtc
      FROM rh.TimeEvent
      WHERE Rfc=@Rfc AND EmployeeId=@EmployeeId AND WorkDate=@WorkDate
      ORDER BY OccurredAtUtc, Id;
      """,
      new { Rfc = rfc, EmployeeId = employeeId, WorkDate = workDate }, transaction, cancellationToken: ct))).AsList();
    var zone = ResolveTimeZone(row.TimeZoneId);
    var result = AttendanceCalculator.Calculate(new AttendanceCalculationInput(
      workDate,
      row.StartTime,
      row.EndTime,
      row.UnpaidBreakMinutes,
      row.GraceMinutes,
      row.RoundingMinutes,
      events.Select(item => new AttendanceCalculationEvent(item.EventType, item.OccurredAtUtc,
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(item.OccurredAtUtc, DateTimeKind.Utc), zone))).ToArray()));
    var excusedFraction = row.IsHoliday ? 1m : Math.Clamp(row.ApprovedLeaveFraction, 0m, 1m);
    var excusedMinutes = (int)Math.Round(result.ScheduledMinutes * excusedFraction, MidpointRounding.AwayFromZero);
    var absenceMinutes = Math.Max(0, result.AbsenceMinutes - excusedMinutes);
    await connection.ExecuteAsync(new CommandDefinition(
      """
      MERGE rh.AttendanceDay WITH (HOLDLOCK) AS target
      USING (SELECT @Rfc Rfc, @EmployeeId EmployeeId, @WorkDate WorkDate) source
      ON target.Rfc=source.Rfc AND target.EmployeeId=source.EmployeeId AND target.WorkDate=source.WorkDate
      WHEN MATCHED THEN UPDATE SET SiteId=@SiteId, ScheduleTemplateId=@ScheduleTemplateId,
        AttendancePolicyId=@AttendancePolicyId, ScheduledMinutes=@ScheduledMinutes, WorkedMinutes=@WorkedMinutes,
        BreakMinutes=@BreakMinutes, AbsenceMinutes=@AbsenceMinutes, LateMinutes=@LateMinutes, EarlyDepartureMinutes=@EarlyDepartureMinutes,
        OvertimeCandidateMinutes=@OvertimeCandidateMinutes, [Status]=@Status,
        HasExceptions=0, CalculatedAtUtc=SYSUTCDATETIME()
      WHEN NOT MATCHED THEN INSERT
        (Rfc, EmployeeId, WorkDate, SiteId, ScheduleTemplateId, AttendancePolicyId,
         ScheduledMinutes, WorkedMinutes, BreakMinutes, AbsenceMinutes, LateMinutes, EarlyDepartureMinutes,
         OvertimeCandidateMinutes, [Status], HasExceptions)
        VALUES (@Rfc, @EmployeeId, @WorkDate, @SiteId, @ScheduleTemplateId, @AttendancePolicyId,
         @ScheduledMinutes, @WorkedMinutes, @BreakMinutes, @AbsenceMinutes, @LateMinutes, @EarlyDepartureMinutes,
         @OvertimeCandidateMinutes, @Status, 0);
      """,
      new
      {
        Rfc = rfc,
        EmployeeId = employeeId,
        WorkDate = workDate,
        row.SiteId,
        row.ScheduleTemplateId,
        row.AttendancePolicyId,
        result.ScheduledMinutes,
        result.WorkedMinutes,
        result.BreakMinutes,
        AbsenceMinutes = absenceMinutes,
        result.LateMinutes,
        result.EarlyDepartureMinutes,
        result.OvertimeCandidateMinutes,
        result.Status
      }, transaction, cancellationToken: ct));

    var attendanceDayId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
      "SELECT Id FROM rh.AttendanceDay WHERE Rfc=@Rfc AND EmployeeId=@EmployeeId AND WorkDate=@WorkDate;",
      new { Rfc = rfc, EmployeeId = employeeId, WorkDate = workDate }, transaction, cancellationToken: ct));
    await connection.ExecuteAsync(new CommandDefinition(
      """
      UPDATE rh.AttendanceException SET [Status]='APPROVED',Resolution=N'Resuelta automáticamente por recálculo.',
        ResolvedAtUtc=SYSUTCDATETIME(),ResolvedBy=N'OrionERP recálculo'
      WHERE AttendanceDayId=@AttendanceDayId AND [Status] IN ('PENDING','RETURNED') AND ExceptionType IN ('UNPAIRED','LATE','EARLY_DEPARTURE','ABSENCE')
        AND ((ExceptionType='UNPAIRED' AND @Unpaired=0) OR (ExceptionType='LATE' AND @LateMinutes=0)
          OR (ExceptionType='EARLY_DEPARTURE' AND @EarlyMinutes=0) OR (ExceptionType='ABSENCE' AND @AbsenceMinutes=0));

      INSERT INTO rh.AttendanceException(Rfc,EmployeeId,WorkDate,AttendanceDayId,ExceptionType,Detail)
      SELECT @Rfc,@EmployeeId,@WorkDate,@AttendanceDayId,candidate.ExceptionType,candidate.Detail
      FROM (VALUES
        ('UNPAIRED',CAST(@Unpaired AS bit),N'La secuencia de registros está incompleta.'),
        ('LATE',CAST(CASE WHEN @LateMinutes>0 THEN 1 ELSE 0 END AS bit),CONCAT(N'Entrada posterior a la tolerancia: ',@LateMinutes,N' minutos.')),
        ('EARLY_DEPARTURE',CAST(CASE WHEN @EarlyMinutes>0 THEN 1 ELSE 0 END AS bit),CONCAT(N'Salida anticipada: ',@EarlyMinutes,N' minutos.')),
        ('ABSENCE',CAST(CASE WHEN @AbsenceMinutes>0 THEN 1 ELSE 0 END AS bit),CONCAT(N'Tiempo programado no cubierto: ',@AbsenceMinutes,N' minutos.'))
      ) candidate(ExceptionType,IsActive,Detail)
      WHERE candidate.IsActive=1 AND NOT EXISTS(SELECT 1 FROM rh.AttendanceException existing
        WHERE existing.AttendanceDayId=@AttendanceDayId AND existing.ExceptionType=candidate.ExceptionType);
      """, new
      {
        Rfc = rfc,
        EmployeeId = employeeId,
        WorkDate = workDate,
        AttendanceDayId = attendanceDayId,
        Unpaired = result.HasUnpairedEvents,
        result.LateMinutes,
        EarlyMinutes = result.EarlyDepartureMinutes,
        AbsenceMinutes = absenceMinutes
      }, transaction, cancellationToken: ct));
    var pendingExceptions = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
      "SELECT COUNT(1) FROM rh.AttendanceException WHERE Rfc=@Rfc AND EmployeeId=@EmployeeId AND WorkDate=@WorkDate AND [Status] IN ('PENDING','RETURNED');",
      new { Rfc = rfc, EmployeeId = employeeId, WorkDate = workDate }, transaction, cancellationToken: ct));
    await connection.ExecuteAsync(new CommandDefinition(
      "UPDATE rh.AttendanceDay SET [Status]=@Status,HasExceptions=@HasExceptions,CalculatedAtUtc=SYSUTCDATETIME() WHERE Id=@Id;",
      new { Id = attendanceDayId, Status = pendingExceptions > 0 ? "EXCEPTION" : result.Status, HasExceptions = pendingExceptions > 0 }, transaction, cancellationToken: ct));
  }

  private const string AssignmentSql =
    """
    SELECT TOP (1) wa.SiteId, site.[Name] SiteName, site.TimeZoneId, site.Latitude, site.Longitude,
      site.RadiusMeters, site.MaxAccuracyMeters, policy.LocationRequired
    FROM rh.EmployeeWorkAssignment wa WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN rh.WorkSite site ON site.Id=wa.SiteId AND site.Rfc=wa.Rfc AND site.IsActive=1
    INNER JOIN rh.AttendancePolicy policy ON policy.Id=wa.AttendancePolicyId AND policy.Rfc=wa.Rfc AND policy.IsActive=1
    WHERE wa.Rfc=@Rfc AND wa.EmployeeId=@EmployeeId
      AND wa.EffectiveFrom<=@EffectiveDate AND (wa.EffectiveTo IS NULL OR wa.EffectiveTo>=@EffectiveDate)
    ORDER BY wa.EffectiveFrom DESC, wa.Id DESC;
    """;

  private static TimeZoneInfo ResolveTimeZone(string id)
  {
    try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
    catch { return TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City"); }
  }

  private static AttendancePunchResult Failure(string message) => new() { Message = message };

  private sealed class AssignmentRow
  {
    public int SiteId { get; set; }
    public string SiteName { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = string.Empty;
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public int RadiusMeters { get; set; }
    public int MaxAccuracyMeters { get; set; }
    public bool LocationRequired { get; set; }
  }
  private sealed class EventStateRow { public string EventType { get; set; } = string.Empty; public DateOnly WorkDate { get; set; } }
  private sealed class ExistingEventRow { public long Id { get; set; } public string EventType { get; set; } = string.Empty; public string LocationStatus { get; set; } = string.Empty; public decimal? DistanceMeters { get; set; } }
  private sealed class CalculationRow
  {
    public int SiteId { get; set; }
    public int ScheduleTemplateId { get; set; }
    public int AttendancePolicyId { get; set; }
    public TimeSpan? StartTime { get; set; }
    public TimeSpan? EndTime { get; set; }
    public int UnpaidBreakMinutes { get; set; }
    public int GraceMinutes { get; set; }
    public int RoundingMinutes { get; set; }
    public string TimeZoneId { get; set; } = string.Empty;
    public bool IsHoliday { get; set; }
    public decimal ApprovedLeaveFraction { get; set; }
  }
  private sealed class CalculationEventRow { public string EventType { get; set; } = string.Empty; public DateTime OccurredAtUtc { get; set; } }
}

public sealed class AttendanceService : WorkforceServiceBase, IAttendanceService
{
  private readonly IAttendanceRecorder _recorder;

  public AttendanceService(IDbConnectionFactory connectionFactory, ICurrentEmployeeAccessor currentEmployeeAccessor, IAttendanceRecorder recorder)
    : base(connectionFactory, currentEmployeeAccessor) => _recorder = recorder;

  public async Task<AttendancePunchResult> PunchAsync(AttendancePunchRequest request, CancellationToken ct = default)
  {
    var rfc = NormalizeRfc(request.Rfc);
    var actor = await RequireActorAsync(rfc, true, ct);
    using (var connection = CreateOpenConnection())
    {
      var acknowledgementRequired = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
        """
        SELECT COUNT(1)
        FROM rh.PrivacyNotice notice
        WHERE notice.Rfc=@Rfc
          AND notice.IsActive=1
          AND notice.EffectiveFrom<=CONVERT(date,SYSUTCDATETIME())
          AND NOT EXISTS
          (
            SELECT 1 FROM rh.EmployeePrivacyAcknowledgement acknowledgement
            WHERE acknowledgement.PrivacyNoticeId=notice.Id AND acknowledgement.EmployeeId=@EmployeeId
          );
        """, new { Rfc = rfc, EmployeeId = actor.EmployeeId!.Value }, cancellationToken: ct));
      if (acknowledgementRequired > 0)
        return new AttendancePunchResult { Message = "Debes aceptar el aviso de privacidad vigente antes de registrar tu asistencia." };
    }
    return await _recorder.RecordAsync(new AttendanceRecordCommand(rfc, actor.EmployeeId!.Value,
      request.EventType, AttendanceSources.Login, request.IdempotencyKey, request.Location,
      NormalizeActor(actor.UserName)), ct);
  }

  public async Task<EmployeeAttendanceDashboardDto> GetMyDashboardAsync(string rfc, DateOnly? asOfDate = null, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, true, ct);
    var employeeId = actor.EmployeeId!.Value;
    var through = asOfDate ?? DateOnly.FromDateTime(DateTime.Today);
    var from = through.AddDays(-30);
    using var connection = CreateOpenConnection();
    var header = await connection.QuerySingleOrDefaultAsync<EmployeeHeaderRow>(new CommandDefinition(
      """
      SELECT ch.ID EmployeeId,
        COALESCE(NULLIF(ch.NombreCorto,''), CONCAT(ch.Nombre,' ',ch.ApellidoPaterno)) EmployeeName,
        ch.Puesto Position, ISNULL(site.[Name],'Sin asignar') SiteName,
        ISNULL(schedule.[Name],'Sin asignar') ScheduleName
      FROM dbo.Capital_Humano ch
      OUTER APPLY (SELECT TOP (1) * FROM rh.EmployeeWorkAssignment wa WHERE wa.Rfc=ch.RFC AND wa.EmployeeId=ch.ID AND wa.EffectiveFrom<=@Through AND (wa.EffectiveTo IS NULL OR wa.EffectiveTo>=@Through) ORDER BY wa.EffectiveFrom DESC) wa
      LEFT JOIN rh.WorkSite site ON site.Id=wa.SiteId
      LEFT JOIN rh.ScheduleTemplate schedule ON schedule.Id=wa.ScheduleTemplateId
      WHERE ch.RFC=@Rfc AND ch.ID=@EmployeeId;
      """, new { Rfc = normalizedRfc, EmployeeId = employeeId, Through = through }, cancellationToken: ct));
    if (header is null) throw new UnauthorizedAccessException("El empleado no pertenece al RFC seleccionado.");

    var days = (await connection.QueryAsync<AttendanceDayDto>(new CommandDefinition(
      """
      SELECT d.Id,d.EmployeeId,COALESCE(NULLIF(ch.NombreCorto,''),CONCAT(ch.Nombre,' ',ch.ApellidoPaterno)) EmployeeName,
        d.WorkDate,d.ScheduledMinutes,d.WorkedMinutes,d.BreakMinutes,d.AbsenceMinutes,d.LateMinutes,d.EarlyDepartureMinutes,
        d.OvertimeCandidateMinutes,d.OvertimeApprovedMinutes,d.[Status],d.HasExceptions
      FROM rh.AttendanceDay d INNER JOIN dbo.Capital_Humano ch ON ch.ID=d.EmployeeId
      WHERE d.Rfc=@Rfc AND d.EmployeeId=@EmployeeId AND d.WorkDate BETWEEN @From AND @Through
      ORDER BY d.WorkDate DESC;
      """, new { Rfc = normalizedRfc, EmployeeId = employeeId, From = from, Through = through }, cancellationToken: ct))).AsList();
    var events = (await connection.QueryAsync<AttendanceEventDto>(new CommandDefinition(
      """
      SELECT TOP (30) e.Id,e.EmployeeId,e.EventType,e.[Source],e.OccurredAtUtc,e.WorkDate,
        site.[Name] SiteName,e.LocationStatus,e.DistanceMeters,e.AccuracyMeters,e.IsAdjustment
      FROM rh.TimeEvent e INNER JOIN rh.WorkSite site ON site.Id=e.SiteId
      WHERE e.Rfc=@Rfc AND e.EmployeeId=@EmployeeId ORDER BY e.OccurredAtUtc DESC,e.Id DESC;
      """, new { Rfc = normalizedRfc, EmployeeId = employeeId }, cancellationToken: ct))).AsList();
    var exceptions = (await connection.QueryAsync<AttendanceExceptionDto>(new CommandDefinition(
      """
      SELECT x.Id,x.EmployeeId,COALESCE(NULLIF(ch.NombreCorto,''),CONCAT(ch.Nombre,' ',ch.ApellidoPaterno)) EmployeeName,
        x.WorkDate,x.ExceptionType,x.Detail,x.Resolution,x.[Status],x.CreatedAtUtc,
        loc.LocationStatus,loc.DistanceMeters,loc.AccuracyMeters,loc.SiteRadiusMeters,loc.SiteMaxAccuracyMeters,loc.SiteName
      FROM rh.AttendanceException x INNER JOIN dbo.Capital_Humano ch ON ch.ID=x.EmployeeId
      OUTER APPLY (
        SELECT TOP (1) evidence.LocationStatus, evidence.DistanceMeters, evidence.AccuracyMeters,
               site.RadiusMeters SiteRadiusMeters, site.MaxAccuracyMeters SiteMaxAccuracyMeters, site.[Name] SiteName
        FROM rh.TimeEvent evidence
        LEFT JOIN rh.WorkSite site ON site.Id = evidence.SiteId
        WHERE evidence.Rfc = x.Rfc AND evidence.EmployeeId = x.EmployeeId AND evidence.WorkDate = x.WorkDate
          AND evidence.DistanceMeters IS NOT NULL
        ORDER BY evidence.OccurredAtUtc DESC, evidence.Id DESC
      ) loc
      WHERE x.Rfc=@Rfc AND x.EmployeeId=@EmployeeId AND x.[Status] IN ('PENDING','RETURNED') ORDER BY x.CreatedAtUtc DESC;
      """, new { Rfc = normalizedRfc, EmployeeId = employeeId }, cancellationToken: ct))).AsList();
    var correctionRequests = (await connection.QueryAsync<AttendanceCorrectionRequestDto>(new CommandDefinition(
      """
      SELECT c.Id,c.EmployeeId,COALESCE(NULLIF(ch.NombreCorto,''),CONCAT(ch.Nombre,' ',ch.ApellidoPaterno)) EmployeeName,
        c.EventType,c.RequestedAtUtc,c.Reason,c.DecisionReason,c.[Status]
      FROM rh.AttendanceCorrectionRequest c INNER JOIN dbo.Capital_Humano ch ON ch.ID=c.EmployeeId
      WHERE c.Rfc=@Rfc AND c.EmployeeId=@EmployeeId ORDER BY c.CreatedAtUtc DESC;
      """, new { Rfc = normalizedRfc, EmployeeId = employeeId }, cancellationToken: ct))).AsList();
    var balances = await QueryBalancesAsync(connection, normalizedRfc, employeeId, ct);
    var leaveRequests = await QueryLeaveRequestsAsync(connection, normalizedRfc, employeeId, ct);
    var privacyNotice = await connection.QuerySingleOrDefaultAsync<PrivacyNoticeDto>(new CommandDefinition(
      """
      SELECT TOP (1) notice.Id,notice.Version,notice.Title,notice.NoticeText,notice.EffectiveFrom,notice.IsActive,
        CAST(CASE WHEN acknowledgement.Id IS NULL THEN 0 ELSE 1 END AS bit) IsAcknowledged
      FROM rh.PrivacyNotice notice
      LEFT JOIN rh.EmployeePrivacyAcknowledgement acknowledgement
        ON acknowledgement.PrivacyNoticeId=notice.Id AND acknowledgement.EmployeeId=@EmployeeId
      WHERE notice.Rfc=@Rfc AND notice.IsActive=1 AND notice.EffectiveFrom<=@Through
      ORDER BY notice.EffectiveFrom DESC,notice.Id DESC;
      """, new { Rfc = normalizedRfc, EmployeeId = employeeId, Through = through }, cancellationToken: ct));
    var lastType = events.FirstOrDefault(item => item.OccurredAtUtc >= DateTime.UtcNow.AddHours(-30))?.EventType;
    return new EmployeeAttendanceDashboardDto
    {
      EmployeeId = employeeId,
      EmployeeName = header.EmployeeName,
      Position = header.Position,
      SiteName = header.SiteName,
      ScheduleName = header.ScheduleName,
      CurrentState = lastType ?? "OUT",
      NextEventType = AttendanceTransitionRules.GetNextEventType(lastType),
      Days = days,
      RecentEvents = events,
      Exceptions = exceptions,
      CorrectionRequests = correctionRequests,
      LeaveBalances = balances,
      LeaveRequests = leaveRequests,
      PrivacyNotice = privacyNotice,
      Period = new AttendancePeriodSummaryDto
      {
        FromDate = from,
        ToDate = through,
        ScheduledMinutes = days.Sum(x => x.ScheduledMinutes),
        WorkedMinutes = days.Sum(x => x.WorkedMinutes),
        OvertimeApprovedMinutes = days.Sum(x => x.OvertimeApprovedMinutes),
        PendingExceptions = exceptions.Count
      }
    };
  }

  public async Task<WorkforceCommandResult> AcknowledgePrivacyNoticeAsync(int privacyNoticeId, string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, true, ct);
    var employeeId = actor.EmployeeId!.Value;
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction();
    var isCurrent = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
      """
      SELECT COUNT(1) FROM rh.PrivacyNotice
      WHERE Id=@PrivacyNoticeId AND Rfc=@Rfc AND IsActive=1 AND EffectiveFrom<=CONVERT(date,SYSUTCDATETIME());
      """, new { PrivacyNoticeId = privacyNoticeId, Rfc = normalizedRfc }, transaction, cancellationToken: ct));
    if (isCurrent == 0)
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail("El aviso ya no es la versión vigente.");
    }

    await connection.ExecuteAsync(new CommandDefinition(
      """
      IF NOT EXISTS
      (
        SELECT 1 FROM rh.EmployeePrivacyAcknowledgement WITH (UPDLOCK,HOLDLOCK)
        WHERE PrivacyNoticeId=@PrivacyNoticeId AND EmployeeId=@EmployeeId
      )
      INSERT INTO rh.EmployeePrivacyAcknowledgement(PrivacyNoticeId,EmployeeId,AcknowledgedFrom)
      VALUES(@PrivacyNoticeId,@EmployeeId,'EMPLOYEE_PORTAL');
      """, new { PrivacyNoticeId = privacyNoticeId, EmployeeId = employeeId }, transaction, cancellationToken: ct));
    await WriteAuditAsync(connection, transaction, normalizedRfc, employeeId, "PrivacyNotice", privacyNoticeId,
      "ACKNOWLEDGED", null, actor.UserName, ct);
    transaction.Commit();
    return WorkforceCommandResult.Ok("Aviso de privacidad aceptado.", privacyNoticeId);
  }

  public async Task<TeamAttendanceDashboardDto> GetTeamDashboardAsync(string rfc, DateOnly fromDate, DateOnly toDate, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin", "CapitalHumanoSupervisor", "CapitalHumanoNomina");
    if (toDate < fromDate) throw new ArgumentException("El rango de fechas no es valido.");
    using var connection = CreateOpenConnection();
    var elevated = actor.IsInRole("Administrador", "CapitalHumanoAdmin", "CapitalHumanoNomina");
    if (!elevated && !actor.EmployeeId.HasValue)
      throw new UnauthorizedAccessException("El supervisor debe estar ligado a un empleado para consultar su equipo.");
    var supervisorId = elevated ? (int?)null : actor.EmployeeId;
    var args = new { Rfc = normalizedRfc, From = fromDate, To = toDate, SupervisorId = supervisorId };
    var scope = "(@SupervisorId IS NULL OR EXISTS (SELECT 1 FROM rh.SupervisorAssignment sa WHERE sa.Rfc=@Rfc AND sa.EmployeeId=employeeId AND sa.SupervisorEmployeeId=@SupervisorId AND sa.EffectiveFrom<=@To AND (sa.EffectiveTo IS NULL OR sa.EffectiveTo>=@From)))";
    var days = (await connection.QueryAsync<AttendanceDayDto>(new CommandDefinition($"""
      SELECT d.Id,d.EmployeeId,COALESCE(NULLIF(ch.NombreCorto,''),CONCAT(ch.Nombre,' ',ch.ApellidoPaterno)) EmployeeName,
        d.WorkDate,d.ScheduledMinutes,d.WorkedMinutes,d.BreakMinutes,d.AbsenceMinutes,d.LateMinutes,d.EarlyDepartureMinutes,
        d.OvertimeCandidateMinutes,d.OvertimeApprovedMinutes,d.[Status],d.HasExceptions
      FROM rh.AttendanceDay d INNER JOIN dbo.Capital_Humano ch ON ch.ID=d.EmployeeId
      WHERE d.Rfc=@Rfc AND d.WorkDate BETWEEN @From AND @To AND {scope.Replace("employeeId", "d.EmployeeId")}
      ORDER BY d.WorkDate DESC,EmployeeName;
      """, args, cancellationToken: ct))).AsList();
    var exceptions = (await connection.QueryAsync<AttendanceExceptionDto>(new CommandDefinition($"""
      SELECT x.Id,x.EmployeeId,COALESCE(NULLIF(ch.NombreCorto,''),CONCAT(ch.Nombre,' ',ch.ApellidoPaterno)) EmployeeName,
        x.WorkDate,x.ExceptionType,x.Detail,x.Resolution,x.[Status],x.CreatedAtUtc,
        loc.LocationStatus,loc.DistanceMeters,loc.AccuracyMeters,loc.SiteRadiusMeters,loc.SiteMaxAccuracyMeters,loc.SiteName
      FROM rh.AttendanceException x INNER JOIN dbo.Capital_Humano ch ON ch.ID=x.EmployeeId
      OUTER APPLY (
        SELECT TOP (1) evidence.LocationStatus, evidence.DistanceMeters, evidence.AccuracyMeters,
               site.RadiusMeters SiteRadiusMeters, site.MaxAccuracyMeters SiteMaxAccuracyMeters, site.[Name] SiteName
        FROM rh.TimeEvent evidence
        LEFT JOIN rh.WorkSite site ON site.Id = evidence.SiteId
        WHERE evidence.Rfc = x.Rfc AND evidence.EmployeeId = x.EmployeeId AND evidence.WorkDate = x.WorkDate
          AND evidence.DistanceMeters IS NOT NULL
        ORDER BY evidence.OccurredAtUtc DESC, evidence.Id DESC
      ) loc
      WHERE x.Rfc=@Rfc AND x.WorkDate BETWEEN @From AND @To AND x.[Status] IN ('PENDING','RETURNED') AND {scope.Replace("employeeId", "x.EmployeeId")}
      ORDER BY x.CreatedAtUtc;
      """, args, cancellationToken: ct))).AsList();
    var corrections = (await connection.QueryAsync<AttendanceCorrectionRequestDto>(new CommandDefinition($"""
      SELECT c.Id,c.EmployeeId,COALESCE(NULLIF(ch.NombreCorto,''),CONCAT(ch.Nombre,' ',ch.ApellidoPaterno)) EmployeeName,
        c.EventType,c.RequestedAtUtc,c.Reason,c.DecisionReason,c.[Status]
      FROM rh.AttendanceCorrectionRequest c INNER JOIN dbo.Capital_Humano ch ON ch.ID=c.EmployeeId
      WHERE c.Rfc=@Rfc AND CAST(c.RequestedAtUtc AS date) BETWEEN @From AND @To AND c.[Status] IN ('PENDING','RETURNED') AND {scope.Replace("employeeId", "c.EmployeeId")}
      ORDER BY c.CreatedAtUtc;
      """, args, cancellationToken: ct))).AsList();
    var leaves = (await connection.QueryAsync<LeaveRequestDto>(new CommandDefinition($"""
      SELECT l.Id,l.EmployeeId,COALESCE(NULLIF(ch.NombreCorto,''),CONCAT(ch.Nombre,' ',ch.ApellidoPaterno)) EmployeeName,
        l.LeaveTypeId,t.[Name] LeaveTypeName,l.StartDate,l.EndDate,l.RequestedDays,l.Reason,l.[Status]
      FROM rh.LeaveRequest l INNER JOIN dbo.Capital_Humano ch ON ch.ID=l.EmployeeId INNER JOIN rh.LeaveType t ON t.Id=l.LeaveTypeId
      WHERE l.Rfc=@Rfc AND l.[Status]='PENDING' AND l.StartDate<=@To AND l.EndDate>=@From AND {scope.Replace("employeeId", "l.EmployeeId")}
      ORDER BY l.StartDate;
      """, args, cancellationToken: ct))).AsList();
    var employeesAtWork = await connection.ExecuteScalarAsync<int>(new CommandDefinition($"""
      SELECT COUNT(1) FROM
      (SELECT e.EmployeeId FROM rh.TimeEvent e WHERE e.Rfc=@Rfc AND e.WorkDate=@To AND {scope.Replace("employeeId", "e.EmployeeId")}
       GROUP BY e.EmployeeId HAVING (SELECT TOP (1) e2.EventType FROM rh.TimeEvent e2 WHERE e2.Rfc=@Rfc AND e2.EmployeeId=e.EmployeeId AND e2.WorkDate=@To ORDER BY e2.OccurredAtUtc DESC,e2.Id DESC)<>'OUT') q;
      """, args, cancellationToken: ct));
    return new TeamAttendanceDashboardDto
    {
      Days = days,
      Exceptions = exceptions,
      Corrections = corrections,
      LeaveRequests = leaves,
      EmployeesAtWork = employeesAtWork,
      PendingActions = exceptions.Count + corrections.Count + leaves.Count
    };
  }

  public async Task<WorkforceCommandResult> SubmitCorrectionAsync(AttendanceCorrectionCreateRequest request, CancellationToken ct = default)
  {
    var rfc = NormalizeRfc(request.Rfc);
    var actor = await RequireActorAsync(rfc, true, ct);
    if (!AttendanceEventTypes.All.Contains(request.EventType)) return WorkforceCommandResult.Fail("El tipo de registro no es valido.");
    using var connection = CreateOpenConnection();
    var timeZoneId = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
      """
      SELECT TOP(1) site.TimeZoneId FROM rh.EmployeeWorkAssignment wa
      INNER JOIN rh.WorkSite site ON site.Id=wa.SiteId
      WHERE wa.Rfc=@Rfc AND wa.EmployeeId=@EmployeeId AND wa.EffectiveFrom<=@RequestedDate
        AND (wa.EffectiveTo IS NULL OR wa.EffectiveTo>=@RequestedDate)
      ORDER BY wa.EffectiveFrom DESC,wa.Id DESC;
      """, new { Rfc = rfc, EmployeeId = actor.EmployeeId!.Value, RequestedDate = DateOnly.FromDateTime(request.RequestedAtLocal) }, cancellationToken: ct));
    if (string.IsNullOrWhiteSpace(timeZoneId)) return WorkforceCommandResult.Fail("No existe una asignacion vigente para la fecha solicitada.");
    var localTime = DateTime.SpecifyKind(request.RequestedAtLocal, DateTimeKind.Unspecified);
    var requestedAtUtc = TimeZoneInfo.ConvertTimeToUtc(localTime, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));
    var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
      """
      INSERT INTO rh.AttendanceCorrectionRequest (Rfc,EmployeeId,EventType,RequestedAtUtc,Reason,CreatedBy)
      VALUES (@Rfc,@EmployeeId,@EventType,@RequestedAtUtc,@Reason,@Actor);
      SELECT CAST(SCOPE_IDENTITY() AS bigint);
      """, new { Rfc = rfc, EmployeeId = actor.EmployeeId!.Value, EventType = request.EventType.ToUpperInvariant(), RequestedAtUtc = requestedAtUtc, Reason = request.Reason.Trim(), Actor = actor.UserName }, cancellationToken: ct));
    return WorkforceCommandResult.Ok("Solicitud de correccion enviada.", id);
  }

  public Task<WorkforceCommandResult> DecideExceptionAsync(long exceptionId, string rfc, bool approve, string reason, CancellationToken ct = default)
    => DecideScopedAsync("AttendanceException", exceptionId, rfc, approve ? ApprovalStatuses.Approved : ApprovalStatuses.Rejected, reason, ct);

  public Task<WorkforceCommandResult> ReturnExceptionAsync(long exceptionId, string rfc, string reason, CancellationToken ct = default)
    => DecideScopedAsync("AttendanceException", exceptionId, rfc, ApprovalStatuses.Returned, reason, ct);

  public async Task<WorkforceCommandResult> DecideCorrectionAsync(long correctionId, string rfc, bool approve, string reason, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin", "CapitalHumanoSupervisor");
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
    var correction = await connection.QuerySingleOrDefaultAsync<CorrectionRow>(new CommandDefinition(
      "SELECT * FROM rh.AttendanceCorrectionRequest WITH (UPDLOCK,HOLDLOCK) WHERE Id=@Id AND Rfc=@Rfc AND [Status] IN ('PENDING','RETURNED');",
      new { Id = correctionId, Rfc = normalizedRfc }, transaction, cancellationToken: ct));
    if (correction is null || !await CanManageEmployeeAsync(connection, transaction, actor, normalizedRfc, correction.EmployeeId, DateOnly.FromDateTime(correction.RequestedAtUtc), ct))
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail("La solicitud no existe o no pertenece a su equipo.");
    }
    if (!approve)
    {
      await connection.ExecuteAsync(new CommandDefinition("UPDATE rh.AttendanceCorrectionRequest SET [Status]='REJECTED',DecisionReason=@Reason,DecidedAtUtc=SYSUTCDATETIME(),DecidedBy=@Actor WHERE Id=@Id;", new { Id = correctionId, Reason = reason.Trim(), Actor = actor.UserName }, transaction, cancellationToken: ct));
      transaction.Commit();
      return WorkforceCommandResult.Ok("Correccion rechazada.", correctionId);
    }
    transaction.Rollback();
    var result = await _recorder.RecordAsync(new AttendanceRecordCommand(normalizedRfc, correction.EmployeeId, correction.EventType,
      AttendanceSources.Adjustment, $"correction:{correctionId}", new LocationEvidenceDto(), NormalizeActor(actor.UserName),
      OccurredAtUtc: correction.RequestedAtUtc, IsAdjustment: true, Reason: correction.Reason), ct);
    if (!result.Success) return WorkforceCommandResult.Fail(result.Message, correctionId);
    using var updateConnection = CreateOpenConnection();
    await updateConnection.ExecuteAsync(new CommandDefinition(
      "UPDATE rh.AttendanceCorrectionRequest SET [Status]='APPROVED',DecisionReason=@Reason,DecidedAtUtc=SYSUTCDATETIME(),DecidedBy=@Actor,AdjustmentEventId=@EventId WHERE Id=@Id AND [Status] IN ('PENDING','RETURNED');",
      new { Id = correctionId, Reason = reason.Trim(), Actor = actor.UserName, result.EventId }, cancellationToken: ct));
    return WorkforceCommandResult.Ok("Correccion aprobada y agregada al historial.", correctionId);
  }

  public async Task<WorkforceCommandResult> ReturnCorrectionAsync(long correctionId, string rfc, string reason, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin", "CapitalHumanoSupervisor");
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
    var correction = await connection.QuerySingleOrDefaultAsync<CorrectionRow>(new CommandDefinition(
      "SELECT EmployeeId,EventType,RequestedAtUtc,Reason FROM rh.AttendanceCorrectionRequest WITH(UPDLOCK,HOLDLOCK) WHERE Id=@Id AND Rfc=@Rfc AND [Status]='PENDING';",
      new { Id = correctionId, Rfc = normalizedRfc }, transaction, cancellationToken: ct));
    if (correction is null || !await CanManageEmployeeAsync(connection, transaction, actor, normalizedRfc, correction.EmployeeId, DateOnly.FromDateTime(correction.RequestedAtUtc), ct))
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail("La solicitud no existe o no pertenece a su equipo.");
    }
    await connection.ExecuteAsync(new CommandDefinition(
      "UPDATE rh.AttendanceCorrectionRequest SET [Status]='RETURNED',DecisionReason=@Reason,DecidedAtUtc=SYSUTCDATETIME(),DecidedBy=@Actor WHERE Id=@Id;",
      new { Id = correctionId, Reason = reason.Trim(), Actor = actor.UserName }, transaction, cancellationToken: ct));
    await WriteAuditAsync(connection, transaction, normalizedRfc, correction.EmployeeId, "AttendanceCorrectionRequest", correctionId, "RETURNED", reason, actor.UserName, ct);
    transaction.Commit();
    return WorkforceCommandResult.Ok("Correccion devuelta al empleado.", correctionId);
  }

  public async Task<WorkforceCommandResult> DecideOvertimeAsync(long attendanceDayId, string rfc, int approvedMinutes, string reason, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin", "CapitalHumanoSupervisor");
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
    var day = await connection.QuerySingleOrDefaultAsync<DayScopeRow>(new CommandDefinition("SELECT EmployeeId,WorkDate,OvertimeCandidateMinutes FROM rh.AttendanceDay WITH(UPDLOCK,HOLDLOCK) WHERE Id=@Id AND Rfc=@Rfc;", new { Id = attendanceDayId, Rfc = normalizedRfc }, transaction, cancellationToken: ct));
    if (day is null || approvedMinutes < 0 || approvedMinutes > day.OvertimeCandidateMinutes || !await CanManageEmployeeAsync(connection, transaction, actor, normalizedRfc, day.EmployeeId, day.WorkDate, ct))
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail("La autorizacion no es valida o no pertenece a su equipo.");
    }
    await connection.ExecuteAsync(new CommandDefinition(
      """
      UPDATE rh.AttendanceDay SET OvertimeApprovedMinutes=@Minutes,ApprovedAtUtc=SYSUTCDATETIME(),ApprovedBy=@Actor WHERE Id=@Id;
      INSERT INTO rh.OvertimeDecision
        (Rfc,AttendanceDayId,EmployeeId,CandidateMinutes,ApprovedMinutes,Decision,Reason,DecidedBy)
      VALUES
        (@Rfc,@Id,@EmployeeId,@CandidateMinutes,@Minutes,
         CASE WHEN @Minutes=0 THEN 'REJECTED' WHEN @Minutes<@CandidateMinutes THEN 'PARTIAL' ELSE 'APPROVED' END,
         @Reason,@Actor);
      """, new { Id = attendanceDayId, Rfc = normalizedRfc, day.EmployeeId, CandidateMinutes = day.OvertimeCandidateMinutes, Minutes = approvedMinutes, Reason = reason.Trim(), Actor = actor.UserName }, transaction, cancellationToken: ct));
    await WriteAuditAsync(connection, transaction, normalizedRfc, day.EmployeeId, "AttendanceDay", attendanceDayId, "OVERTIME_DECIDED", $"{approvedMinutes} minutos; {reason}", actor.UserName, ct);
    transaction.Commit();
    return WorkforceCommandResult.Ok("Tiempo extra autorizado.", attendanceDayId);
  }

  private async Task<WorkforceCommandResult> DecideScopedAsync(string table, long id, string rfc, string decision, string reason, CancellationToken ct)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin", "CapitalHumanoSupervisor");
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
    if (decision is not (ApprovalStatuses.Approved or ApprovalStatuses.Rejected or ApprovalStatuses.Returned))
      return WorkforceCommandResult.Fail("La decision no es valida.");
    var allowedStatus = decision == ApprovalStatuses.Returned ? "[Status]='PENDING'" : "[Status] IN ('PENDING','RETURNED')";
    var row = await connection.QuerySingleOrDefaultAsync<ExceptionScopeRow>(new CommandDefinition($"SELECT EmployeeId,WorkDate FROM rh.{table} WITH(UPDLOCK,HOLDLOCK) WHERE Id=@Id AND Rfc=@Rfc AND {allowedStatus};", new { Id = id, Rfc = normalizedRfc }, transaction, cancellationToken: ct));
    if (row is null || !await CanManageEmployeeAsync(connection, transaction, actor, normalizedRfc, row.EmployeeId, row.WorkDate, ct))
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail("La excepcion no existe o no pertenece a su equipo.");
    }
    await connection.ExecuteAsync(new CommandDefinition($"UPDATE rh.{table} SET [Status]=@Status,Resolution=@Reason,ResolvedAtUtc=CASE WHEN @Status='RETURNED' THEN NULL ELSE SYSUTCDATETIME() END,ResolvedBy=CASE WHEN @Status='RETURNED' THEN NULL ELSE @Actor END WHERE Id=@Id;", new { Id = id, Status = decision, Reason = reason.Trim(), Actor = actor.UserName }, transaction, cancellationToken: ct));
    await _recorder.RecalculateAsync(connection, transaction, normalizedRfc, row.EmployeeId, row.WorkDate, ct);
    await WriteAuditAsync(connection, transaction, normalizedRfc, row.EmployeeId, table, id, decision, reason, actor.UserName, ct);
    transaction.Commit();
    return WorkforceCommandResult.Ok(decision switch { ApprovalStatuses.Approved => "Excepcion aprobada.", ApprovalStatuses.Rejected => "Excepcion rechazada.", _ => "Excepcion devuelta al empleado." }, id);
  }

  internal static async Task<IReadOnlyList<LeaveBalanceDto>> QueryBalancesAsync(IDbConnection connection, string rfc, int employeeId, CancellationToken ct)
    => (await connection.QueryAsync<LeaveBalanceDto>(new CommandDefinition(
      """
      SELECT t.Id LeaveTypeId,t.Code LeaveTypeCode,t.[Name] LeaveTypeName,ISNULL(SUM(l.Days),0) BalanceDays
      FROM rh.LeaveType t LEFT JOIN rh.LeaveBalanceLedger l ON l.LeaveTypeId=t.Id AND l.Rfc=t.Rfc AND l.EmployeeId=@EmployeeId
      WHERE t.Rfc=@Rfc AND t.IsActive=1 GROUP BY t.Id,t.Code,t.[Name] ORDER BY t.[Name];
      """, new { Rfc = rfc, EmployeeId = employeeId }, cancellationToken: ct))).AsList();

  internal static async Task<IReadOnlyList<LeaveRequestDto>> QueryLeaveRequestsAsync(IDbConnection connection, string rfc, int employeeId, CancellationToken ct)
    => (await connection.QueryAsync<LeaveRequestDto>(new CommandDefinition(
      """
      SELECT l.Id,l.EmployeeId,COALESCE(NULLIF(ch.NombreCorto,''),CONCAT(ch.Nombre,' ',ch.ApellidoPaterno)) EmployeeName,
        l.LeaveTypeId,t.[Name] LeaveTypeName,l.StartDate,l.EndDate,l.RequestedDays,l.Reason,l.[Status]
      FROM rh.LeaveRequest l INNER JOIN dbo.Capital_Humano ch ON ch.ID=l.EmployeeId INNER JOIN rh.LeaveType t ON t.Id=l.LeaveTypeId
      WHERE l.Rfc=@Rfc AND l.EmployeeId=@EmployeeId ORDER BY l.CreatedAtUtc DESC;
      """, new { Rfc = rfc, EmployeeId = employeeId }, cancellationToken: ct))).AsList();

  private sealed class EmployeeHeaderRow { public string EmployeeName { get; set; } = string.Empty; public string? Position { get; set; } public string SiteName { get; set; } = string.Empty; public string ScheduleName { get; set; } = string.Empty; }
  private sealed class CorrectionRow { public int EmployeeId { get; set; } public string EventType { get; set; } = string.Empty; public DateTime RequestedAtUtc { get; set; } public string Reason { get; set; } = string.Empty; }
  private sealed class DayScopeRow { public int EmployeeId { get; set; } public DateOnly WorkDate { get; set; } public int OvertimeCandidateMinutes { get; set; } }
  private sealed class ExceptionScopeRow { public int EmployeeId { get; set; } public DateOnly WorkDate { get; set; } }
}
