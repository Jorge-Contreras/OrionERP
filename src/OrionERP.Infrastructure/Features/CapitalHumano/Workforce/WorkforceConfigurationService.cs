using System.Data;
using System.Security.Cryptography;
using Dapper;
using Microsoft.AspNetCore.Identity;
using OrionERP.Application.Common;
using OrionERP.Application.Features.CapitalHumano.Workforce;

namespace OrionERP.Infrastructure.Features.CapitalHumano.Workforce;

public sealed class WorkforceConfigurationService : WorkforceServiceBase, IWorkforceConfigurationService
{
  private readonly PasswordHasher<object> _passwordHasher = new();

  public WorkforceConfigurationService(
    IDbConnectionFactory connectionFactory,
    ICurrentEmployeeAccessor currentEmployeeAccessor)
    : base(connectionFactory, currentEmployeeAccessor)
  {
  }

  public async Task<WorkforceSetupSnapshotDto> GetSetupAsync(string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin", "CapitalHumanoNomina");

    const string sql =
      """
      SELECT
        ch.ID AS EmployeeId,
        COALESCE(NULLIF(ch.NombreCorto, ''), CONCAT(ch.Nombre, ' ', ch.ApellidoPaterno)) AS [Name],
        ch.Puesto AS Position,
        CAST(CASE WHEN au.EmployeeId IS NULL THEN 0 ELSE 1 END AS bit) AS HasLogin,
        CAST(CASE WHEN wa.Id IS NULL THEN 0 ELSE 1 END AS bit) AS IsConfigured
      FROM dbo.Capital_Humano ch
      LEFT JOIN (SELECT DISTINCT EmployeeId FROM auth.AspNetUsers WHERE EmployeeId IS NOT NULL) au ON au.EmployeeId = ch.ID
      OUTER APPLY
      (
        SELECT TOP (1) a.Id
        FROM rh.EmployeeWorkAssignment a
        WHERE a.Rfc = ch.RFC AND a.EmployeeId = ch.ID
          AND a.EffectiveFrom <= CAST(GETDATE() AS date)
          AND (a.EffectiveTo IS NULL OR a.EffectiveTo >= CAST(GETDATE() AS date))
        ORDER BY a.EffectiveFrom DESC, a.Id DESC
      ) wa
      WHERE ch.RFC = @Rfc
        AND UPPER(LTRIM(RTRIM(ISNULL(ch.[Status], '')))) = 'ACTIVO'
      ORDER BY [Name], ch.ID;

      SELECT Id, Rfc, Code, [Name], TimeZoneId, Latitude, Longitude, RadiusMeters, MaxAccuracyMeters, IsActive
      FROM rh.WorkSite WHERE Rfc = @Rfc ORDER BY IsActive DESC, [Name];

      SELECT Id, Rfc, Code, [Name], IsActive
      FROM rh.ScheduleTemplate WHERE Rfc = @Rfc ORDER BY IsActive DESC, [Name];

      SELECT sd.ScheduleTemplateId, sd.DayOfWeek, sd.IsWorkingDay, sd.StartTime, sd.EndTime, sd.UnpaidBreakMinutes
      FROM rh.ScheduleDay sd
      INNER JOIN rh.ScheduleTemplate st ON st.Id = sd.ScheduleTemplateId
      WHERE st.Rfc = @Rfc
      ORDER BY sd.ScheduleTemplateId, sd.DayOfWeek;

      SELECT scheduleBreak.Id,scheduleBreak.ScheduleTemplateId,scheduleBreak.[Name],scheduleBreak.StartTime,
        scheduleBreak.DurationMinutes,scheduleBreak.IsPaid,scheduleBreak.IsRequired
      FROM rh.ScheduleBreak scheduleBreak
      INNER JOIN rh.ScheduleTemplate template ON template.Id=scheduleBreak.ScheduleTemplateId
      WHERE template.Rfc=@Rfc ORDER BY scheduleBreak.ScheduleTemplateId,scheduleBreak.StartTime,scheduleBreak.Id;

      SELECT Id, Rfc, Code, [Name], EffectiveFrom, EffectiveTo, WeeklyOrdinaryMinutes,
        WeeklyDoubleOvertimeMinutes, WeeklyTripleOvertimeMinutes, GraceMinutes, RoundingMinutes,
        LocationRequired, IsActive
      FROM rh.AttendancePolicy WHERE Rfc = @Rfc ORDER BY EffectiveFrom DESC, Code;

      SELECT Id, Rfc, Code, [Name], Frequency, IsActive
      FROM rh.PayGroup WHERE Rfc = @Rfc ORDER BY IsActive DESC, [Name];

      SELECT wa.Id, wa.Rfc, wa.EmployeeId,
        COALESCE(NULLIF(ch.NombreCorto, ''), CONCAT(ch.Nombre, ' ', ch.ApellidoPaterno)) AS EmployeeName,
        wa.SiteId, site.[Name] AS SiteName, wa.ScheduleTemplateId, schedule.[Name] AS ScheduleName,
        wa.AttendancePolicyId, policy.[Name] AS PolicyName, wa.PayGroupId, pg.[Name] AS PayGroupName,
        wa.EffectiveFrom, wa.EffectiveTo
      FROM rh.EmployeeWorkAssignment wa
      INNER JOIN dbo.Capital_Humano ch ON ch.ID = wa.EmployeeId AND ch.RFC = wa.Rfc
      INNER JOIN rh.WorkSite site ON site.Id = wa.SiteId
      INNER JOIN rh.ScheduleTemplate schedule ON schedule.Id = wa.ScheduleTemplateId
      INNER JOIN rh.AttendancePolicy policy ON policy.Id = wa.AttendancePolicyId
      INNER JOIN rh.PayGroup pg ON pg.Id = wa.PayGroupId
      WHERE wa.Rfc = @Rfc
      ORDER BY wa.EffectiveFrom DESC, EmployeeName;

      SELECT sa.Id, sa.EmployeeId,
        COALESCE(NULLIF(employee.NombreCorto, ''), CONCAT(employee.Nombre, ' ', employee.ApellidoPaterno)) AS EmployeeName,
        sa.SupervisorEmployeeId,
        COALESCE(NULLIF(supervisor.NombreCorto, ''), CONCAT(supervisor.Nombre, ' ', supervisor.ApellidoPaterno)) AS SupervisorName,
        sa.EffectiveFrom, sa.EffectiveTo
      FROM rh.SupervisorAssignment sa
      INNER JOIN dbo.Capital_Humano employee ON employee.ID = sa.EmployeeId AND employee.RFC = sa.Rfc
      INNER JOIN dbo.Capital_Humano supervisor ON supervisor.ID = sa.SupervisorEmployeeId AND supervisor.RFC = sa.Rfc
      WHERE sa.Rfc = @Rfc
      ORDER BY sa.EffectiveFrom DESC, EmployeeName;

      SELECT ch.ID AS EmployeeId,
        COALESCE(NULLIF(ch.NombreCorto, ''), CONCAT(ch.Nombre, ' ', ch.ApellidoPaterno)) AS EmployeeName,
        CAST(CASE WHEN au.EmployeeId IS NULL THEN 0 ELSE 1 END AS bit) AS HasLogin,
        CAST(CASE WHEN wa.Id IS NULL THEN 0 ELSE 1 END AS bit) AS HasWorkAssignment,
        CAST(CASE WHEN sa.Id IS NULL THEN 0 ELSE 1 END AS bit) AS HasSupervisor
      FROM dbo.Capital_Humano ch
      LEFT JOIN (SELECT DISTINCT EmployeeId FROM auth.AspNetUsers WHERE EmployeeId IS NOT NULL) au ON au.EmployeeId = ch.ID
      OUTER APPLY
      (
        SELECT TOP (1) a.Id FROM rh.EmployeeWorkAssignment a
        WHERE a.Rfc = ch.RFC AND a.EmployeeId = ch.ID
          AND a.EffectiveFrom <= CAST(GETDATE() AS date)
          AND (a.EffectiveTo IS NULL OR a.EffectiveTo >= CAST(GETDATE() AS date))
      ) wa
      OUTER APPLY
      (
        SELECT TOP (1) a.Id FROM rh.SupervisorAssignment a
        WHERE a.Rfc = ch.RFC AND a.EmployeeId = ch.ID
          AND a.EffectiveFrom <= CAST(GETDATE() AS date)
          AND (a.EffectiveTo IS NULL OR a.EffectiveTo >= CAST(GETDATE() AS date))
      ) sa
      WHERE ch.RFC = @Rfc AND UPPER(LTRIM(RTRIM(ISNULL(ch.[Status], '')))) = 'ACTIVO'
      ORDER BY EmployeeName;

      SELECT device.Id, device.[Name], device.SiteId, site.[Name] AS SiteName,
        device.IsActive, device.LastSeenAtUtc
      FROM rh.KioskDevice device
      INNER JOIN rh.WorkSite site ON site.Id = device.SiteId
      WHERE device.Rfc = @Rfc
      ORDER BY device.IsActive DESC, device.[Name];

      SELECT Id,SiteId,HolidayDate,[Name],IsPaid
      FROM rh.Holiday WHERE Rfc=@Rfc ORDER BY HolidayDate DESC,[Name];

      SELECT Id,Version,Title,NoticeText,EffectiveFrom,IsActive,CAST(0 AS bit) IsAcknowledged
      FROM rh.PrivacyNotice WHERE Rfc=@Rfc ORDER BY EffectiveFrom DESC,Id DESC;
      """;

    using var connection = CreateOpenConnection();
    using var multi = await connection.QueryMultipleAsync(new CommandDefinition(sql, new { Rfc = normalizedRfc }, cancellationToken: ct));
    var employees = (await multi.ReadAsync<EmployeeWorkforceOptionDto>()).AsList();
    var sites = (await multi.ReadAsync<WorkSiteDto>()).AsList();
    var schedules = (await multi.ReadAsync<ScheduleTemplateDto>()).AsList();
    var days = (await multi.ReadAsync<ScheduleDayRow>()).AsList();
    var breaks = (await multi.ReadAsync<ScheduleBreakRow>()).AsList();
    foreach (var schedule in schedules)
    {
      schedule.Days = days.Where(day => day.ScheduleTemplateId == schedule.Id)
        .Select(day => new ScheduleDayDto
        {
          DayOfWeek = day.DayOfWeek,
          IsWorkingDay = day.IsWorkingDay,
          StartTime = day.StartTime,
          EndTime = day.EndTime,
          UnpaidBreakMinutes = day.UnpaidBreakMinutes
        }).ToList();
      schedule.Breaks = breaks.Where(item => item.ScheduleTemplateId == schedule.Id)
        .Select(item => new ScheduleBreakDto
        {
          Id = item.Id,
          Name = item.Name,
          StartTime = item.StartTime,
          DurationMinutes = item.DurationMinutes,
          IsPaid = item.IsPaid,
          IsRequired = item.IsRequired
        }).ToList();
    }

    return new WorkforceSetupSnapshotDto
    {
      Employees = employees,
      Sites = sites,
      Schedules = schedules,
      Policies = (await multi.ReadAsync<AttendancePolicyDto>()).AsList(),
      PayGroups = (await multi.ReadAsync<PayGroupDto>()).AsList(),
      WorkAssignments = (await multi.ReadAsync<EmployeeWorkAssignmentDto>()).AsList(),
      SupervisorAssignments = (await multi.ReadAsync<SupervisorAssignmentDto>()).AsList(),
      Readiness = (await multi.ReadAsync<WorkforceReadinessDto>()).AsList(),
      Kiosks = (await multi.ReadAsync<KioskDeviceDto>()).AsList(),
      Holidays = (await multi.ReadAsync<HolidayDto>()).AsList(),
      PrivacyNotices = (await multi.ReadAsync<PrivacyNoticeDto>()).AsList()
    };
  }

  public async Task<WorkforceCommandResult> SaveSiteAsync(WorkSiteSaveRequest request, CancellationToken ct = default)
  {
    var rfc = NormalizeRfc(request.Rfc);
    var actor = await RequireActorAsync(rfc, false, ct, "CapitalHumanoAdmin");
    if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
      return WorkforceCommandResult.Fail("El codigo y nombre del sitio son obligatorios.");

    _ = ResolveTimeZone(request.TimeZoneId);
    const string sql =
      """
      IF @Id IS NULL
      BEGIN
        INSERT INTO rh.WorkSite
          (Rfc, Code, [Name], TimeZoneId, Latitude, Longitude, RadiusMeters, MaxAccuracyMeters, IsActive, CreatedBy)
        VALUES
          (@Rfc, @Code, @Name, @TimeZoneId, @Latitude, @Longitude, @RadiusMeters, @MaxAccuracyMeters, @IsActive, @Actor);
        SELECT CAST(SCOPE_IDENTITY() AS int);
      END
      ELSE
      BEGIN
        UPDATE rh.WorkSite
        SET Code=@Code, [Name]=@Name, TimeZoneId=@TimeZoneId, Latitude=@Latitude, Longitude=@Longitude,
            RadiusMeters=@RadiusMeters, MaxAccuracyMeters=@MaxAccuracyMeters, IsActive=@IsActive,
            UpdatedAtUtc=SYSUTCDATETIME(), UpdatedBy=@Actor
        WHERE Id=@Id AND Rfc=@Rfc;
        SELECT CASE WHEN @@ROWCOUNT = 0 THEN 0 ELSE @Id END;
      END;
      """;
    using var connection = CreateOpenConnection();
    var id = await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new
    {
      request.Id,
      Rfc = rfc,
      Code = request.Code.Trim().ToUpperInvariant(),
      Name = request.Name.Trim(),
      TimeZoneId = request.TimeZoneId.Trim(),
      request.Latitude,
      request.Longitude,
      request.RadiusMeters,
      request.MaxAccuracyMeters,
      request.IsActive,
      Actor = NormalizeActor(actor.UserName)
    }, cancellationToken: ct));
    return id > 0 ? WorkforceCommandResult.Ok("Sitio guardado.", id) : WorkforceCommandResult.Fail("No se encontro el sitio.");
  }

  public async Task<WorkforceCommandResult> SaveScheduleAsync(ScheduleTemplateSaveRequest request, CancellationToken ct = default)
  {
    var rfc = NormalizeRfc(request.Rfc);
    var actor = await RequireActorAsync(rfc, false, ct, "CapitalHumanoAdmin");
    if (request.Days.GroupBy(day => day.DayOfWeek).Any(group => group.Count() > 1) || request.Days.Any(day => day.DayOfWeek is < 0 or > 6))
      return WorkforceCommandResult.Fail("La plantilla contiene dias duplicados o invalidos.");

    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction();
    try
    {
      const string headerSql =
        """
        IF @Id IS NULL
        BEGIN
          INSERT INTO rh.ScheduleTemplate (Rfc, Code, [Name], IsActive, CreatedBy)
          VALUES (@Rfc, @Code, @Name, @IsActive, @Actor);
          SELECT CAST(SCOPE_IDENTITY() AS int);
        END
        ELSE
        BEGIN
          UPDATE rh.ScheduleTemplate
          SET Code=@Code, [Name]=@Name, IsActive=@IsActive, UpdatedAtUtc=SYSUTCDATETIME(), UpdatedBy=@Actor
          WHERE Id=@Id AND Rfc=@Rfc;
          SELECT CASE WHEN @@ROWCOUNT = 0 THEN 0 ELSE @Id END;
        END;
        """;
      var scheduleId = await connection.ExecuteScalarAsync<int>(new CommandDefinition(headerSql, new
      {
        request.Id,
        Rfc = rfc,
        Code = request.Code.Trim().ToUpperInvariant(),
        Name = request.Name.Trim(),
        request.IsActive,
        Actor = NormalizeActor(actor.UserName)
      }, transaction, cancellationToken: ct));
      if (scheduleId <= 0)
      {
        transaction.Rollback();
        return WorkforceCommandResult.Fail("No se encontro la plantilla.");
      }

      await connection.ExecuteAsync(new CommandDefinition("DELETE FROM rh.ScheduleDay WHERE ScheduleTemplateId=@ScheduleTemplateId;", new { ScheduleTemplateId = scheduleId }, transaction, cancellationToken: ct));
      const string daySql =
        """
        INSERT INTO rh.ScheduleDay
          (ScheduleTemplateId, DayOfWeek, IsWorkingDay, StartTime, EndTime, UnpaidBreakMinutes)
        VALUES
          (@ScheduleTemplateId, @DayOfWeek, @IsWorkingDay, @StartTime, @EndTime, @UnpaidBreakMinutes);
        """;
      var allDays = Enumerable.Range(0, 7).Select(dayNumber =>
      {
        var configured = request.Days.FirstOrDefault(day => day.DayOfWeek == dayNumber);
        return configured ?? new ScheduleDayDto { DayOfWeek = dayNumber, IsWorkingDay = false };
      });
      foreach (var day in allDays)
      {
        if (day.IsWorkingDay && (!day.StartTime.HasValue || !day.EndTime.HasValue))
          throw new InvalidOperationException("Cada dia laborable requiere hora de inicio y fin.");
        await connection.ExecuteAsync(new CommandDefinition(daySql, new
        {
          ScheduleTemplateId = scheduleId,
          day.DayOfWeek,
          day.IsWorkingDay,
          day.StartTime,
          day.EndTime,
          UnpaidBreakMinutes = Math.Clamp(day.UnpaidBreakMinutes, 0, 480)
        }, transaction, cancellationToken: ct));
      }
      await connection.ExecuteAsync(new CommandDefinition("DELETE FROM rh.ScheduleBreak WHERE ScheduleTemplateId=@ScheduleTemplateId;", new { ScheduleTemplateId = scheduleId }, transaction, cancellationToken: ct));
      foreach (var scheduleBreak in request.Breaks)
      {
        if (string.IsNullOrWhiteSpace(scheduleBreak.Name) || scheduleBreak.DurationMinutes is < 1 or > 480)
          throw new InvalidOperationException("Cada descanso requiere nombre y una duracion valida.");
        await connection.ExecuteAsync(new CommandDefinition(
          "INSERT INTO rh.ScheduleBreak(ScheduleTemplateId,[Name],StartTime,DurationMinutes,IsPaid,IsRequired) VALUES(@ScheduleTemplateId,@Name,@StartTime,@DurationMinutes,@IsPaid,@IsRequired);",
          new { ScheduleTemplateId = scheduleId, Name = scheduleBreak.Name.Trim(), scheduleBreak.StartTime, scheduleBreak.DurationMinutes, scheduleBreak.IsPaid, scheduleBreak.IsRequired }, transaction, cancellationToken: ct));
      }
      await WriteAuditAsync(connection, transaction, rfc, null, "ScheduleTemplate", scheduleId, "SAVED", request.Name, actor.UserName, ct);
      transaction.Commit();
      return WorkforceCommandResult.Ok("Plantilla de horario guardada.", scheduleId);
    }
    catch
    {
      transaction.Rollback();
      throw;
    }
  }

  public async Task<WorkforceCommandResult> SavePolicyAsync(AttendancePolicySaveRequest request, CancellationToken ct = default)
  {
    var rfc = NormalizeRfc(request.Rfc);
    var actor = await RequireActorAsync(rfc, false, ct, "CapitalHumanoAdmin");
    if (request.EffectiveTo.HasValue && request.EffectiveTo < request.EffectiveFrom)
      return WorkforceCommandResult.Fail("La vigencia de la politica no es valida.");

    const string sql =
      """
      IF @Id <= 0
      BEGIN
        INSERT INTO rh.AttendancePolicy
          (Rfc, Code, [Name], EffectiveFrom, EffectiveTo, WeeklyOrdinaryMinutes,
           WeeklyDoubleOvertimeMinutes, WeeklyTripleOvertimeMinutes, GraceMinutes,
           RoundingMinutes, LocationRequired, IsActive, RequiresReview, CreatedBy)
        VALUES
          (@Rfc, @Code, @Name, @EffectiveFrom, @EffectiveTo, @WeeklyOrdinaryMinutes,
           @WeeklyDoubleOvertimeMinutes, @WeeklyTripleOvertimeMinutes, @GraceMinutes,
           @RoundingMinutes, @LocationRequired, @IsActive, 0, @Actor);
        SELECT CAST(SCOPE_IDENTITY() AS int);
      END
      ELSE
      BEGIN
        UPDATE rh.AttendancePolicy
        SET Code=@Code, [Name]=@Name, EffectiveFrom=@EffectiveFrom, EffectiveTo=@EffectiveTo,
            WeeklyOrdinaryMinutes=@WeeklyOrdinaryMinutes,
            WeeklyDoubleOvertimeMinutes=@WeeklyDoubleOvertimeMinutes,
            WeeklyTripleOvertimeMinutes=@WeeklyTripleOvertimeMinutes,
            GraceMinutes=@GraceMinutes, RoundingMinutes=@RoundingMinutes,
            LocationRequired=@LocationRequired, IsActive=@IsActive, RequiresReview=0
        WHERE Id=@Id AND Rfc=@Rfc;
        SELECT CASE WHEN @@ROWCOUNT=0 THEN 0 ELSE @Id END;
      END;
      """;
    using var connection = CreateOpenConnection();
    var id = await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new
    {
      request.Id,
      Rfc = rfc,
      Code = request.Code.Trim().ToUpperInvariant(),
      Name = request.Name.Trim(),
      request.EffectiveFrom,
      request.EffectiveTo,
      request.WeeklyOrdinaryMinutes,
      request.WeeklyDoubleOvertimeMinutes,
      request.WeeklyTripleOvertimeMinutes,
      request.GraceMinutes,
      request.RoundingMinutes,
      request.LocationRequired,
      request.IsActive,
      Actor = NormalizeActor(actor.UserName)
    }, cancellationToken: ct));
    return id > 0 ? WorkforceCommandResult.Ok("Politica guardada y marcada como revisada.", id) : WorkforceCommandResult.Fail("No se encontro la politica.");
  }

  public async Task<WorkforceCommandResult> SavePayGroupAsync(PayGroupSaveRequest request, CancellationToken ct = default)
  {
    var rfc = NormalizeRfc(request.Rfc);
    var actor = await RequireActorAsync(rfc, false, ct, "CapitalHumanoAdmin", "CapitalHumanoNomina");
    var frequency = request.Frequency.Trim().ToUpperInvariant();
    if (frequency is not ("WEEKLY" or "BIWEEKLY" or "MONTHLY"))
      return WorkforceCommandResult.Fail("La frecuencia debe ser semanal, quincenal o mensual.");
    const string sql =
      """
      IF @Id <= 0
      BEGIN
        INSERT INTO rh.PayGroup (Rfc, Code, [Name], Frequency, IsActive, CreatedBy)
        VALUES (@Rfc, @Code, @Name, @Frequency, @IsActive, @Actor);
        SELECT CAST(SCOPE_IDENTITY() AS int);
      END
      ELSE
      BEGIN
        UPDATE rh.PayGroup SET Code=@Code, [Name]=@Name, Frequency=@Frequency, IsActive=@IsActive
        WHERE Id=@Id AND Rfc=@Rfc;
        SELECT CASE WHEN @@ROWCOUNT=0 THEN 0 ELSE @Id END;
      END;
      """;
    using var connection = CreateOpenConnection();
    var id = await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new
    {
      request.Id,
      Rfc = rfc,
      Code = request.Code.Trim().ToUpperInvariant(),
      Name = request.Name.Trim(),
      Frequency = frequency,
      request.IsActive,
      Actor = NormalizeActor(actor.UserName)
    }, cancellationToken: ct));
    return id > 0 ? WorkforceCommandResult.Ok("Grupo de pago guardado.", id) : WorkforceCommandResult.Fail("No se encontro el grupo de pago.");
  }

  public async Task<WorkforceCommandResult> SaveWorkAssignmentAsync(EmployeeWorkAssignmentSaveRequest request, CancellationToken ct = default)
  {
    var rfc = NormalizeRfc(request.Rfc);
    var actor = await RequireActorAsync(rfc, false, ct, "CapitalHumanoAdmin");
    if (request.EffectiveTo.HasValue && request.EffectiveTo < request.EffectiveFrom)
      return WorkforceCommandResult.Fail("La vigencia de la asignacion no es valida.");
    const string sql =
      """
      IF NOT EXISTS (SELECT 1 FROM dbo.Capital_Humano WHERE ID=@EmployeeId AND RFC=@Rfc)
        THROW 51000, 'El empleado no existe para el RFC seleccionado.', 1;
      IF EXISTS
      (
        SELECT 1 FROM rh.EmployeeWorkAssignment
        WHERE Rfc=@Rfc AND EmployeeId=@EmployeeId AND Id<>ISNULL(@Id, 0)
          AND EffectiveFrom <= ISNULL(@EffectiveTo, '99991231')
          AND ISNULL(EffectiveTo, '99991231') >= @EffectiveFrom
      )
        THROW 51000, 'La asignacion se traslapa con otra vigencia del empleado.', 1;
      IF @Id IS NULL
      BEGIN
        INSERT INTO rh.EmployeeWorkAssignment
          (Rfc, EmployeeId, SiteId, ScheduleTemplateId, AttendancePolicyId, PayGroupId, EffectiveFrom, EffectiveTo, CreatedBy)
        SELECT @Rfc, @EmployeeId, @SiteId, @ScheduleTemplateId, @AttendancePolicyId, @PayGroupId, @EffectiveFrom, @EffectiveTo, @Actor
        WHERE EXISTS (SELECT 1 FROM rh.WorkSite WHERE Id=@SiteId AND Rfc=@Rfc)
          AND EXISTS (SELECT 1 FROM rh.ScheduleTemplate WHERE Id=@ScheduleTemplateId AND Rfc=@Rfc)
          AND EXISTS (SELECT 1 FROM rh.AttendancePolicy WHERE Id=@AttendancePolicyId AND Rfc=@Rfc)
          AND EXISTS (SELECT 1 FROM rh.PayGroup WHERE Id=@PayGroupId AND Rfc=@Rfc);
        SELECT CASE WHEN @@ROWCOUNT=0 THEN 0 ELSE CAST(SCOPE_IDENTITY() AS int) END;
      END
      ELSE
      BEGIN
        IF NOT EXISTS (SELECT 1 FROM rh.WorkSite WHERE Id=@SiteId AND Rfc=@Rfc)
          OR NOT EXISTS (SELECT 1 FROM rh.ScheduleTemplate WHERE Id=@ScheduleTemplateId AND Rfc=@Rfc)
          OR NOT EXISTS (SELECT 1 FROM rh.AttendancePolicy WHERE Id=@AttendancePolicyId AND Rfc=@Rfc)
          OR NOT EXISTS (SELECT 1 FROM rh.PayGroup WHERE Id=@PayGroupId AND Rfc=@Rfc)
          THROW 51000, 'Los catalogos de la asignacion no pertenecen al RFC.', 1;
        UPDATE rh.EmployeeWorkAssignment
        SET SiteId=@SiteId, ScheduleTemplateId=@ScheduleTemplateId, AttendancePolicyId=@AttendancePolicyId,
            PayGroupId=@PayGroupId, EffectiveFrom=@EffectiveFrom, EffectiveTo=@EffectiveTo,
            UpdatedAtUtc=SYSUTCDATETIME(), UpdatedBy=@Actor
        WHERE Id=@Id AND Rfc=@Rfc AND EmployeeId=@EmployeeId;
        SELECT CASE WHEN @@ROWCOUNT=0 THEN 0 ELSE @Id END;
      END;
      """;
    using var connection = CreateOpenConnection();
    var id = await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new
    {
      request.Id,
      Rfc = rfc,
      request.EmployeeId,
      request.SiteId,
      request.ScheduleTemplateId,
      request.AttendancePolicyId,
      request.PayGroupId,
      request.EffectiveFrom,
      request.EffectiveTo,
      Actor = NormalizeActor(actor.UserName)
    }, cancellationToken: ct));
    return id > 0 ? WorkforceCommandResult.Ok("Asignacion laboral guardada.", id) : WorkforceCommandResult.Fail("Verifica que los catalogos pertenezcan al RFC seleccionado.");
  }

  public async Task<WorkforceCommandResult> SaveSupervisorAssignmentAsync(SupervisorAssignmentSaveRequest request, CancellationToken ct = default)
  {
    var rfc = NormalizeRfc(request.Rfc);
    var actor = await RequireActorAsync(rfc, false, ct, "CapitalHumanoAdmin");
    if (request.EmployeeId == request.SupervisorEmployeeId)
      return WorkforceCommandResult.Fail("Un empleado no puede supervisarse a si mismo.");
    const string sql =
      """
      IF NOT EXISTS (SELECT 1 FROM dbo.Capital_Humano WHERE ID=@EmployeeId AND RFC=@Rfc)
         OR NOT EXISTS (SELECT 1 FROM dbo.Capital_Humano WHERE ID=@SupervisorEmployeeId AND RFC=@Rfc)
        THROW 51000, 'El empleado o supervisor no pertenece al RFC seleccionado.', 1;
      IF EXISTS
      (
        SELECT 1 FROM rh.SupervisorAssignment
        WHERE Rfc=@Rfc AND EmployeeId=@EmployeeId AND Id<>ISNULL(@Id, 0)
          AND EffectiveFrom <= ISNULL(@EffectiveTo, '99991231')
          AND ISNULL(EffectiveTo, '99991231') >= @EffectiveFrom
      )
        THROW 51000, 'La supervision se traslapa con otra vigencia del empleado.', 1;
      IF @Id IS NULL
      BEGIN
        INSERT INTO rh.SupervisorAssignment
          (Rfc, EmployeeId, SupervisorEmployeeId, EffectiveFrom, EffectiveTo, CreatedBy)
        VALUES (@Rfc, @EmployeeId, @SupervisorEmployeeId, @EffectiveFrom, @EffectiveTo, @Actor);
        SELECT CAST(SCOPE_IDENTITY() AS int);
      END
      ELSE
      BEGIN
        UPDATE rh.SupervisorAssignment
        SET SupervisorEmployeeId=@SupervisorEmployeeId, EffectiveFrom=@EffectiveFrom, EffectiveTo=@EffectiveTo
        WHERE Id=@Id AND Rfc=@Rfc AND EmployeeId=@EmployeeId;
        SELECT CASE WHEN @@ROWCOUNT=0 THEN 0 ELSE @Id END;
      END;
      """;
    using var connection = CreateOpenConnection();
    var id = await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new
    {
      request.Id,
      Rfc = rfc,
      request.EmployeeId,
      request.SupervisorEmployeeId,
      request.EffectiveFrom,
      request.EffectiveTo,
      Actor = NormalizeActor(actor.UserName)
    }, cancellationToken: ct));
    return id > 0 ? WorkforceCommandResult.Ok("Supervisor asignado.", id) : WorkforceCommandResult.Fail("No se pudo guardar la supervision.");
  }

  public async Task<KioskPairingCodeDto> CreateKioskPairingCodeAsync(KioskPairingCreateRequest request, CancellationToken ct = default)
  {
    var rfc = NormalizeRfc(request.Rfc);
    var actor = await RequireActorAsync(rfc, false, ct, "CapitalHumanoAdmin");
    var code = RandomNumberGenerator.GetInt32(10000000, 99999999).ToString(System.Globalization.CultureInfo.InvariantCulture);
    var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code));
    var expires = DateTime.UtcNow.AddMinutes(15);
    const string sql =
      """
      INSERT INTO rh.KioskDevice (Rfc, SiteId, [Name], CreatedBy)
      SELECT @Rfc, @SiteId, @Name, @Actor
      WHERE EXISTS (SELECT 1 FROM rh.WorkSite WHERE Id=@SiteId AND Rfc=@Rfc AND IsActive=1);
      DECLARE @DeviceId int = CAST(SCOPE_IDENTITY() AS int);
      IF @DeviceId IS NULL THROW 51000, 'El sitio no existe o esta inactivo.', 1;
      INSERT INTO rh.KioskPairingCode (KioskDeviceId, CodeHash, ExpiresAtUtc)
      VALUES (@DeviceId, @CodeHash, @ExpiresAtUtc);
      SELECT @DeviceId;
      """;
    using var connection = CreateOpenConnection();
    _ = await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new
    {
      Rfc = rfc,
      request.SiteId,
      Name = request.DeviceName.Trim(),
      Actor = NormalizeActor(actor.UserName),
      CodeHash = hash,
      ExpiresAtUtc = expires
    }, cancellationToken: ct));
    return new KioskPairingCodeDto { Code = code, ExpiresAtUtc = expires };
  }

  public async Task<WorkforceCommandResult> SaveHolidayAsync(HolidaySaveRequest request, CancellationToken ct = default)
  {
    var rfc = NormalizeRfc(request.Rfc);
    var actor = await RequireActorAsync(rfc, false, ct, "CapitalHumanoAdmin");
    if (string.IsNullOrWhiteSpace(request.Name)) return WorkforceCommandResult.Fail("El nombre del dia festivo es obligatorio.");
    using var connection = CreateOpenConnection();
    var id = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
      """
      IF @SiteId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM rh.WorkSite WHERE Id=@SiteId AND Rfc=@Rfc)
        THROW 51000,'El sitio no pertenece al RFC.',1;
      IF @Id IS NULL
      BEGIN
        INSERT INTO rh.Holiday(Rfc,SiteId,HolidayDate,[Name],IsPaid) VALUES(@Rfc,@SiteId,@HolidayDate,@Name,@IsPaid);
        SELECT CAST(SCOPE_IDENTITY() AS int);
      END
      ELSE
      BEGIN
        UPDATE rh.Holiday SET SiteId=@SiteId,HolidayDate=@HolidayDate,[Name]=@Name,IsPaid=@IsPaid WHERE Id=@Id AND Rfc=@Rfc;
        SELECT CASE WHEN @@ROWCOUNT=0 THEN 0 ELSE @Id END;
      END;
      """, new { request.Id, Rfc = rfc, request.SiteId, request.HolidayDate, Name = request.Name.Trim(), request.IsPaid, Actor = actor.UserName }, cancellationToken: ct));
    return id > 0 ? WorkforceCommandResult.Ok("Dia festivo guardado.", id) : WorkforceCommandResult.Fail("No se encontro el dia festivo.");
  }

  public async Task<WorkforceCommandResult> SavePrivacyNoticeAsync(PrivacyNoticeSaveRequest request, CancellationToken ct = default)
  {
    var rfc = NormalizeRfc(request.Rfc);
    var actor = await RequireActorAsync(rfc, false, ct, "CapitalHumanoAdmin");
    if (string.IsNullOrWhiteSpace(request.NoticeText) || string.IsNullOrWhiteSpace(request.Version))
      return WorkforceCommandResult.Fail("La version y el texto del aviso son obligatorios.");
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction();
    if (request.IsActive)
      await connection.ExecuteAsync(new CommandDefinition("UPDATE rh.PrivacyNotice SET IsActive=0 WHERE Rfc=@Rfc;", new { Rfc = rfc }, transaction, cancellationToken: ct));
    var id = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
      """
      IF @Id IS NULL
      BEGIN
        INSERT INTO rh.PrivacyNotice(Rfc,Version,Title,NoticeText,EffectiveFrom,IsActive,CreatedBy)
        VALUES(@Rfc,@Version,@Title,@NoticeText,@EffectiveFrom,@IsActive,@Actor);
        SELECT CAST(SCOPE_IDENTITY() AS int);
      END
      ELSE
      BEGIN
        UPDATE rh.PrivacyNotice SET Version=@Version,Title=@Title,NoticeText=@NoticeText,EffectiveFrom=@EffectiveFrom,IsActive=@IsActive WHERE Id=@Id AND Rfc=@Rfc;
        SELECT CASE WHEN @@ROWCOUNT=0 THEN 0 ELSE @Id END;
      END;
      """, new { request.Id, Rfc = rfc, Version = request.Version.Trim(), Title = request.Title.Trim(), NoticeText = request.NoticeText.Trim(), request.EffectiveFrom, request.IsActive, Actor = actor.UserName }, transaction, cancellationToken: ct));
    if (id <= 0) { transaction.Rollback(); return WorkforceCommandResult.Fail("No se encontro el aviso de privacidad."); }
    await WriteAuditAsync(connection, transaction, rfc, null, "PrivacyNotice", id, request.IsActive ? "ACTIVATED" : "SAVED", request.Version, actor.UserName, ct);
    transaction.Commit();
    return WorkforceCommandResult.Ok(request.IsActive ? "Aviso guardado y activado." : "Aviso guardado como borrador.", id);
  }

  public async Task<KioskCredentialResult> CreateKioskCredentialAsync(KioskCredentialCreateRequest request, CancellationToken ct = default)
  {
    var rfc = NormalizeRfc(request.Rfc);
    var actor = await RequireActorAsync(rfc, false, ct, "CapitalHumanoAdmin");
    if (request.Pin.Any(character => !char.IsDigit(character)))
      return new KioskCredentialResult { Message = "El PIN debe contener solo numeros." };

    var badgeBytes = RandomNumberGenerator.GetBytes(24);
    var badgeToken = Convert.ToHexString(badgeBytes);
    var badgeHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(badgeToken));
    var pinHash = _passwordHasher.HashPassword(new object(), request.Pin);
    const string sql =
      """
      IF NOT EXISTS (SELECT 1 FROM dbo.Capital_Humano WHERE ID=@EmployeeId AND RFC=@Rfc)
        THROW 51000, 'El empleado no pertenece al RFC seleccionado.', 1;
      MERGE rh.EmployeeKioskCredential AS target
      USING (SELECT @Rfc AS Rfc, @EmployeeId AS EmployeeId) AS source
      ON target.Rfc=source.Rfc AND target.EmployeeId=source.EmployeeId
      WHEN MATCHED THEN UPDATE SET BadgeTokenHash=@BadgeTokenHash, PinHash=@PinHash,
        FailedAttempts=0, LockedUntilUtc=NULL, IsActive=1, CreatedAtUtc=SYSUTCDATETIME(), CreatedBy=@Actor
      WHEN NOT MATCHED THEN INSERT (Rfc, EmployeeId, BadgeTokenHash, PinHash, CreatedBy)
        VALUES (@Rfc, @EmployeeId, @BadgeTokenHash, @PinHash, @Actor);
      """;
    using var connection = CreateOpenConnection();
    await connection.ExecuteAsync(new CommandDefinition(sql, new
    {
      Rfc = rfc,
      request.EmployeeId,
      BadgeTokenHash = badgeHash,
      PinHash = pinHash,
      Actor = NormalizeActor(actor.UserName)
    }, cancellationToken: ct));
    return new KioskCredentialResult
    {
      Success = true,
      Message = "Credencial creada. El codigo de gafete se muestra una sola vez.",
      BadgeToken = badgeToken
    };
  }

  private static TimeZoneInfo ResolveTimeZone(string id)
  {
    try { return TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); }
    catch (TimeZoneNotFoundException) { throw new ArgumentException("La zona horaria no existe.", nameof(id)); }
    catch (InvalidTimeZoneException) { throw new ArgumentException("La zona horaria no es valida.", nameof(id)); }
  }

  private sealed class ScheduleDayRow
  {
    public int ScheduleTemplateId { get; set; }
    public int DayOfWeek { get; set; }
    public bool IsWorkingDay { get; set; }
    public TimeSpan? StartTime { get; set; }
    public TimeSpan? EndTime { get; set; }
    public int UnpaidBreakMinutes { get; set; }
  }
  private sealed class ScheduleBreakRow
  {
    public int Id { get; set; }
    public int ScheduleTemplateId { get; set; }
    public string Name { get; set; } = string.Empty;
    public TimeSpan? StartTime { get; set; }
    public int DurationMinutes { get; set; }
    public bool IsPaid { get; set; }
    public bool IsRequired { get; set; }
  }
}
