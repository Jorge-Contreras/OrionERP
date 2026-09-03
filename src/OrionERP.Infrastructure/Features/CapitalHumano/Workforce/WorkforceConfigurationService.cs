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
        CAST(CASE WHEN membership.EmployeeId IS NULL THEN 0 ELSE 1 END AS bit) AS HasLogin,
        CAST(CASE WHEN wa.Id IS NULL THEN 0 ELSE 1 END AS bit) AS IsConfigured
      FROM dbo.Capital_Humano ch
      LEFT JOIN (SELECT DISTINCT EmployeeId,Rfc FROM auth.AspNetUserCompanies WHERE EmployeeId IS NOT NULL AND IsActive=1) membership ON membership.EmployeeId = ch.ID AND membership.Rfc=ch.RFC
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
        LocationRequired, IsActive, RequiresReview
      FROM rh.AttendancePolicy WHERE Rfc = @Rfc ORDER BY RequiresReview DESC, EffectiveFrom DESC, Code;

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
        CAST(CASE WHEN membership.EmployeeId IS NULL THEN 0 ELSE 1 END AS bit) AS HasLogin,
        CAST(CASE WHEN wa.Id IS NULL THEN 0 ELSE 1 END AS bit) AS HasWorkAssignment,
        CAST(CASE WHEN sa.Id IS NULL THEN 0 ELSE 1 END AS bit) AS HasSupervisor
      FROM dbo.Capital_Humano ch
      LEFT JOIN (SELECT DISTINCT EmployeeId,Rfc FROM auth.AspNetUserCompanies WHERE EmployeeId IS NOT NULL AND IsActive=1) membership ON membership.EmployeeId = ch.ID AND membership.Rfc=ch.RFC
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
        device.IsActive, CAST(CASE WHEN device.DeviceTokenHash IS NULL THEN 0 ELSE 1 END AS bit) AS IsPaired,
        device.LastSeenAtUtc
      FROM rh.KioskDevice device
      INNER JOIN rh.WorkSite site ON site.Id = device.SiteId
      WHERE device.Rfc = @Rfc
      ORDER BY device.IsActive DESC, device.[Name];

      SELECT credential.EmployeeId,
        COALESCE(NULLIF(ch.NombreCorto, ''), CONCAT(ch.Nombre, ' ', ch.ApellidoPaterno)) AS EmployeeName,
        credential.IsActive, credential.FailedAttempts, credential.LockedUntilUtc, credential.CreatedAtUtc
      FROM rh.EmployeeKioskCredential credential
      INNER JOIN dbo.Capital_Humano ch ON ch.ID = credential.EmployeeId AND ch.RFC = credential.Rfc
      WHERE credential.Rfc = @Rfc
      ORDER BY EmployeeName;

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
      KioskCredentials = (await multi.ReadAsync<KioskCredentialDto>()).AsList(),
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
      IF EXISTS (SELECT 1 FROM rh.WorkSite WHERE Rfc=@Rfc AND Code=@Code AND Id<>ISNULL(@Id, 0))
        THROW 51000, 'Ya existe un sitio con ese codigo.', 1;
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
    using var transaction = connection.BeginTransaction();
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
    }, transaction, cancellationToken: ct));
    if (id <= 0)
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail("No se encontro el sitio.");
    }
    await WriteAuditAsync(connection, transaction, rfc, null, "WorkSite", id, request.Id is null ? "CREATED" : "UPDATED", request.Name, actor.UserName, ct);
    transaction.Commit();
    return WorkforceCommandResult.Ok("Sitio guardado.", id);
  }

  public async Task<WorkforceCommandResult> DeleteSiteAsync(int siteId, string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin");
    const string sql =
      """
      SELECT site.Id, site.[Name],
        (SELECT COUNT(1) FROM rh.EmployeeWorkAssignment wa WHERE wa.SiteId = site.Id) AS AssignmentCount,
        (SELECT COUNT(1) FROM rh.KioskDevice device WHERE device.SiteId = site.Id) AS KioskCount,
        (SELECT COUNT(1) FROM rh.Holiday holiday WHERE holiday.SiteId = site.Id) AS HolidayCount,
        (SELECT COUNT(1) FROM rh.TimeEvent e WHERE e.SiteId = site.Id) AS TimeEventCount
      FROM rh.WorkSite site WITH(UPDLOCK,HOLDLOCK)
      WHERE site.Id=@Id AND site.Rfc=@Rfc;
      """;
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction();
    var site = await connection.QuerySingleOrDefaultAsync<SiteUsageRow>(new CommandDefinition(
      sql, new { Id = siteId, Rfc = normalizedRfc }, transaction, cancellationToken: ct));
    if (site is null)
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail("No se encontro el sitio para el RFC seleccionado.");
    }
    var uses = site.AssignmentCount + site.KioskCount + site.HolidayCount + site.TimeEventCount;
    if (uses > 0)
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail(
        $"'{site.Name}' esta en uso: {site.AssignmentCount} asignacion(es), {site.KioskCount} kiosco(s), {site.HolidayCount} festivo(s) y {site.TimeEventCount} registro(s) de asistencia. Desactivalo en lugar de eliminarlo.");
    }
    await connection.ExecuteAsync(new CommandDefinition(
      "DELETE FROM rh.WorkSite WHERE Id=@Id AND Rfc=@Rfc;",
      new { Id = siteId, Rfc = normalizedRfc }, transaction, cancellationToken: ct));
    await WriteAuditAsync(connection, transaction, normalizedRfc, null, "WorkSite", siteId, "DELETED", site.Name, actor.UserName, ct);
    transaction.Commit();
    return WorkforceCommandResult.Ok($"Sitio '{site.Name}' eliminado.", siteId);
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
        IF EXISTS (SELECT 1 FROM rh.ScheduleTemplate WHERE Rfc=@Rfc AND Code=@Code AND Id<>ISNULL(@Id, 0))
          THROW 51000, 'Ya existe una plantilla de horario con ese codigo.', 1;
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

  public async Task<WorkforceCommandResult> DeleteScheduleAsync(int scheduleTemplateId, string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin");
    const string sql =
      """
      SELECT template.Id, template.[Name],
        (SELECT COUNT(1) FROM rh.EmployeeWorkAssignment wa WHERE wa.ScheduleTemplateId = template.Id) AS AssignmentCount,
        (SELECT COUNT(1) FROM rh.AttendanceDay d WHERE d.ScheduleTemplateId = template.Id) AS AttendanceDayCount
      FROM rh.ScheduleTemplate template WITH(UPDLOCK,HOLDLOCK)
      WHERE template.Id=@Id AND template.Rfc=@Rfc;
      """;
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction();
    var template = await connection.QuerySingleOrDefaultAsync<ScheduleUsageRow>(new CommandDefinition(
      sql, new { Id = scheduleTemplateId, Rfc = normalizedRfc }, transaction, cancellationToken: ct));
    if (template is null)
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail("No se encontro la plantilla para el RFC seleccionado.");
    }
    if (template.AssignmentCount > 0 || template.AttendanceDayCount > 0)
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail(
        $"'{template.Name}' tiene {template.AssignmentCount} asignacion(es) y {template.AttendanceDayCount} dia(s) de asistencia calculados. Desactivala en lugar de eliminarla.");
    }
    await connection.ExecuteAsync(new CommandDefinition(
      "DELETE FROM rh.ScheduleTemplate WHERE Id=@Id AND Rfc=@Rfc;",
      new { Id = scheduleTemplateId, Rfc = normalizedRfc }, transaction, cancellationToken: ct));
    await WriteAuditAsync(connection, transaction, normalizedRfc, null, "ScheduleTemplate", scheduleTemplateId, "DELETED", template.Name, actor.UserName, ct);
    transaction.Commit();
    return WorkforceCommandResult.Ok($"Plantilla '{template.Name}' eliminada.", scheduleTemplateId);
  }

  public async Task<WorkforceCommandResult> SavePolicyAsync(AttendancePolicySaveRequest request, CancellationToken ct = default)
  {
    var rfc = NormalizeRfc(request.Rfc);
    var actor = await RequireActorAsync(rfc, false, ct, "CapitalHumanoAdmin");
    if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
      return WorkforceCommandResult.Fail("El codigo y nombre de la politica son obligatorios.");
    if (request.EffectiveTo.HasValue && request.EffectiveTo < request.EffectiveFrom)
      return WorkforceCommandResult.Fail("La vigencia de la politica no es valida.");
    if (request.WeeklyOrdinaryMinutes <= 0 || request.WeeklyDoubleOvertimeMinutes < 0 || request.WeeklyTripleOvertimeMinutes < 0)
      return WorkforceCommandResult.Fail("Las horas semanales de la politica no son validas.");
    if (request.GraceMinutes is < 0 or > 120 || request.RoundingMinutes is < 1 or > 60)
      return WorkforceCommandResult.Fail("La tolerancia admite hasta 120 minutos y el redondeo debe estar entre 1 y 60 minutos.");

    const string sql =
      """
      IF EXISTS (SELECT 1 FROM rh.AttendancePolicy WHERE Rfc=@Rfc AND Code=@Code AND EffectiveFrom=@EffectiveFrom AND Id<>@Id)
        THROW 51000, 'Ya existe una version de esa politica con la misma fecha de inicio.', 1;
      IF @Id <= 0
      BEGIN
        INSERT INTO rh.AttendancePolicy
          (Rfc, Code, [Name], EffectiveFrom, EffectiveTo, WeeklyOrdinaryMinutes,
           WeeklyDoubleOvertimeMinutes, WeeklyTripleOvertimeMinutes, GraceMinutes,
           RoundingMinutes, LocationRequired, IsActive, RequiresReview, CreatedBy)
        VALUES
          (@Rfc, @Code, @Name, @EffectiveFrom, @EffectiveTo, @WeeklyOrdinaryMinutes,
           @WeeklyDoubleOvertimeMinutes, @WeeklyTripleOvertimeMinutes, @GraceMinutes,
           @RoundingMinutes, @LocationRequired, @IsActive, @RequiresReview, @Actor);
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
            LocationRequired=@LocationRequired, IsActive=@IsActive, RequiresReview=@RequiresReview
        WHERE Id=@Id AND Rfc=@Rfc;
        SELECT CASE WHEN @@ROWCOUNT=0 THEN 0 ELSE @Id END;
      END;
      """;
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction();
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
      request.RequiresReview,
      Actor = NormalizeActor(actor.UserName)
    }, transaction, cancellationToken: ct));
    if (id <= 0)
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail("No se encontro la politica.");
    }
    await WriteAuditAsync(connection, transaction, rfc, null, "AttendancePolicy", id, request.Id <= 0 ? "CREATED" : "UPDATED", request.Name, actor.UserName, ct);
    transaction.Commit();
    return WorkforceCommandResult.Ok(
      request.RequiresReview
        ? "Politica guardada. Queda pendiente de validacion de Capital Humano."
        : "Politica guardada y marcada como validada.",
      id);
  }

  public async Task<WorkforceCommandResult> ApprovePolicyAsync(int policyId, string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin");
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction();
    var policy = await connection.QuerySingleOrDefaultAsync<PolicyReviewRow>(new CommandDefinition(
      "SELECT Id,[Name],RequiresReview FROM rh.AttendancePolicy WITH(UPDLOCK,HOLDLOCK) WHERE Id=@Id AND Rfc=@Rfc;",
      new { Id = policyId, Rfc = normalizedRfc }, transaction, cancellationToken: ct));
    if (policy is null)
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail("No se encontro la politica para el RFC seleccionado.");
    }
    if (!policy.RequiresReview)
    {
      transaction.Rollback();
      return WorkforceCommandResult.Ok($"La politica '{policy.Name}' ya estaba validada.", policyId);
    }
    await connection.ExecuteAsync(new CommandDefinition(
      "UPDATE rh.AttendancePolicy SET RequiresReview=0 WHERE Id=@Id AND Rfc=@Rfc;",
      new { Id = policyId, Rfc = normalizedRfc }, transaction, cancellationToken: ct));
    await WriteAuditAsync(connection, transaction, normalizedRfc, null, "AttendancePolicy", policyId, "REVIEWED", policy.Name, actor.UserName, ct);
    transaction.Commit();
    return WorkforceCommandResult.Ok($"Politica '{policy.Name}' validada por Capital Humano.", policyId);
  }

  public async Task<WorkforceCommandResult> SavePayGroupAsync(PayGroupSaveRequest request, CancellationToken ct = default)
  {
    var rfc = NormalizeRfc(request.Rfc);
    var actor = await RequireActorAsync(rfc, false, ct, "CapitalHumanoAdmin", "CapitalHumanoNomina");
    if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
      return WorkforceCommandResult.Fail("El codigo y nombre del grupo de pago son obligatorios.");
    var frequency = request.Frequency.Trim().ToUpperInvariant();
    if (frequency is not ("WEEKLY" or "BIWEEKLY" or "MONTHLY"))
      return WorkforceCommandResult.Fail("La frecuencia debe ser semanal, quincenal o mensual.");
    const string sql =
      """
      IF EXISTS (SELECT 1 FROM rh.PayGroup WHERE Rfc=@Rfc AND Code=@Code AND Id<>@Id)
        THROW 51000, 'Ya existe un grupo de pago con ese codigo.', 1;
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
    using var transaction = connection.BeginTransaction();
    var id = await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new
    {
      request.Id,
      Rfc = rfc,
      Code = request.Code.Trim().ToUpperInvariant(),
      Name = request.Name.Trim(),
      Frequency = frequency,
      request.IsActive,
      Actor = NormalizeActor(actor.UserName)
    }, transaction, cancellationToken: ct));
    if (id <= 0)
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail("No se encontro el grupo de pago.");
    }
    await WriteAuditAsync(connection, transaction, rfc, null, "PayGroup", id, request.Id <= 0 ? "CREATED" : "UPDATED", request.Name, actor.UserName, ct);
    transaction.Commit();
    return WorkforceCommandResult.Ok("Grupo de pago guardado.", id);
  }

  public async Task<WorkforceCommandResult> DeletePayGroupAsync(int payGroupId, string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin");
    const string sql =
      """
      SELECT pg.Id, pg.[Name],
        (SELECT COUNT(1) FROM rh.EmployeeWorkAssignment wa WHERE wa.PayGroupId = pg.Id) AS AssignmentCount,
        (SELECT COUNT(1) FROM rh.PrenominaPeriod period WHERE period.PayGroupId = pg.Id) AS PeriodCount
      FROM rh.PayGroup pg WITH(UPDLOCK,HOLDLOCK)
      WHERE pg.Id=@Id AND pg.Rfc=@Rfc;
      """;
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction();
    var payGroup = await connection.QuerySingleOrDefaultAsync<PayGroupUsageRow>(new CommandDefinition(
      sql, new { Id = payGroupId, Rfc = normalizedRfc }, transaction, cancellationToken: ct));
    if (payGroup is null)
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail("No se encontro el grupo de pago para el RFC seleccionado.");
    }
    if (payGroup.AssignmentCount > 0 || payGroup.PeriodCount > 0)
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail(
        $"'{payGroup.Name}' tiene {payGroup.AssignmentCount} asignacion(es) y {payGroup.PeriodCount} periodo(s) de pre-nomina. Desactivalo en lugar de eliminarlo.");
    }
    await connection.ExecuteAsync(new CommandDefinition(
      "DELETE FROM rh.PayGroup WHERE Id=@Id AND Rfc=@Rfc;",
      new { Id = payGroupId, Rfc = normalizedRfc }, transaction, cancellationToken: ct));
    await WriteAuditAsync(connection, transaction, normalizedRfc, null, "PayGroup", payGroupId, "DELETED", payGroup.Name, actor.UserName, ct);
    transaction.Commit();
    return WorkforceCommandResult.Ok($"Grupo de pago '{payGroup.Name}' eliminado.", payGroupId);
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

  public async Task<WorkforceCommandResult> DeleteWorkAssignmentAsync(int assignmentId, string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin");
    const string sql =
      """
      SELECT wa.Id, wa.EmployeeId,
        COALESCE(NULLIF(ch.NombreCorto, ''), CONCAT(ch.Nombre, ' ', ch.ApellidoPaterno)) AS EmployeeName,
        (SELECT COUNT(1) FROM rh.TimeEvent e
         WHERE e.Rfc = wa.Rfc AND e.EmployeeId = wa.EmployeeId
           AND e.WorkDate >= wa.EffectiveFrom
           AND (wa.EffectiveTo IS NULL OR e.WorkDate <= wa.EffectiveTo)) AS TimeEventCount
      FROM rh.EmployeeWorkAssignment wa WITH(UPDLOCK,HOLDLOCK)
      INNER JOIN dbo.Capital_Humano ch ON ch.ID = wa.EmployeeId AND ch.RFC = wa.Rfc
      WHERE wa.Id=@Id AND wa.Rfc=@Rfc;
      """;
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction();
    var assignment = await connection.QuerySingleOrDefaultAsync<AssignmentUsageRow>(new CommandDefinition(
      sql, new { Id = assignmentId, Rfc = normalizedRfc }, transaction, cancellationToken: ct));
    if (assignment is null)
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail("No se encontro la asignacion para el RFC seleccionado.");
    }
    if (assignment.TimeEventCount > 0)
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail(
        $"{assignment.EmployeeName} ya tiene {assignment.TimeEventCount} registro(s) de asistencia en esa vigencia. Cierrala con una fecha 'Vigente hasta' en lugar de eliminarla.");
    }
    await connection.ExecuteAsync(new CommandDefinition(
      "DELETE FROM rh.EmployeeWorkAssignment WHERE Id=@Id AND Rfc=@Rfc;",
      new { Id = assignmentId, Rfc = normalizedRfc }, transaction, cancellationToken: ct));
    await WriteAuditAsync(connection, transaction, normalizedRfc, assignment.EmployeeId, "EmployeeWorkAssignment", assignmentId, "DELETED", assignment.EmployeeName, actor.UserName, ct);
    transaction.Commit();
    return WorkforceCommandResult.Ok($"Asignacion de {assignment.EmployeeName} eliminada.", assignmentId);
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

  public async Task<WorkforceCommandResult> DeleteSupervisorAssignmentAsync(int assignmentId, string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin");
    const string sql =
      """
      SELECT sa.Id, sa.EmployeeId,
        COALESCE(NULLIF(employee.NombreCorto, ''), CONCAT(employee.Nombre, ' ', employee.ApellidoPaterno)) AS EmployeeName,
        COALESCE(NULLIF(supervisor.NombreCorto, ''), CONCAT(supervisor.Nombre, ' ', supervisor.ApellidoPaterno)) AS SupervisorName
      FROM rh.SupervisorAssignment sa WITH(UPDLOCK,HOLDLOCK)
      INNER JOIN dbo.Capital_Humano employee ON employee.ID = sa.EmployeeId AND employee.RFC = sa.Rfc
      INNER JOIN dbo.Capital_Humano supervisor ON supervisor.ID = sa.SupervisorEmployeeId AND supervisor.RFC = sa.Rfc
      WHERE sa.Id=@Id AND sa.Rfc=@Rfc;
      """;
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction();
    var relation = await connection.QuerySingleOrDefaultAsync<SupervisorRelationRow>(new CommandDefinition(
      sql, new { Id = assignmentId, Rfc = normalizedRfc }, transaction, cancellationToken: ct));
    if (relation is null)
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail("No se encontro la relacion de supervision para el RFC seleccionado.");
    }
    await connection.ExecuteAsync(new CommandDefinition(
      "DELETE FROM rh.SupervisorAssignment WHERE Id=@Id AND Rfc=@Rfc;",
      new { Id = assignmentId, Rfc = normalizedRfc }, transaction, cancellationToken: ct));
    await WriteAuditAsync(connection, transaction, normalizedRfc, relation.EmployeeId, "SupervisorAssignment", assignmentId, "DELETED", $"{relation.EmployeeName} / {relation.SupervisorName}", actor.UserName, ct);
    transaction.Commit();
    return WorkforceCommandResult.Ok($"Se elimino la supervision de {relation.SupervisorName} sobre {relation.EmployeeName}.", assignmentId);
  }

  public async Task<KioskPairingCodeDto> CreateKioskPairingCodeAsync(KioskPairingCreateRequest request, CancellationToken ct = default)
  {
    var rfc = NormalizeRfc(request.Rfc);
    var actor = await RequireActorAsync(rfc, false, ct, "CapitalHumanoAdmin");
    if (request.DeviceId is null && string.IsNullOrWhiteSpace(request.DeviceName))
      throw new ArgumentException("El nombre del dispositivo es obligatorio.", nameof(request));
    var code = RandomNumberGenerator.GetInt32(10000000, 99999999).ToString(System.Globalization.CultureInfo.InvariantCulture);
    var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code));
    var expires = DateTime.UtcNow.AddMinutes(15);
    const string sql =
      """
      DECLARE @DeviceId int = @ExistingDeviceId;
      IF @DeviceId IS NULL
      BEGIN
        IF NOT EXISTS (SELECT 1 FROM rh.WorkSite WHERE Id=@SiteId AND Rfc=@Rfc AND IsActive=1)
          THROW 51000, 'El sitio no existe o esta inactivo.', 1;
        INSERT INTO rh.KioskDevice (Rfc, SiteId, [Name], CreatedBy)
        VALUES (@Rfc, @SiteId, @Name, @Actor);
        SET @DeviceId = CAST(SCOPE_IDENTITY() AS int);
      END
      ELSE
      BEGIN
        IF NOT EXISTS (SELECT 1 FROM rh.KioskDevice WHERE Id=@DeviceId AND Rfc=@Rfc)
          THROW 51000, 'El kiosco no pertenece al RFC seleccionado.', 1;
        -- Re-pairing invalidates the previous token and any code still pending.
        UPDATE rh.KioskDevice SET DeviceTokenHash=NULL, IsActive=0, PairedAtUtc=NULL WHERE Id=@DeviceId;
        UPDATE rh.KioskPairingCode SET UsedAtUtc=SYSUTCDATETIME()
        WHERE KioskDeviceId=@DeviceId AND UsedAtUtc IS NULL;
      END;
      INSERT INTO rh.KioskPairingCode (KioskDeviceId, CodeHash, ExpiresAtUtc)
      VALUES (@DeviceId, @CodeHash, @ExpiresAtUtc);
      SELECT @DeviceId;
      """;
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction();
    var deviceId = await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new
    {
      Rfc = rfc,
      ExistingDeviceId = request.DeviceId,
      request.SiteId,
      Name = request.DeviceName.Trim(),
      Actor = NormalizeActor(actor.UserName),
      CodeHash = hash,
      ExpiresAtUtc = expires
    }, transaction, cancellationToken: ct));
    await WriteAuditAsync(connection, transaction, rfc, null, "KioskDevice", deviceId,
      request.DeviceId is null ? "PAIRING_REQUESTED" : "PAIRING_REGENERATED", request.DeviceName.Trim(), actor.UserName, ct);
    transaction.Commit();
    return new KioskPairingCodeDto { Code = code, ExpiresAtUtc = expires };
  }

  public async Task<WorkforceCommandResult> SaveKioskDeviceAsync(KioskDeviceSaveRequest request, CancellationToken ct = default)
  {
    var rfc = NormalizeRfc(request.Rfc);
    var actor = await RequireActorAsync(rfc, false, ct, "CapitalHumanoAdmin");
    if (request.Id <= 0) return WorkforceCommandResult.Fail("Genera un codigo de vinculacion para dar de alta un kiosco.");
    if (string.IsNullOrWhiteSpace(request.Name)) return WorkforceCommandResult.Fail("El nombre del dispositivo es obligatorio.");
    const string sql =
      """
      IF NOT EXISTS (SELECT 1 FROM rh.WorkSite WHERE Id=@SiteId AND Rfc=@Rfc)
        THROW 51000, 'El sitio no pertenece al RFC seleccionado.', 1;
      UPDATE rh.KioskDevice SET SiteId=@SiteId, [Name]=@Name, IsActive=@IsActive
      WHERE Id=@Id AND Rfc=@Rfc AND (@IsActive=0 OR DeviceTokenHash IS NOT NULL);
      SELECT @@ROWCOUNT;
      """;
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction();
    var affected = await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new
    {
      request.Id,
      Rfc = rfc,
      request.SiteId,
      Name = request.Name.Trim(),
      request.IsActive
    }, transaction, cancellationToken: ct));
    if (affected == 0)
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail("No se encontro el kiosco, o sigue pendiente de vincular y no puede activarse manualmente.");
    }
    await WriteAuditAsync(connection, transaction, rfc, null, "KioskDevice", request.Id, request.IsActive ? "UPDATED" : "DEACTIVATED", request.Name.Trim(), actor.UserName, ct);
    transaction.Commit();
    return WorkforceCommandResult.Ok("Kiosco actualizado.", request.Id);
  }

  public async Task<WorkforceCommandResult> DeleteKioskDeviceAsync(int deviceId, string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin");
    const string sql =
      """
      SELECT device.Id, device.[Name],
        (SELECT COUNT(1) FROM rh.TimeEvent e WHERE e.KioskDeviceId = device.Id) AS TimeEventCount
      FROM rh.KioskDevice device WITH(UPDLOCK,HOLDLOCK)
      WHERE device.Id=@Id AND device.Rfc=@Rfc;
      """;
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction();
    var device = await connection.QuerySingleOrDefaultAsync<KioskUsageRow>(new CommandDefinition(
      sql, new { Id = deviceId, Rfc = normalizedRfc }, transaction, cancellationToken: ct));
    if (device is null)
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail("No se encontro el kiosco para el RFC seleccionado.");
    }
    if (device.TimeEventCount > 0)
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail(
        $"'{device.Name}' registro {device.TimeEventCount} asistencia(s). Desactivalo en lugar de eliminarlo.");
    }
    await connection.ExecuteAsync(new CommandDefinition(
      "DELETE FROM rh.KioskDevice WHERE Id=@Id AND Rfc=@Rfc;",
      new { Id = deviceId, Rfc = normalizedRfc }, transaction, cancellationToken: ct));
    await WriteAuditAsync(connection, transaction, normalizedRfc, null, "KioskDevice", deviceId, "DELETED", device.Name, actor.UserName, ct);
    transaction.Commit();
    return WorkforceCommandResult.Ok($"Kiosco '{device.Name}' eliminado.", deviceId);
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

  public async Task<WorkforceCommandResult> DeleteHolidayAsync(int holidayId, string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin");
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction();
    var holiday = await connection.QuerySingleOrDefaultAsync<HolidayRow>(new CommandDefinition(
      "SELECT Id,[Name],HolidayDate FROM rh.Holiday WITH(UPDLOCK,HOLDLOCK) WHERE Id=@Id AND Rfc=@Rfc;",
      new { Id = holidayId, Rfc = normalizedRfc }, transaction, cancellationToken: ct));
    if (holiday is null)
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail("No se encontro el dia festivo para el RFC seleccionado.");
    }
    await connection.ExecuteAsync(new CommandDefinition(
      "DELETE FROM rh.Holiday WHERE Id=@Id AND Rfc=@Rfc;",
      new { Id = holidayId, Rfc = normalizedRfc }, transaction, cancellationToken: ct));
    await WriteAuditAsync(connection, transaction, normalizedRfc, null, "Holiday", holidayId, "DELETED", $"{holiday.Name} ({holiday.HolidayDate:yyyy-MM-dd})", actor.UserName, ct);
    transaction.Commit();
    return WorkforceCommandResult.Ok($"Dia festivo '{holiday.Name}' eliminado.", holidayId);
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
    var pin = request.Pin?.Trim() ?? string.Empty;
    if (pin.Length is < 4 or > 12 || pin.Any(character => !char.IsDigit(character)))
      return new KioskCredentialResult { Message = "El PIN debe tener entre 4 y 12 digitos numericos." };
    if (pin.Distinct().Count() == 1)
      return new KioskCredentialResult { Message = "El PIN no puede repetir el mismo digito." };

    var badgeBytes = RandomNumberGenerator.GetBytes(24);
    var badgeToken = Convert.ToHexString(badgeBytes);
    var badgeHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(badgeToken));
    var pinHash = _passwordHasher.HashPassword(new object(), pin);
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
    using var transaction = connection.BeginTransaction();
    await connection.ExecuteAsync(new CommandDefinition(sql, new
    {
      Rfc = rfc,
      request.EmployeeId,
      BadgeTokenHash = badgeHash,
      PinHash = pinHash,
      Actor = NormalizeActor(actor.UserName)
    }, transaction, cancellationToken: ct));
    await WriteAuditAsync(connection, transaction, rfc, request.EmployeeId, "EmployeeKioskCredential", request.EmployeeId, "ROTATED", null, actor.UserName, ct);
    transaction.Commit();
    return new KioskCredentialResult
    {
      Success = true,
      Message = "Credencial creada. El codigo de gafete se muestra una sola vez.",
      BadgeToken = badgeToken
    };
  }

  public async Task<WorkforceCommandResult> RevokeKioskCredentialAsync(int employeeId, string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin");
    using var connection = CreateOpenConnection();
    using var transaction = connection.BeginTransaction();
    var affected = await connection.ExecuteAsync(new CommandDefinition(
      "UPDATE rh.EmployeeKioskCredential SET IsActive=0, FailedAttempts=0, LockedUntilUtc=NULL WHERE Rfc=@Rfc AND EmployeeId=@EmployeeId AND IsActive=1;",
      new { Rfc = normalizedRfc, EmployeeId = employeeId }, transaction, cancellationToken: ct));
    if (affected == 0)
    {
      transaction.Rollback();
      return WorkforceCommandResult.Fail("El empleado no tiene una credencial activa.");
    }
    await WriteAuditAsync(connection, transaction, normalizedRfc, employeeId, "EmployeeKioskCredential", employeeId, "REVOKED", null, actor.UserName, ct);
    transaction.Commit();
    return WorkforceCommandResult.Ok("Credencial revocada. El gafete y el PIN dejan de funcionar.", employeeId);
  }

  private static TimeZoneInfo ResolveTimeZone(string id)
  {
    try { return TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); }
    catch (TimeZoneNotFoundException) { throw new ArgumentException("La zona horaria no existe.", nameof(id)); }
    catch (InvalidTimeZoneException) { throw new ArgumentException("La zona horaria no es valida.", nameof(id)); }
  }

  private sealed class SiteUsageRow
  {
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int AssignmentCount { get; set; }
    public int KioskCount { get; set; }
    public int HolidayCount { get; set; }
    public int TimeEventCount { get; set; }
  }
  private sealed class ScheduleUsageRow
  {
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int AssignmentCount { get; set; }
    public int AttendanceDayCount { get; set; }
  }
  private sealed class KioskUsageRow
  {
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int TimeEventCount { get; set; }
  }
  private sealed class HolidayRow
  {
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateOnly HolidayDate { get; set; }
  }
  private sealed class AssignmentUsageRow
  {
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public int TimeEventCount { get; set; }
  }
  private sealed class SupervisorRelationRow
  {
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string SupervisorName { get; set; } = string.Empty;
  }
  private sealed class PolicyReviewRow
  {
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool RequiresReview { get; set; }
  }
  private sealed class PayGroupUsageRow
  {
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int AssignmentCount { get; set; }
    public int PeriodCount { get; set; }
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
