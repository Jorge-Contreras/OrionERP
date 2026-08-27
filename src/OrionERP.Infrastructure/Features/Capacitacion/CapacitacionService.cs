using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.SqlClient;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Capacitacion;
using OrionERP.Application.Features.CapitalHumano.Workforce;

namespace OrionERP.Infrastructure.Features.Capacitacion;

public sealed partial class CapacitacionService : ICapacitacionService
{
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly ICurrentEmployeeAccessor _currentEmployeeAccessor;

  public CapacitacionService(IDbConnectionFactory connectionFactory, ICurrentEmployeeAccessor currentEmployeeAccessor)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    _currentEmployeeAccessor = currentEmployeeAccessor ?? throw new ArgumentNullException(nameof(currentEmployeeAccessor));
  }

  public async Task<CapacitacionDashboardDto> GetDashboardAsync(CapacitacionActorContext context, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(context);
    var rfc = RequireRfc(context.Rfc);
    var employeeId = RequireEmployeeId(context.EmployeeId, nameof(context.EmployeeId));
    var current = await RequireCurrentAsync(rfc, requireEmployee: true, ct);
    RequireSelf(current, employeeId);

    const string sql =
      """
      SELECT
        COUNT(CASE WHEN a.Estado = 'ASIGNADA' THEN 1 END) AS Pendientes,
        COUNT(CASE WHEN a.Estado IN ('EN_CURSO','ESPERA_FIRMA','ESPERA_ACUSE') THEN 1 END) AS EnCurso,
        COUNT(CASE WHEN a.Estado = 'COMPLETADA' THEN 1 END) AS Completadas,
        COUNT(CASE WHEN a.FechaLimite < SYSUTCDATETIME() AND a.Estado NOT IN ('COMPLETADA','CANCELADA') THEN 1 END) AS Vencidas,
        CAST(ISNULL(AVG(CASE WHEN a.Estado <> 'CANCELADA' THEN a.Porcentaje END), 0) AS decimal(5,2)) AS ProgresoPromedio
      FROM capacitacion.Asignacion a
      WHERE a.Rfc = @Rfc AND a.EmployeeId = @EmployeeId;

      SELECT COUNT(1)
      FROM capacitacion.Sesion s
      WHERE s.Rfc = @Rfc
        AND s.Estado IN ('PROGRAMADA','EN_CURSO')
        AND (s.InstructorEmployeeId = @EmployeeId OR EXISTS
        (
          SELECT 1 FROM capacitacion.SesionParticipante sp
          WHERE sp.SesionId = s.SesionId AND sp.EmployeeId = @EmployeeId
        ));

      """ + AssignmentSelectSql +
      """
      WHERE a.Rfc = @Rfc AND a.EmployeeId = @EmployeeId AND a.Estado <> 'CANCELADA'
      ORDER BY
        CASE a.Estado WHEN 'EN_CURSO' THEN 0 WHEN 'ESPERA_FIRMA' THEN 1 WHEN 'ESPERA_ACUSE' THEN 2 WHEN 'ASIGNADA' THEN 3 ELSE 4 END,
        CASE WHEN a.FechaLimite IS NULL THEN 1 ELSE 0 END,
        a.FechaLimite,
        a.AsignadaEn DESC;

      """ + SessionSelectSql +
      """
      WHERE s.Rfc = @Rfc
        AND s.Estado IN ('PROGRAMADA','EN_CURSO')
        AND (s.InstructorEmployeeId = @EmployeeId OR EXISTS
        (
          SELECT 1 FROM capacitacion.SesionParticipante mine
          WHERE mine.SesionId = s.SesionId AND mine.EmployeeId = @EmployeeId
        ))
      ORDER BY CASE s.Estado WHEN 'EN_CURSO' THEN 0 ELSE 1 END, s.ProgramadaEn;
      """;

    using var conn = CreateConnection();
    using var multi = await conn.QueryMultipleAsync(
      new CommandDefinition(sql, new { Rfc = rfc, EmployeeId = employeeId }, cancellationToken: ct));

    var dashboard = await multi.ReadSingleAsync<CapacitacionDashboardDto>();
    dashboard.SesionesActivas = await multi.ReadSingleAsync<int>();
    dashboard.MisAsignaciones = (await multi.ReadAsync<CapacitacionAsignacionDto>()).AsList();
    dashboard.Sesiones = (await multi.ReadAsync<CapacitacionSesionResumenDto>()).AsList();
    return dashboard;
  }

  public async Task<IReadOnlyList<CapacitacionCursoResumenDto>> GetCatalogoAsync(string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = RequireRfc(rfc);
    _ = await RequireCurrentAsync(normalizedRfc, requireEmployee: false, ct);
    const string sql =
      """
      WITH candidatos AS
      (
        SELECT
          c.CursoId,
          cv.CursoVersionId,
          cv.NumeroVersion,
          c.Clave,
          c.Categoria,
          c.Nombre,
          c.Descripcion,
          cv.Objetivos,
          cv.Prerequisitos,
          c.DuracionMinutos,
          cv.CalificacionMinima,
          cv.Estado AS EstadoVersion,
          ROW_NUMBER() OVER (PARTITION BY c.Clave ORDER BY CASE WHEN c.Rfc = @Rfc THEN 0 ELSE 1 END, cv.NumeroVersion DESC) AS Preferencia
        FROM capacitacion.Curso c
        JOIN capacitacion.CursoVersion cv ON cv.CursoId = c.CursoId AND cv.Estado = 'PUBLICADA'
        WHERE c.Activo = 1 AND c.Rfc IN (@Rfc, '*')
      )
      SELECT
        item.CursoId,
        item.CursoVersionId,
        item.NumeroVersion,
        item.Clave,
        item.Categoria,
        item.Nombre,
        item.Descripcion,
        item.Objetivos,
        item.Prerequisitos,
        item.DuracionMinutos,
        item.CalificacionMinima,
        item.EstadoVersion,
        (SELECT COUNT(1) FROM capacitacion.Leccion l WHERE l.CursoVersionId = item.CursoVersionId) AS LeccionCount,
        (SELECT COUNT(1) FROM capacitacion.Leccion l JOIN capacitacion.BloqueContenido b ON b.LeccionId = l.LeccionId WHERE l.CursoVersionId = item.CursoVersionId) AS BloqueCount
      FROM candidatos item
      WHERE item.Preferencia = 1
      ORDER BY item.Categoria, item.Nombre;
      """;

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<CapacitacionCursoResumenDto>(
      new CommandDefinition(sql, new { Rfc = normalizedRfc }, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<CapacitacionCursoDetalleDto?> GetCursoAsync(int cursoVersionId, string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = RequireRfc(rfc);
    _ = await RequireCurrentAsync(normalizedRfc, requireEmployee: false, ct);
    if (cursoVersionId <= 0)
      return null;

    using var conn = CreateConnection();
    return await LoadCourseAsync(conn, null, cursoVersionId, normalizedRfc, allowPinned: false, ct: ct);
  }

  public async Task<CapacitacionCursoDetalleDto?> GetCursoAsignadoAsync(
    long asignacionId,
    string rfc,
    int employeeId,
    CancellationToken ct = default)
  {
    var normalizedRfc = RequireRfc(rfc);
    var normalizedEmployeeId = RequireEmployeeId(employeeId, nameof(employeeId));
    var current = await RequireCurrentAsync(normalizedRfc, requireEmployee: true, ct);
    RequireSelf(current, normalizedEmployeeId);
    if (asignacionId <= 0)
      return null;

    using var conn = CreateConnection();
    var courseVersionId = await conn.QueryFirstOrDefaultAsync<int?>(new CommandDefinition(
      """
      SELECT CursoVersionId
      FROM capacitacion.Asignacion
      WHERE AsignacionId = @AsignacionId AND Rfc = @Rfc AND EmployeeId = @EmployeeId
        AND Estado <> 'CANCELADA';
      """,
      new { AsignacionId = asignacionId, Rfc = normalizedRfc, EmployeeId = normalizedEmployeeId },
      cancellationToken: ct));
    if (!courseVersionId.HasValue)
      return null;

    return await LoadCourseAsync(conn, null, courseVersionId.Value, normalizedRfc, allowPinned: true, ct: ct);
  }

  public async Task<IReadOnlyList<CapacitacionAsignacionDto>> GetMiPlanAsync(string rfc, int employeeId, CancellationToken ct = default)
  {
    var normalizedRfc = RequireRfc(rfc);
    var normalizedEmployeeId = RequireEmployeeId(employeeId, nameof(employeeId));
    var current = await RequireCurrentAsync(normalizedRfc, requireEmployee: true, ct);
    RequireSelf(current, normalizedEmployeeId);
    var sql = AssignmentSelectSql +
      """
      WHERE a.Rfc = @Rfc AND a.EmployeeId = @EmployeeId AND a.Estado <> 'CANCELADA'
      ORDER BY
        CASE a.Estado WHEN 'EN_CURSO' THEN 0 WHEN 'ESPERA_FIRMA' THEN 1 WHEN 'ESPERA_ACUSE' THEN 2 WHEN 'ASIGNADA' THEN 3 ELSE 4 END,
        CASE WHEN a.FechaLimite IS NULL THEN 1 ELSE 0 END,
        a.FechaLimite,
        a.AsignadaEn DESC;
      """;

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<CapacitacionAsignacionDto>(new CommandDefinition(
      sql,
      new { Rfc = normalizedRfc, EmployeeId = normalizedEmployeeId },
      cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<IReadOnlyList<CapacitacionEmpleadoDto>> GetEmpleadosAsignablesAsync(string rfc, string? search = null, CancellationToken ct = default)
  {
    var normalizedRfc = RequireRfc(rfc);
    _ = await RequireCurrentAsync(normalizedRfc, requireEmployee: true, ct, CapacitacionCodes.RoleAdmin, CapacitacionCodes.RoleInstructor);
    const string sql =
      """
      SELECT TOP (200)
        ch.ID AS EmployeeId,
        COALESCE(NULLIF(LTRIM(RTRIM(ch.NombreCorto)), ''), NULLIF(LTRIM(RTRIM(CONCAT(ch.Nombre, ' ', ch.ApellidoPaterno, ' ', ch.ApellidoMaterno))), ''), CONCAT('Colaborador ', ch.ID)) AS Nombre,
        ch.Puesto,
        authInfo.Email,
        CAST(CASE WHEN authInfo.AuthCount > 0 THEN 1 ELSE 0 END AS bit) AS TieneUsuario
      FROM dbo.Capital_Humano ch
      OUTER APPLY
      (
        SELECT COUNT(1) AS AuthCount, MAX(authUser.Email) AS Email
        FROM auth.AspNetUserCompanies membership
        JOIN auth.AspNetUsers authUser ON authUser.Id=membership.UserId
        WHERE membership.EmployeeId = ch.ID AND membership.Rfc = ch.RFC AND membership.IsActive = 1
      ) authInfo
      WHERE ch.RFC = @Rfc
        AND UPPER(LTRIM(RTRIM(ISNULL(ch.[Status], '')))) = 'ACTIVO'
        AND (@Search IS NULL OR
          ch.NombreCorto LIKE @SearchLike OR ch.Nombre LIKE @SearchLike OR
          ch.ApellidoPaterno LIKE @SearchLike OR ch.ApellidoMaterno LIKE @SearchLike OR
          ch.Puesto LIKE @SearchLike OR authInfo.Email LIKE @SearchLike)
      ORDER BY Nombre, ch.ID;
      """;

    var normalizedSearch = NullIfWhiteSpace(search);
    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<CapacitacionEmpleadoDto>(new CommandDefinition(
      sql,
      new { Rfc = normalizedRfc, Search = normalizedSearch, SearchLike = normalizedSearch is null ? null : $"%{normalizedSearch}%" },
      cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<CapacitacionCommandResult> CrearAsignacionesAsync(CapacitacionCrearAsignacionesRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = RequireRfc(request.Rfc);
    var actorEmployeeId = RequireEmployeeId(request.ActorEmployeeId, nameof(request.ActorEmployeeId));
    var current = await RequireCurrentAsync(rfc, requireEmployee: true, ct, CapacitacionCodes.RoleAdmin, CapacitacionCodes.RoleInstructor);
    RequireSelf(current, actorEmployeeId);
    var actor = RequireActor(current.UserName);
    if (!current.IsInRole(CapacitacionCodes.RoleAdmin)
      && request.InstructorEmployeeId.HasValue
      && request.InstructorEmployeeId.Value != actorEmployeeId)
      throw new UnauthorizedAccessException("Un instructor solo puede crear asignaciones bajo su propia responsabilidad.");
    var assignedInstructorEmployeeId = current.IsInRole(CapacitacionCodes.RoleAdmin)
      ? request.InstructorEmployeeId
      : actorEmployeeId;
    var employeeIds = request.EmployeeIds.Where(id => id > 0).Distinct().ToArray();
    if (request.CursoVersionId <= 0)
      return CapacitacionCommandResult.Fail("Selecciona un curso publicado.");
    if (employeeIds.Length == 0)
      return CapacitacionCommandResult.Fail("Selecciona al menos un colaborador.");
    if (request.FechaLimite.HasValue && request.FechaLimite.Value <= DateTime.UtcNow)
      return CapacitacionCommandResult.Fail("La fecha límite debe ser futura.");

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      if (!await PublishedCourseExistsAsync(conn, tx, request.CursoVersionId, rfc, ct))
      {
        await tx.RollbackAsync(ct);
        return CapacitacionCommandResult.Fail("El curso no existe, no está publicado o no pertenece al RFC activo.");
      }

      var employeesToValidate = employeeIds
        .Append(actorEmployeeId)
        .Concat(assignedInstructorEmployeeId is > 0 ? [assignedInstructorEmployeeId.Value] : Array.Empty<int>())
        .Distinct()
        .ToArray();
      if (!await EmployeesBelongToRfcAsync(conn, tx, rfc, employeesToValidate, requireActive: true, ct))
      {
        await tx.RollbackAsync(ct);
        return CapacitacionCommandResult.Fail("Uno o más colaboradores no existen, no están activos o no pertenecen al RFC activo.");
      }

      long? firstId = null;
      var created = 0;
      foreach (var employeeId in employeeIds)
      {
        var existing = await conn.QueryFirstOrDefaultAsync<long?>(new CommandDefinition(
          """
          SELECT TOP (1) AsignacionId
          FROM capacitacion.Asignacion WITH (UPDLOCK, HOLDLOCK)
          WHERE Rfc = @Rfc AND EmployeeId = @EmployeeId AND CursoVersionId = @CursoVersionId AND Estado <> 'CANCELADA';
          """,
          new { Rfc = rfc, EmployeeId = employeeId, request.CursoVersionId },
          tx,
          cancellationToken: ct));
        if (existing.HasValue)
          continue;

        var assignmentId = await conn.QuerySingleAsync<long>(new CommandDefinition(
          """
          INSERT INTO capacitacion.Asignacion
            (Rfc, EmployeeId, CursoVersionId, InstructorEmployeeId, Estado, Porcentaje, FechaLimite, AsignadaPorEmployeeId, AsignadaPor)
          OUTPUT inserted.AsignacionId
          VALUES
            (@Rfc, @EmployeeId, @CursoVersionId, @InstructorEmployeeId, 'ASIGNADA', 0, @FechaLimite, @ActorEmployeeId, @Actor);
          """,
          new
          {
            Rfc = rfc,
            EmployeeId = employeeId,
            request.CursoVersionId,
            InstructorEmployeeId = assignedInstructorEmployeeId,
            request.FechaLimite,
            ActorEmployeeId = actorEmployeeId,
            Actor = actor
          },
          tx,
          cancellationToken: ct));
        firstId ??= assignmentId;
        created++;
        await AddAuditAsync(conn, tx, rfc, "ASIGNACION", assignmentId, "ASIGNADA", $"Curso versión {request.CursoVersionId} asignado al colaborador {employeeId}.", actorEmployeeId, actor, ct);
      }

      await tx.CommitAsync(ct);
      return created == 0
        ? CapacitacionCommandResult.Ok("Los colaboradores seleccionados ya tenían esta capacitación activa.", affectedCount: 0)
        : CapacitacionCommandResult.Ok($"Se crearon {created} asignación(es) de capacitación.", firstId, created);
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
      await tx.RollbackAsync(ct);
      return CapacitacionCommandResult.Fail("La capacitación ya fue asignada a uno de los colaboradores.");
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<CapacitacionCommandResult> CrearSesionAsync(CapacitacionCrearSesionRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = RequireRfc(request.Rfc);
    var actorEmployeeId = RequireEmployeeId(request.ActorEmployeeId, nameof(request.ActorEmployeeId));
    var instructorEmployeeId = RequireEmployeeId(request.InstructorEmployeeId, nameof(request.InstructorEmployeeId));
    var current = await RequireCurrentAsync(rfc, requireEmployee: true, ct, CapacitacionCodes.RoleAdmin, CapacitacionCodes.RoleInstructor);
    RequireSelf(current, actorEmployeeId);
    if (!current.IsInRole(CapacitacionCodes.RoleAdmin) && instructorEmployeeId != actorEmployeeId)
      throw new UnauthorizedAccessException("Un instructor solo puede crear sesiones que él mismo impartirá.");
    var actor = RequireActor(current.UserName);
    var participantIds = request.ParticipantEmployeeIds.Where(id => id > 0 && id != instructorEmployeeId).Distinct().ToArray();
    var sessionName = NullIfWhiteSpace(request.Nombre);
    if (request.CursoVersionId <= 0)
      return CapacitacionCommandResult.Fail("Selecciona un curso publicado.");
    if (sessionName is null)
      return CapacitacionCommandResult.Fail("Indica un nombre para la sesión.");
    if (participantIds.Length == 0)
      return CapacitacionCommandResult.Fail("Selecciona al menos un colaborador para la sesión.");

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      if (!await PublishedCourseExistsAsync(conn, tx, request.CursoVersionId, rfc, ct))
      {
        await tx.RollbackAsync(ct);
        return CapacitacionCommandResult.Fail("El curso no existe, no está publicado o no pertenece al RFC activo.");
      }

      var employeesToValidate = participantIds.Append(instructorEmployeeId).Append(actorEmployeeId).Distinct().ToArray();
      if (!await EmployeesBelongToRfcAsync(conn, tx, rfc, employeesToValidate, requireActive: true, ct))
      {
        await tx.RollbackAsync(ct);
        return CapacitacionCommandResult.Fail("El instructor o uno de los participantes no pertenece al RFC activo.");
      }

      if (!current.IsInRole(CapacitacionCodes.RoleAdmin))
      {
        var assignmentsOwnedByAnotherInstructor = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
          """
          SELECT COUNT(1)
          FROM capacitacion.Asignacion WITH (UPDLOCK, HOLDLOCK)
          WHERE Rfc = @Rfc AND CursoVersionId = @CursoVersionId AND EmployeeId IN @EmployeeIds
            AND Estado <> 'CANCELADA' AND InstructorEmployeeId IS NOT NULL AND InstructorEmployeeId <> @InstructorEmployeeId;
          """,
          new
          {
            Rfc = rfc,
            request.CursoVersionId,
            EmployeeIds = participantIds,
            InstructorEmployeeId = instructorEmployeeId
          },
          tx,
          cancellationToken: ct));
        if (assignmentsOwnedByAnotherInstructor > 0)
        {
          await tx.RollbackAsync(ct);
          return CapacitacionCommandResult.Fail("Uno de los colaboradores ya está asignado a otro instructor para este curso.");
        }
      }

      var firstBlockId = await conn.QueryFirstOrDefaultAsync<int?>(new CommandDefinition(
        """
        SELECT TOP (1) b.BloqueId
        FROM capacitacion.Leccion l
        JOIN capacitacion.BloqueContenido b ON b.LeccionId = l.LeccionId
        WHERE l.CursoVersionId = @CursoVersionId
        ORDER BY l.Orden, b.Orden;
        """,
        new { request.CursoVersionId },
        tx,
        cancellationToken: ct));
      if (!firstBlockId.HasValue)
      {
        await tx.RollbackAsync(ct);
        return CapacitacionCommandResult.Fail("El curso publicado no contiene bloques de capacitación.");
      }

      var accessCode = CreateAccessCode();
      var sessionId = await conn.QuerySingleAsync<long>(new CommandDefinition(
        """
        INSERT INTO capacitacion.Sesion
          (Rfc, CursoVersionId, Nombre, CodigoAcceso, Estado, InstructorEmployeeId, BloqueActualId, ProgramadaEn, CreadaPorEmployeeId, CreadaPor)
        OUTPUT inserted.SesionId
        VALUES
          (@Rfc, @CursoVersionId, @Nombre, @CodigoAcceso, 'PROGRAMADA', @InstructorEmployeeId, @BloqueActualId, @ProgramadaEn, @ActorEmployeeId, @Actor);
        """,
        new
        {
          Rfc = rfc,
          request.CursoVersionId,
          Nombre = Truncate(sessionName, 160),
          CodigoAcceso = accessCode,
          InstructorEmployeeId = instructorEmployeeId,
          BloqueActualId = firstBlockId.Value,
          ProgramadaEn = request.ProgramadaEn ?? DateTime.UtcNow,
          ActorEmployeeId = actorEmployeeId,
          Actor = actor
        },
        tx,
        cancellationToken: ct));

      await conn.ExecuteAsync(new CommandDefinition(
        """
        INSERT INTO capacitacion.SesionParticipante (SesionId, EmployeeId, AsignacionId, Rol, UnidoEn)
        VALUES (@SesionId, @EmployeeId, NULL, 'INSTRUCTOR', SYSUTCDATETIME());
        """,
        new { SesionId = sessionId, EmployeeId = instructorEmployeeId },
        tx,
        cancellationToken: ct));

      foreach (var participantId in participantIds)
      {
        var assignmentId = await GetOrCreateAssignmentAsync(
          conn, tx, rfc, request.CursoVersionId, participantId, instructorEmployeeId, actorEmployeeId, actor, ct);
        await conn.ExecuteAsync(new CommandDefinition(
          """
          INSERT INTO capacitacion.SesionParticipante (SesionId, EmployeeId, AsignacionId, Rol)
          VALUES (@SesionId, @EmployeeId, @AsignacionId, 'COLABORADOR');
          """,
          new { SesionId = sessionId, EmployeeId = participantId, AsignacionId = assignmentId },
          tx,
          cancellationToken: ct));
      }

      await AddAuditAsync(conn, tx, rfc, "SESION", sessionId, "CREADA", $"Sesión {accessCode} creada con {participantIds.Length} participante(s).", actorEmployeeId, actor, ct);
      await tx.CommitAsync(ct);
      return CapacitacionCommandResult.Ok($"Sesión creada. Código de acceso: {accessCode}.", sessionId, participantIds.Length);
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
      await tx.RollbackAsync(ct);
      return CapacitacionCommandResult.Fail("No fue posible reservar el código de sesión. Intenta de nuevo.");
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<CapacitacionSesionDto?> GetSesionAsync(long sesionId, string rfc, int actorEmployeeId, CancellationToken ct = default)
  {
    if (sesionId <= 0)
      return null;

    var normalizedRfc = RequireRfc(rfc);
    var normalizedActorEmployeeId = RequireEmployeeId(actorEmployeeId, nameof(actorEmployeeId));
    var current = await RequireCurrentAsync(normalizedRfc, requireEmployee: true, ct);
    RequireSelf(current, normalizedActorEmployeeId);
    var sql = SessionSelectSql +
      """
      WHERE s.SesionId = @SesionId AND s.Rfc = @Rfc
        AND (s.InstructorEmployeeId = @ActorEmployeeId OR EXISTS
        (
          SELECT 1 FROM capacitacion.SesionParticipante accessInfo
          WHERE accessInfo.SesionId = s.SesionId AND accessInfo.EmployeeId = @ActorEmployeeId
        ));

      SELECT
        sp.EmployeeId,
        COALESCE(NULLIF(LTRIM(RTRIM(ch.NombreCorto)), ''), CONCAT(ch.Nombre, ' ', ch.ApellidoPaterno)) AS Nombre,
        sp.Rol,
        sp.AsignacionId,
        sp.UnidoEn,
        ISNULL(a.Porcentaje, 0) AS Porcentaje,
        ISNULL(a.Estado, '') AS EstadoAsignacion,
        CAST(CASE WHEN EXISTS
        (
          SELECT 1
          FROM capacitacion.ProgresoBloque currentProgress
          WHERE currentProgress.AsignacionId = sp.AsignacionId
            AND currentProgress.EmployeeId = sp.EmployeeId
            AND currentProgress.BloqueId = currentSession.BloqueActualId
            AND currentProgress.Estado = 'COMPLETADO'
        ) THEN 1 ELSE 0 END AS bit) AS BloqueActualCompletado
      FROM capacitacion.SesionParticipante sp
      JOIN capacitacion.Sesion currentSession ON currentSession.SesionId = sp.SesionId AND currentSession.Rfc = @Rfc
      JOIN dbo.Capital_Humano ch ON ch.ID = sp.EmployeeId
      LEFT JOIN capacitacion.Asignacion a ON a.AsignacionId = sp.AsignacionId AND a.Rfc = @Rfc
      WHERE sp.SesionId = @SesionId
      ORDER BY CASE sp.Rol WHEN 'INSTRUCTOR' THEN 0 ELSE 1 END, Nombre;
      """;

    int courseVersionId;
    CapacitacionSesionDto session;
    using (var conn = CreateConnection())
    {
      using var multi = await conn.QueryMultipleAsync(new CommandDefinition(
        sql,
        new { SesionId = sesionId, Rfc = normalizedRfc, ActorEmployeeId = normalizedActorEmployeeId },
        cancellationToken: ct));
      var summary = await multi.ReadFirstOrDefaultAsync<CapacitacionSesionResumenDto>();
      if (summary is null)
        return null;

      courseVersionId = summary.CursoVersionId;
      session = CopySession(summary);
      session.Participantes = (await multi.ReadAsync<CapacitacionSesionParticipanteDto>()).AsList();
    }

    using var courseConnection = CreateConnection();
    var course = await LoadCourseAsync(courseConnection, null, courseVersionId, normalizedRfc, allowPinned: true, ct: ct);
    if (course is null)
      return null;

    session.Curso = course;
    session.BloqueActual = course.Lecciones.SelectMany(item => item.Bloques).FirstOrDefault(item => item.BloqueId == session.BloqueActualId);
    return session;
  }

  public async Task<CapacitacionCommandResult> AvanzarSesionAsync(CapacitacionAvanzarSesionRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = RequireRfc(request.Rfc);
    var actorEmployeeId = RequireEmployeeId(request.ActorEmployeeId, nameof(request.ActorEmployeeId));
    var current = await RequireCurrentAsync(rfc, requireEmployee: true, ct, CapacitacionCodes.RoleAdmin, CapacitacionCodes.RoleInstructor);
    RequireSelf(current, actorEmployeeId);
    var actor = RequireActor(current.UserName);
    if (request.SesionId <= 0)
      return CapacitacionCommandResult.Fail("La sesión no es válida.");

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var session = await conn.QueryFirstOrDefaultAsync<SessionStateRow>(new CommandDefinition(
        """
        SELECT SesionId, CursoVersionId, InstructorEmployeeId, BloqueActualId, Estado
        FROM capacitacion.Sesion WITH (UPDLOCK, HOLDLOCK)
        WHERE SesionId = @SesionId AND Rfc = @Rfc;
        """,
        new { request.SesionId, Rfc = rfc },
        tx,
        cancellationToken: ct));
      if (session is null)
      {
        await tx.RollbackAsync(ct);
        return CapacitacionCommandResult.Fail("No se encontró la sesión en el RFC activo.");
      }
      if (session.InstructorEmployeeId != actorEmployeeId)
      {
        await tx.RollbackAsync(ct);
        return CapacitacionCommandResult.Fail("Solo el instructor de la sesión puede cambiar el paso presentado.");
      }
      if (session.Estado is CapacitacionCodes.SesionFinalizada or CapacitacionCodes.SesionCancelada)
      {
        await tx.RollbackAsync(ct);
        return CapacitacionCommandResult.Fail("La sesión ya está cerrada.", session.SesionId);
      }

      if (request.Finalizar)
      {
        await conn.ExecuteAsync(new CommandDefinition(
          """
          UPDATE capacitacion.Sesion
          SET Estado = 'FINALIZADA', FinalizadaEn = SYSUTCDATETIME(), IniciadaEn = ISNULL(IniciadaEn, SYSUTCDATETIME())
          WHERE SesionId = @SesionId AND Rfc = @Rfc;
          """,
          new { request.SesionId, Rfc = rfc },
          tx,
          cancellationToken: ct));
        await AddAuditAsync(conn, tx, rfc, "SESION", request.SesionId, "FINALIZADA", "El instructor cerró la sesión.", actorEmployeeId, actor, ct);
        await tx.CommitAsync(ct);
        return CapacitacionCommandResult.Ok("La sesión finalizó. Cada colaborador debe completar su evaluación, práctica, firma y acuse.", request.SesionId);
      }

      int? targetBlockId = request.BloqueId;
      if (targetBlockId.HasValue)
      {
        var belongs = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
          """
          SELECT COUNT(1)
          FROM capacitacion.Leccion l
          JOIN capacitacion.BloqueContenido b ON b.LeccionId = l.LeccionId
          WHERE l.CursoVersionId = @CursoVersionId AND b.BloqueId = @BloqueId;
          """,
          new { session.CursoVersionId, BloqueId = targetBlockId.Value },
          tx,
          cancellationToken: ct));
        if (belongs != 1)
        {
          await tx.RollbackAsync(ct);
          return CapacitacionCommandResult.Fail("El bloque seleccionado no pertenece al curso de la sesión.");
        }
      }
      else
      {
        targetBlockId = await conn.QueryFirstOrDefaultAsync<int?>(new CommandDefinition(
          """
          SELECT TOP (1) candidate.BloqueId
          FROM capacitacion.Leccion candidateLesson
          JOIN capacitacion.BloqueContenido candidate ON candidate.LeccionId = candidateLesson.LeccionId
          LEFT JOIN capacitacion.BloqueContenido currentBlock ON currentBlock.BloqueId = @BloqueActualId
          LEFT JOIN capacitacion.Leccion currentLesson ON currentLesson.LeccionId = currentBlock.LeccionId
          WHERE candidateLesson.CursoVersionId = @CursoVersionId
            AND (@BloqueActualId IS NULL OR candidateLesson.Orden > currentLesson.Orden OR
              (candidateLesson.Orden = currentLesson.Orden AND candidate.Orden > currentBlock.Orden))
          ORDER BY candidateLesson.Orden, candidate.Orden;
          """,
          new { session.CursoVersionId, session.BloqueActualId },
          tx,
          cancellationToken: ct));
      }

      if (!targetBlockId.HasValue)
      {
        await conn.ExecuteAsync(new CommandDefinition(
          """
          UPDATE capacitacion.Sesion
          SET Estado = 'FINALIZADA', FinalizadaEn = SYSUTCDATETIME(), IniciadaEn = ISNULL(IniciadaEn, SYSUTCDATETIME())
          WHERE SesionId = @SesionId AND Rfc = @Rfc;
          """,
          new { request.SesionId, Rfc = rfc },
          tx,
          cancellationToken: ct));
        await AddAuditAsync(conn, tx, rfc, "SESION", request.SesionId, "FINALIZADA", "Se presentó el último bloque de contenido.", actorEmployeeId, actor, ct);
        await tx.CommitAsync(ct);
        return CapacitacionCommandResult.Ok("No hay más bloques; la sesión quedó finalizada.", request.SesionId);
      }

      await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE capacitacion.Sesion
        SET Estado = 'EN_CURSO', BloqueActualId = @BloqueId, IniciadaEn = ISNULL(IniciadaEn, SYSUTCDATETIME())
        WHERE SesionId = @SesionId AND Rfc = @Rfc;
        """,
        new { request.SesionId, Rfc = rfc, BloqueId = targetBlockId.Value },
        tx,
        cancellationToken: ct));
      await AddAuditAsync(conn, tx, rfc, "SESION", request.SesionId, "BLOQUE_CAMBIADO", $"Bloque actual: {targetBlockId.Value}.", actorEmployeeId, actor, ct);
      await tx.CommitAsync(ct);
      return CapacitacionCommandResult.Ok("El contenido de la sesión se actualizó.", request.SesionId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<CapacitacionCommandResult> RegistrarProgresoBloqueAsync(CapacitacionRegistrarBloqueRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = RequireRfc(request.Rfc);
    var employeeId = RequireEmployeeId(request.EmployeeId, nameof(request.EmployeeId));
    var actorEmployeeId = RequireEmployeeId(request.ActorEmployeeId, nameof(request.ActorEmployeeId));
    var current = await RequireCurrentAsync(rfc, requireEmployee: true, ct);
    RequireSelf(current, actorEmployeeId);
    var actor = RequireActor(current.UserName);
    if (employeeId != actorEmployeeId)
      return CapacitacionCommandResult.Fail("El colaborador debe confirmar personalmente el bloque completado.");
    if (request.AsignacionId <= 0 || request.BloqueId <= 0)
      return CapacitacionCommandResult.Fail("La asignación o el bloque no son válidos.");

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var valid = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
        """
        SELECT COUNT(1)
        FROM capacitacion.Asignacion a WITH (UPDLOCK, HOLDLOCK)
        JOIN capacitacion.Leccion l ON l.CursoVersionId = a.CursoVersionId
        JOIN capacitacion.BloqueContenido b ON b.LeccionId = l.LeccionId AND b.BloqueId = @BloqueId
        WHERE a.AsignacionId = @AsignacionId AND a.Rfc = @Rfc AND a.EmployeeId = @EmployeeId
          AND a.Estado NOT IN ('CANCELADA','COMPLETADA')
          AND (@SesionId IS NULL OR EXISTS
          (
            SELECT 1 FROM capacitacion.Sesion s
            JOIN capacitacion.SesionParticipante sp ON sp.SesionId = s.SesionId
            WHERE s.SesionId = @SesionId AND s.Rfc = a.Rfc AND s.CursoVersionId = a.CursoVersionId
              AND sp.EmployeeId = a.EmployeeId AND sp.AsignacionId = a.AsignacionId
          ));
        """,
        new { request.AsignacionId, Rfc = rfc, EmployeeId = employeeId, request.BloqueId, request.SesionId },
        tx,
        cancellationToken: ct));
      if (valid != 1)
      {
        await tx.RollbackAsync(ct);
        return CapacitacionCommandResult.Fail("El bloque no pertenece a la asignación o el colaborador no tiene acceso.");
      }

      var inserted = await conn.ExecuteAsync(new CommandDefinition(
        """
        IF NOT EXISTS
        (
          SELECT 1 FROM capacitacion.ProgresoBloque WITH (UPDLOCK, HOLDLOCK)
          WHERE AsignacionId = @AsignacionId AND EmployeeId = @EmployeeId AND BloqueId = @BloqueId
        )
        BEGIN
          INSERT INTO capacitacion.ProgresoBloque
            (Rfc, AsignacionId, SesionId, EmployeeId, BloqueId, Estado, RegistradoPorEmployeeId, RegistradoPor)
          VALUES
            (@Rfc, @AsignacionId, @SesionId, @EmployeeId, @BloqueId, 'COMPLETADO', @ActorEmployeeId, @Actor);
        END;
        """,
        new
        {
          Rfc = rfc,
          request.AsignacionId,
          request.SesionId,
          EmployeeId = employeeId,
          request.BloqueId,
          ActorEmployeeId = actorEmployeeId,
          Actor = actor
        },
        tx,
        cancellationToken: ct));

      if (inserted > 0)
        await AddAuditAsync(conn, tx, rfc, "ASIGNACION", request.AsignacionId, "BLOQUE_COMPLETADO", $"Bloque {request.BloqueId} confirmado.", actorEmployeeId, actor, ct);
      await RecalculateAssignmentAsync(conn, tx, request.AsignacionId, rfc, ct);
      await tx.CommitAsync(ct);
      return CapacitacionCommandResult.Ok(inserted > 0 ? "Bloque completado." : "El bloque ya estaba completado.", request.AsignacionId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<CapacitacionEvaluacionResultadoDto> RegistrarEvaluacionAsync(CapacitacionRegistrarEvaluacionRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = RequireRfc(request.Rfc);
    var employeeId = RequireEmployeeId(request.EmployeeId, nameof(request.EmployeeId));
    var actorEmployeeId = RequireEmployeeId(request.ActorEmployeeId, nameof(request.ActorEmployeeId));
    var current = await RequireCurrentAsync(rfc, requireEmployee: true, ct);
    RequireSelf(current, actorEmployeeId);
    var actor = RequireActor(current.UserName);
    if (employeeId != actorEmployeeId)
      return EvaluationFailure("El colaborador debe presentar personalmente su evaluación.");
    if (request.AsignacionId <= 0 || request.EvaluacionId <= 0)
      return EvaluationFailure("La asignación o evaluación no son válidas.");

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var evaluation = await conn.QueryFirstOrDefaultAsync<EvaluationHeaderRow>(new CommandDefinition(
        """
        SELECT e.EvaluacionId, e.CalificacionMinima
        FROM capacitacion.Asignacion a WITH (UPDLOCK, HOLDLOCK)
        JOIN capacitacion.Evaluacion e ON e.CursoVersionId = a.CursoVersionId AND e.EvaluacionId = @EvaluacionId
        WHERE a.AsignacionId = @AsignacionId AND a.Rfc = @Rfc AND a.EmployeeId = @EmployeeId
          AND a.Estado NOT IN ('CANCELADA','COMPLETADA')
          AND (@SesionId IS NULL OR EXISTS
          (
            SELECT 1 FROM capacitacion.Sesion s
            JOIN capacitacion.SesionParticipante sp ON sp.SesionId = s.SesionId
            WHERE s.SesionId = @SesionId AND s.Rfc = a.Rfc AND s.CursoVersionId = a.CursoVersionId
              AND sp.EmployeeId = a.EmployeeId AND sp.AsignacionId = a.AsignacionId
          ));
        """,
        new { request.AsignacionId, Rfc = rfc, EmployeeId = employeeId, request.EvaluacionId, request.SesionId },
        tx,
        cancellationToken: ct));
      if (evaluation is null)
      {
        await tx.RollbackAsync(ct);
        return EvaluationFailure("La evaluación no pertenece a esta asignación o el colaborador no tiene acceso.");
      }

      var options = (await conn.QueryAsync<EvaluationOptionRow>(new CommandDefinition(
        """
        SELECT p.PreguntaId, p.Critica, o.OpcionId, o.EsCorrecta
        FROM capacitacion.Pregunta p
        JOIN capacitacion.OpcionPregunta o ON o.PreguntaId = p.PreguntaId
        WHERE p.EvaluacionId = @EvaluacionId
        ORDER BY p.Orden, o.Orden;
        """,
        new { request.EvaluacionId },
        tx,
        cancellationToken: ct))).AsList();
      var questions = options.GroupBy(item => new { item.PreguntaId, item.Critica }).ToArray();
      if (questions.Length == 0)
      {
        await tx.RollbackAsync(ct);
        return EvaluationFailure("La evaluación todavía no contiene preguntas.");
      }

      var selectedIds = request.OpcionIds.Distinct().ToHashSet();
      var answers = new List<EvaluationAnswerRow>(questions.Length);
      foreach (var question in questions)
      {
        var selected = question.Where(item => selectedIds.Contains(item.OpcionId)).ToArray();
        if (selected.Length != 1)
        {
          await tx.RollbackAsync(ct);
          return EvaluationFailure("Selecciona exactamente una respuesta para cada pregunta.");
        }

        answers.Add(new EvaluationAnswerRow
        {
          PreguntaId = question.Key.PreguntaId,
          OpcionId = selected[0].OpcionId,
          EsCorrecta = selected[0].EsCorrecta,
          Critica = question.Key.Critica
        });
      }
      if (selectedIds.Count != answers.Count || selectedIds.Any(id => options.All(option => option.OpcionId != id)))
      {
        await tx.RollbackAsync(ct);
        return EvaluationFailure("Una respuesta no pertenece a esta evaluación.");
      }

      var correctCount = answers.Count(item => item.EsCorrecta);
      var score = decimal.Round(correctCount * 100m / answers.Count, 2, MidpointRounding.AwayFromZero);
      var failedCritical = answers.Any(item => item.Critica && !item.EsCorrecta);
      var passed = score >= evaluation.CalificacionMinima && !failedCritical;
      var attemptNumber = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
        """
        SELECT ISNULL(MAX(NumeroIntento), 0) + 1
        FROM capacitacion.IntentoEvaluacion WITH (UPDLOCK, HOLDLOCK)
        WHERE AsignacionId = @AsignacionId AND EvaluacionId = @EvaluacionId;
        """,
        new { request.AsignacionId, request.EvaluacionId },
        tx,
        cancellationToken: ct));
      var attemptId = await conn.QuerySingleAsync<long>(new CommandDefinition(
        """
        INSERT INTO capacitacion.IntentoEvaluacion
          (Rfc, AsignacionId, SesionId, EvaluacionId, EmployeeId, NumeroIntento, Calificacion, Aprobada, FalloPreguntaCritica, RegistradoPorEmployeeId, RegistradoPor)
        OUTPUT inserted.IntentoId
        VALUES
          (@Rfc, @AsignacionId, @SesionId, @EvaluacionId, @EmployeeId, @NumeroIntento, @Calificacion, @Aprobada, @FalloCritica, @ActorEmployeeId, @Actor);
        """,
        new
        {
          Rfc = rfc,
          request.AsignacionId,
          request.SesionId,
          request.EvaluacionId,
          EmployeeId = employeeId,
          NumeroIntento = attemptNumber,
          Calificacion = score,
          Aprobada = passed,
          FalloCritica = failedCritical,
          ActorEmployeeId = actorEmployeeId,
          Actor = actor
        },
        tx,
        cancellationToken: ct));

      foreach (var answer in answers)
      {
        await conn.ExecuteAsync(new CommandDefinition(
          """
          INSERT INTO capacitacion.RespuestaEvaluacion (IntentoId, PreguntaId, OpcionId, EsCorrecta)
          VALUES (@IntentoId, @PreguntaId, @OpcionId, @EsCorrecta);
          """,
          new { IntentoId = attemptId, answer.PreguntaId, answer.OpcionId, answer.EsCorrecta },
          tx,
          cancellationToken: ct));
      }

      await AddAuditAsync(conn, tx, rfc, "ASIGNACION", request.AsignacionId, "EVALUACION_PRESENTADA", $"Evaluación {request.EvaluacionId}: {score:0.##}, {(passed ? "aprobada" : "no aprobada")}.", actorEmployeeId, actor, ct);
      await RecalculateAssignmentAsync(conn, tx, request.AsignacionId, rfc, ct);
      await tx.CommitAsync(ct);
      return new CapacitacionEvaluacionResultadoDto
      {
        Success = true,
        Message = passed
          ? $"Evaluación aprobada con {score:0.##}."
          : failedCritical
            ? $"Resultado {score:0.##}. Debes corregir una respuesta crítica antes de continuar."
            : $"Resultado {score:0.##}. La calificación mínima es {evaluation.CalificacionMinima:0.##}.",
        IntentoId = attemptId,
        Calificacion = score,
        Aprobada = passed,
        FalloPreguntaCritica = failedCritical,
        PreguntasIncorrectas = answers.Where(item => !item.EsCorrecta).Select(item => item.PreguntaId).ToArray()
      };
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<CapacitacionCommandResult> RegistrarResultadoPracticoAsync(CapacitacionRegistrarPracticaRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = RequireRfc(request.Rfc);
    var employeeId = RequireEmployeeId(request.EmployeeId, nameof(request.EmployeeId));
    var actorEmployeeId = RequireEmployeeId(request.ActorEmployeeId, nameof(request.ActorEmployeeId));
    var current = await RequireCurrentAsync(rfc, requireEmployee: true, ct, CapacitacionCodes.RoleAdmin, CapacitacionCodes.RoleInstructor);
    RequireSelf(current, actorEmployeeId);
    var actor = RequireActor(current.UserName);
    if (request.AsignacionId <= 0 || request.PracticaId <= 0)
      return CapacitacionCommandResult.Fail("La asignación o práctica no son válidas.");
    if (actorEmployeeId == employeeId)
      return CapacitacionCommandResult.Fail("La práctica debe ser evaluada por el instructor.");

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var authorized = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
        """
        SELECT COUNT(1)
        FROM capacitacion.Asignacion a WITH (UPDLOCK, HOLDLOCK)
        JOIN capacitacion.Practica p ON p.CursoVersionId = a.CursoVersionId AND p.PracticaId = @PracticaId
        WHERE a.AsignacionId = @AsignacionId AND a.Rfc = @Rfc AND a.EmployeeId = @EmployeeId
          AND a.Estado NOT IN ('CANCELADA','COMPLETADA')
          AND (a.InstructorEmployeeId = @ActorEmployeeId OR EXISTS
          (
            SELECT 1 FROM capacitacion.Sesion s
            JOIN capacitacion.SesionParticipante sp ON sp.SesionId = s.SesionId
            WHERE s.SesionId = @SesionId AND s.Rfc = a.Rfc AND s.CursoVersionId = a.CursoVersionId
              AND s.InstructorEmployeeId = @ActorEmployeeId AND sp.EmployeeId = a.EmployeeId AND sp.AsignacionId = a.AsignacionId
          ));
        """,
        new
        {
          request.AsignacionId,
          Rfc = rfc,
          EmployeeId = employeeId,
          request.PracticaId,
          ActorEmployeeId = actorEmployeeId,
          request.SesionId
        },
        tx,
        cancellationToken: ct));
      if (authorized != 1)
      {
        await tx.RollbackAsync(ct);
        return CapacitacionCommandResult.Fail("Solo el instructor asignado puede evaluar esta práctica.");
      }

      var expectedSteps = (await conn.QueryAsync<PracticeStepRow>(new CommandDefinition(
        """
        SELECT PracticaPasoId, Critico
        FROM capacitacion.PracticaPaso
        WHERE PracticaId = @PracticaId
        ORDER BY Orden;
        """,
        new { request.PracticaId },
        tx,
        cancellationToken: ct))).AsList();
      var submittedSteps = request.Pasos.GroupBy(item => item.PracticaPasoId).ToDictionary(group => group.Key, group => group.First());
      if (expectedSteps.Count == 0 || submittedSteps.Count != expectedSteps.Count || expectedSteps.Any(step => !submittedSteps.ContainsKey(step.PracticaPasoId)))
      {
        await tx.RollbackAsync(ct);
        return CapacitacionCommandResult.Fail("Evalúa cada punto de la lista práctica exactamente una vez.");
      }

      var approved = expectedSteps.All(step => submittedSteps[step.PracticaPasoId].Aprobado);
      var attemptNumber = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
        """
        SELECT ISNULL(MAX(NumeroIntento), 0) + 1
        FROM capacitacion.ResultadoPractico WITH (UPDLOCK, HOLDLOCK)
        WHERE AsignacionId = @AsignacionId AND PracticaId = @PracticaId;
        """,
        new { request.AsignacionId, request.PracticaId },
        tx,
        cancellationToken: ct));
      var resultId = await conn.QuerySingleAsync<long>(new CommandDefinition(
        """
        INSERT INTO capacitacion.ResultadoPractico
          (Rfc, AsignacionId, SesionId, PracticaId, EmployeeId, NumeroIntento, Aprobada, Observaciones, EvaluadaPorEmployeeId, EvaluadaPor)
        OUTPUT inserted.ResultadoPracticoId
        VALUES
          (@Rfc, @AsignacionId, @SesionId, @PracticaId, @EmployeeId, @NumeroIntento, @Aprobada, @Observaciones, @ActorEmployeeId, @Actor);
        """,
        new
        {
          Rfc = rfc,
          request.AsignacionId,
          request.SesionId,
          request.PracticaId,
          EmployeeId = employeeId,
          NumeroIntento = attemptNumber,
          Aprobada = approved,
          Observaciones = TruncateNullable(request.Observaciones, 1000),
          ActorEmployeeId = actorEmployeeId,
          Actor = actor
        },
        tx,
        cancellationToken: ct));
      foreach (var expectedStep in expectedSteps)
      {
        var submitted = submittedSteps[expectedStep.PracticaPasoId];
        await conn.ExecuteAsync(new CommandDefinition(
          """
          INSERT INTO capacitacion.ResultadoPracticoPaso (ResultadoPracticoId, PracticaPasoId, Aprobado, Observaciones)
          VALUES (@ResultadoPracticoId, @PracticaPasoId, @Aprobado, @Observaciones);
          """,
          new
          {
            ResultadoPracticoId = resultId,
            expectedStep.PracticaPasoId,
            submitted.Aprobado,
            Observaciones = TruncateNullable(submitted.Observaciones, 500)
          },
          tx,
          cancellationToken: ct));
      }

      await AddAuditAsync(conn, tx, rfc, "ASIGNACION", request.AsignacionId, "PRACTICA_EVALUADA", $"Práctica {request.PracticaId}: {(approved ? "aprobada" : "requiere repetición")}.", actorEmployeeId, actor, ct);
      await RecalculateAssignmentAsync(conn, tx, request.AsignacionId, rfc, ct);
      await tx.CommitAsync(ct);
      return CapacitacionCommandResult.Ok(approved ? "Práctica aprobada." : "La práctica requiere corrección y un nuevo intento.", resultId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<CapacitacionCommandResult> FirmarFinalizacionAsync(CapacitacionFirmarRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = RequireRfc(request.Rfc);
    var instructorEmployeeId = RequireEmployeeId(request.InstructorEmployeeId, nameof(request.InstructorEmployeeId));
    var current = await RequireCurrentAsync(rfc, requireEmployee: true, ct, CapacitacionCodes.RoleAdmin, CapacitacionCodes.RoleInstructor);
    RequireSelf(current, instructorEmployeeId);
    var actor = RequireActor(current.UserName);
    if (request.AsignacionId <= 0)
      return CapacitacionCommandResult.Fail("La asignación no es válida.");

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      await RecalculateAssignmentAsync(conn, tx, request.AsignacionId, rfc, ct);
      var assignment = await conn.QueryFirstOrDefaultAsync<AssignmentSignRow>(new CommandDefinition(
        """
        SELECT a.AsignacionId, a.InstructorEmployeeId, a.Estado, a.Porcentaje,
          (SELECT TOP (1) FirmaInstructorId FROM capacitacion.FirmaInstructor fi WHERE fi.AsignacionId = a.AsignacionId) AS FirmaInstructorId
        FROM capacitacion.Asignacion a WITH (UPDLOCK, HOLDLOCK)
        WHERE a.AsignacionId = @AsignacionId AND a.Rfc = @Rfc;
        """,
        new { request.AsignacionId, Rfc = rfc },
        tx,
        cancellationToken: ct));
      if (assignment is null)
      {
        await tx.RollbackAsync(ct);
        return CapacitacionCommandResult.Fail("No se encontró la asignación en el RFC activo.");
      }
      if (assignment.InstructorEmployeeId != instructorEmployeeId)
      {
        await tx.RollbackAsync(ct);
        return CapacitacionCommandResult.Fail("Solo el instructor asignado puede firmar la finalización.");
      }
      if (assignment.FirmaInstructorId.HasValue)
      {
        await tx.RollbackAsync(ct);
        return CapacitacionCommandResult.Ok("La finalización ya fue firmada por el instructor.", assignment.FirmaInstructorId);
      }
      if (assignment.Estado != CapacitacionCodes.AsignacionEsperaFirma || assignment.Porcentaje < 100)
      {
        await tx.RollbackAsync(ct);
        return CapacitacionCommandResult.Fail("Aún faltan bloques, una evaluación aprobada o la práctica aprobada.");
      }

      var signatureId = await conn.QuerySingleAsync<long>(new CommandDefinition(
        """
        INSERT INTO capacitacion.FirmaInstructor (Rfc, AsignacionId, InstructorEmployeeId, Comentarios, FirmadaPor)
        OUTPUT inserted.FirmaInstructorId
        VALUES (@Rfc, @AsignacionId, @InstructorEmployeeId, @Comentarios, @Actor);

        UPDATE capacitacion.Asignacion
        SET Estado = 'ESPERA_ACUSE'
        WHERE AsignacionId = @AsignacionId AND Rfc = @Rfc;
        """,
        new
        {
          Rfc = rfc,
          request.AsignacionId,
          InstructorEmployeeId = instructorEmployeeId,
          Comentarios = TruncateNullable(request.Comentarios, 1000),
          Actor = actor
        },
        tx,
        cancellationToken: ct));
      await AddAuditAsync(conn, tx, rfc, "ASIGNACION", request.AsignacionId, "FIRMADA_INSTRUCTOR", "El instructor confirmó los resultados y el cierre de la capacitación.", instructorEmployeeId, actor, ct);
      await tx.CommitAsync(ct);
      return CapacitacionCommandResult.Ok("Finalización firmada. Falta el acuse del colaborador.", signatureId);
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
      await tx.RollbackAsync(ct);
      return CapacitacionCommandResult.Ok("La finalización ya fue firmada por el instructor.", request.AsignacionId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<CapacitacionCommandResult> AcusarFinalizacionAsync(CapacitacionAcusarRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = RequireRfc(request.Rfc);
    var employeeId = RequireEmployeeId(request.EmployeeId, nameof(request.EmployeeId));
    var current = await RequireCurrentAsync(rfc, requireEmployee: true, ct);
    RequireSelf(current, employeeId);
    var actor = RequireActor(current.UserName);
    if (request.AsignacionId <= 0)
      return CapacitacionCommandResult.Fail("La asignación no es válida.");

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var existingFinalization = await conn.QueryFirstOrDefaultAsync<long?>(new CommandDefinition(
        """
        SELECT FinalizacionId
        FROM capacitacion.Finalizacion WITH (UPDLOCK, HOLDLOCK)
        WHERE AsignacionId = @AsignacionId AND Rfc = @Rfc AND EmployeeId = @EmployeeId;
        """,
        new { request.AsignacionId, Rfc = rfc, EmployeeId = employeeId },
        tx,
        cancellationToken: ct));
      if (existingFinalization.HasValue)
      {
        await tx.RollbackAsync(ct);
        return CapacitacionCommandResult.Ok("La capacitación ya cuenta con el acuse del colaborador.", existingFinalization);
      }

      var finalizationId = await conn.QueryFirstOrDefaultAsync<long?>(new CommandDefinition(
        """
        INSERT INTO capacitacion.Finalizacion
          (Rfc, AsignacionId, EmployeeId, CursoId, CursoVersionId, NumeroVersion, CursoClave, CursoNombre,
           Calificacion, PracticaAprobada, FirmaInstructorId, AcusadaPor)
        OUTPUT inserted.FinalizacionId
        SELECT
          a.Rfc, a.AsignacionId, a.EmployeeId, c.CursoId, cv.CursoVersionId, cv.NumeroVersion, c.Clave, c.Nombre,
          assessment.Calificacion, CONVERT(bit, ISNULL(practical.Aprobada, 0)), signatureInfo.FirmaInstructorId, @Actor
        FROM capacitacion.Asignacion a WITH (UPDLOCK, HOLDLOCK)
        JOIN capacitacion.CursoVersion cv ON cv.CursoVersionId = a.CursoVersionId
        JOIN capacitacion.Curso c ON c.CursoId = cv.CursoId
        JOIN capacitacion.FirmaInstructor signatureInfo ON signatureInfo.AsignacionId = a.AsignacionId AND signatureInfo.Rfc = a.Rfc
        OUTER APPLY
        (
          SELECT TOP (1) attempt.Calificacion
          FROM capacitacion.IntentoEvaluacion attempt
          WHERE attempt.AsignacionId = a.AsignacionId AND attempt.Aprobada = 1
          ORDER BY attempt.PresentadaEn DESC, attempt.IntentoId DESC
        ) assessment
        OUTER APPLY
        (
          SELECT TOP (1) resultInfo.Aprobada
          FROM capacitacion.ResultadoPractico resultInfo
          WHERE resultInfo.AsignacionId = a.AsignacionId AND resultInfo.Aprobada = 1
          ORDER BY resultInfo.EvaluadaEn DESC, resultInfo.ResultadoPracticoId DESC
        ) practical
        WHERE a.AsignacionId = @AsignacionId AND a.Rfc = @Rfc AND a.EmployeeId = @EmployeeId
          AND a.Estado = 'ESPERA_ACUSE' AND a.Porcentaje = 100;
        """,
        new { request.AsignacionId, Rfc = rfc, EmployeeId = employeeId, Actor = actor },
        tx,
        cancellationToken: ct));
      if (!finalizationId.HasValue)
      {
        await tx.RollbackAsync(ct);
        return CapacitacionCommandResult.Fail("La capacitación aún no está firmada o no pertenece al colaborador activo.");
      }

      await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE capacitacion.Asignacion
        SET Estado = 'COMPLETADA', Porcentaje = 100, CompletadaEn = SYSUTCDATETIME()
        WHERE AsignacionId = @AsignacionId AND Rfc = @Rfc AND EmployeeId = @EmployeeId;
        """,
        new { request.AsignacionId, Rfc = rfc, EmployeeId = employeeId },
        tx,
        cancellationToken: ct));
      await AddAuditAsync(conn, tx, rfc, "ASIGNACION", request.AsignacionId, "ACUSE_COLABORADOR", "El colaborador confirmó que recibió y comprendió la capacitación.", employeeId, actor, ct);
      await tx.CommitAsync(ct);
      return CapacitacionCommandResult.Ok("Capacitación completada y registrada en tu historial.", finalizationId);
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
      await tx.RollbackAsync(ct);
      return CapacitacionCommandResult.Ok("La capacitación ya cuenta con el acuse del colaborador.", request.AsignacionId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  private async Task<CapacitacionCursoDetalleDto?> LoadCourseAsync(
    DbConnection conn,
    IDbTransaction? tx,
    int courseVersionId,
    string rfc,
    bool allowPinned,
    CancellationToken ct)
  {
    const string sql =
      """
      SELECT TOP (1)
        c.CursoId,
        cv.CursoVersionId,
        cv.NumeroVersion,
        c.Clave,
        c.Categoria,
        c.Nombre,
        c.Descripcion,
        cv.Objetivos,
        cv.Prerequisitos,
        c.DuracionMinutos,
        cv.CalificacionMinima,
        cv.Estado AS EstadoVersion,
        (SELECT COUNT(1) FROM capacitacion.Leccion countLesson WHERE countLesson.CursoVersionId = cv.CursoVersionId) AS LeccionCount,
        (SELECT COUNT(1) FROM capacitacion.Leccion countLesson JOIN capacitacion.BloqueContenido countBlock ON countBlock.LeccionId = countLesson.LeccionId WHERE countLesson.CursoVersionId = cv.CursoVersionId) AS BloqueCount
      FROM capacitacion.CursoVersion cv
      JOIN capacitacion.Curso c ON c.CursoId = cv.CursoId
      WHERE cv.CursoVersionId = @CursoVersionId AND c.Rfc IN (@Rfc, '*')
        AND (@AllowPinned = 1 OR (cv.Estado = 'PUBLICADA' AND c.Activo = 1))
      ORDER BY CASE WHEN c.Rfc = @Rfc THEN 0 ELSE 1 END;

      SELECT LeccionId, CursoVersionId, Orden, Clave, Titulo, Objetivo, DuracionMinutos, Requerida
      FROM capacitacion.Leccion
      WHERE CursoVersionId = @CursoVersionId
      ORDER BY Orden;

      SELECT b.BloqueId, b.LeccionId, b.Orden, b.Tipo, b.Titulo, b.Contenido, b.ConfiguracionJson, b.Requerido
      FROM capacitacion.Leccion l
      JOIN capacitacion.BloqueContenido b ON b.LeccionId = l.LeccionId
      WHERE l.CursoVersionId = @CursoVersionId
      ORDER BY l.Orden, b.Orden;

      SELECT r.RecursoId, r.BloqueId, r.Orden, r.Tipo, r.Titulo, r.Ruta, r.TextoAlternativo, r.HashContenido, r.CapturadoEn, r.VersionAplicacion
      FROM capacitacion.Leccion l
      JOIN capacitacion.BloqueContenido b ON b.LeccionId = l.LeccionId
      JOIN capacitacion.Recurso r ON r.BloqueId = b.BloqueId
      WHERE l.CursoVersionId = @CursoVersionId
      ORDER BY l.Orden, b.Orden, r.Orden;

      SELECT EvaluacionId, CursoVersionId, Titulo, Instrucciones, CalificacionMinima, Requerida
      FROM capacitacion.Evaluacion
      WHERE CursoVersionId = @CursoVersionId
      ORDER BY EvaluacionId;

      SELECT p.PreguntaId, p.EvaluacionId, p.Orden, p.Texto, p.Explicacion, p.Critica
      FROM capacitacion.Evaluacion e
      JOIN capacitacion.Pregunta p ON p.EvaluacionId = e.EvaluacionId
      WHERE e.CursoVersionId = @CursoVersionId
      ORDER BY e.EvaluacionId, p.Orden;

      SELECT o.OpcionId, o.PreguntaId, o.Orden, o.Texto
      FROM capacitacion.Evaluacion e
      JOIN capacitacion.Pregunta p ON p.EvaluacionId = e.EvaluacionId
      JOIN capacitacion.OpcionPregunta o ON o.PreguntaId = p.PreguntaId
      WHERE e.CursoVersionId = @CursoVersionId
      ORDER BY e.EvaluacionId, p.Orden, o.Orden;

      SELECT PracticaId, CursoVersionId, Titulo, Instrucciones, RutaSandbox, Requerida
      FROM capacitacion.Practica
      WHERE CursoVersionId = @CursoVersionId
      ORDER BY PracticaId;

      SELECT pp.PracticaPasoId, pp.PracticaId, pp.Orden, pp.Descripcion, pp.Critico
      FROM capacitacion.Practica p
      JOIN capacitacion.PracticaPaso pp ON pp.PracticaId = p.PracticaId
      WHERE p.CursoVersionId = @CursoVersionId
      ORDER BY p.PracticaId, pp.Orden;
      """;

    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(
      sql,
      new { CursoVersionId = courseVersionId, Rfc = rfc, AllowPinned = allowPinned },
      tx,
      cancellationToken: ct));
    var detail = await multi.ReadFirstOrDefaultAsync<CapacitacionCursoDetalleDto>();
    if (detail is null)
      return null;

    var lessons = (await multi.ReadAsync<CapacitacionLeccionDto>()).AsList();
    var blocks = (await multi.ReadAsync<CapacitacionBloqueDto>()).AsList();
    var resources = (await multi.ReadAsync<CapacitacionRecursoDto>()).AsList();
    var evaluations = (await multi.ReadAsync<CapacitacionEvaluacionDto>()).AsList();
    var questions = (await multi.ReadAsync<CapacitacionPreguntaDto>()).AsList();
    var options = (await multi.ReadAsync<CapacitacionOpcionDto>()).AsList();
    var practices = (await multi.ReadAsync<CapacitacionPracticaDto>()).AsList();
    var practiceSteps = (await multi.ReadAsync<CapacitacionPracticaPasoDto>()).AsList();

    foreach (var block in blocks)
      block.Recursos = resources.Where(resource => resource.BloqueId == block.BloqueId).ToArray();
    foreach (var lesson in lessons)
      lesson.Bloques = blocks.Where(block => block.LeccionId == lesson.LeccionId).ToArray();
    foreach (var question in questions)
      question.Opciones = options.Where(option => option.PreguntaId == question.PreguntaId).ToArray();
    foreach (var evaluation in evaluations)
      evaluation.Preguntas = questions.Where(question => question.EvaluacionId == evaluation.EvaluacionId).ToArray();
    foreach (var practice in practices)
      practice.Pasos = practiceSteps.Where(step => step.PracticaId == practice.PracticaId).ToArray();

    detail.Lecciones = lessons;
    detail.Evaluaciones = evaluations;
    detail.Practicas = practices;
    return detail;
  }

  private async Task<long> GetOrCreateAssignmentAsync(
    DbConnection conn,
    IDbTransaction tx,
    string rfc,
    int courseVersionId,
    int employeeId,
    int instructorEmployeeId,
    int actorEmployeeId,
    string actor,
    CancellationToken ct)
  {
    var existingId = await conn.QueryFirstOrDefaultAsync<long?>(new CommandDefinition(
      """
      SELECT TOP (1) AsignacionId
      FROM capacitacion.Asignacion WITH (UPDLOCK, HOLDLOCK)
      WHERE Rfc = @Rfc AND EmployeeId = @EmployeeId AND CursoVersionId = @CursoVersionId AND Estado <> 'CANCELADA';
      """,
      new { Rfc = rfc, EmployeeId = employeeId, CursoVersionId = courseVersionId },
      tx,
      cancellationToken: ct));
    if (existingId.HasValue)
    {
      await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE capacitacion.Asignacion
        SET InstructorEmployeeId = @InstructorEmployeeId
        WHERE AsignacionId = @AsignacionId AND Rfc = @Rfc AND Estado NOT IN ('COMPLETADA','CANCELADA');
        """,
        new { AsignacionId = existingId.Value, Rfc = rfc, InstructorEmployeeId = instructorEmployeeId },
        tx,
        cancellationToken: ct));
      return existingId.Value;
    }

    var assignmentId = await conn.QuerySingleAsync<long>(new CommandDefinition(
      """
      INSERT INTO capacitacion.Asignacion
        (Rfc, EmployeeId, CursoVersionId, InstructorEmployeeId, Estado, Porcentaje, AsignadaPorEmployeeId, AsignadaPor)
      OUTPUT inserted.AsignacionId
      VALUES
        (@Rfc, @EmployeeId, @CursoVersionId, @InstructorEmployeeId, 'ASIGNADA', 0, @ActorEmployeeId, @Actor);
      """,
      new
      {
        Rfc = rfc,
        EmployeeId = employeeId,
        CursoVersionId = courseVersionId,
        InstructorEmployeeId = instructorEmployeeId,
        ActorEmployeeId = actorEmployeeId,
        Actor = actor
      },
      tx,
      cancellationToken: ct));
    await AddAuditAsync(conn, tx, rfc, "ASIGNACION", assignmentId, "ASIGNADA", $"Asignación creada al preparar una sesión para el colaborador {employeeId}.", actorEmployeeId, actor, ct);
    return assignmentId;
  }

  private static async Task RecalculateAssignmentAsync(DbConnection conn, IDbTransaction tx, long assignmentId, string rfc, CancellationToken ct)
  {
    const string sql =
      """
      SELECT
        a.Estado,
        (SELECT COUNT(1) FROM capacitacion.Leccion l JOIN capacitacion.BloqueContenido b ON b.LeccionId = l.LeccionId WHERE l.CursoVersionId = a.CursoVersionId AND l.Requerida = 1 AND b.Requerido = 1) AS RequiredBlocks,
        (SELECT COUNT(1) FROM capacitacion.ProgresoBloque progressInfo JOIN capacitacion.Leccion l ON l.CursoVersionId = a.CursoVersionId JOIN capacitacion.BloqueContenido b ON b.LeccionId = l.LeccionId AND b.BloqueId = progressInfo.BloqueId WHERE progressInfo.AsignacionId = a.AsignacionId AND progressInfo.Estado = 'COMPLETADO' AND l.Requerida = 1 AND b.Requerido = 1) AS CompletedBlocks,
        (SELECT COUNT(1) FROM capacitacion.Evaluacion e WHERE e.CursoVersionId = a.CursoVersionId AND e.Requerida = 1) AS RequiredEvaluations,
        (SELECT COUNT(1) FROM capacitacion.Evaluacion e WHERE e.CursoVersionId = a.CursoVersionId AND e.Requerida = 1 AND EXISTS (SELECT 1 FROM capacitacion.IntentoEvaluacion attempt WHERE attempt.AsignacionId = a.AsignacionId AND attempt.EvaluacionId = e.EvaluacionId AND attempt.Aprobada = 1)) AS PassedEvaluations,
        (SELECT COUNT(1) FROM capacitacion.Practica p WHERE p.CursoVersionId = a.CursoVersionId AND p.Requerida = 1) AS RequiredPractices,
        (SELECT COUNT(1) FROM capacitacion.Practica p WHERE p.CursoVersionId = a.CursoVersionId AND p.Requerida = 1 AND EXISTS (SELECT 1 FROM capacitacion.ResultadoPractico resultInfo WHERE resultInfo.AsignacionId = a.AsignacionId AND resultInfo.PracticaId = p.PracticaId AND resultInfo.Aprobada = 1)) AS PassedPractices,
        CAST(CASE WHEN EXISTS (SELECT 1 FROM capacitacion.FirmaInstructor signatureInfo WHERE signatureInfo.AsignacionId = a.AsignacionId) THEN 1 ELSE 0 END AS bit) AS HasSignature
      FROM capacitacion.Asignacion a WITH (UPDLOCK, HOLDLOCK)
      WHERE a.AsignacionId = @AsignacionId AND a.Rfc = @Rfc;
      """;
    var row = await conn.QueryFirstOrDefaultAsync<ProgressTotalsRow>(new CommandDefinition(
      sql,
      new { AsignacionId = assignmentId, Rfc = rfc },
      tx,
      cancellationToken: ct));
    if (row is null || row.Estado is CapacitacionCodes.AsignacionCancelada or CapacitacionCodes.AsignacionCompletada)
      return;

    var total = row.RequiredBlocks + row.RequiredEvaluations + row.RequiredPractices;
    var completed = row.CompletedBlocks + row.PassedEvaluations + row.PassedPractices;
    var percentage = total == 0 ? 0 : decimal.Round(completed * 100m / total, 2, MidpointRounding.AwayFromZero);
    var state = row.HasSignature
      ? CapacitacionCodes.AsignacionEsperaAcuse
      : percentage >= 100
        ? CapacitacionCodes.AsignacionEsperaFirma
        : CapacitacionCodes.AsignacionEnCurso;

    await conn.ExecuteAsync(new CommandDefinition(
      """
      UPDATE capacitacion.Asignacion
      SET Porcentaje = @Porcentaje,
          Estado = @Estado,
          IniciadaEn = ISNULL(IniciadaEn, SYSUTCDATETIME())
      WHERE AsignacionId = @AsignacionId AND Rfc = @Rfc AND Estado NOT IN ('COMPLETADA','CANCELADA');
      """,
      new { AsignacionId = assignmentId, Rfc = rfc, Porcentaje = percentage, Estado = state },
      tx,
      cancellationToken: ct));
  }

  private static async Task<bool> PublishedCourseExistsAsync(DbConnection conn, IDbTransaction tx, int courseVersionId, string rfc, CancellationToken ct)
  {
    var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
      """
      SELECT COUNT(1)
      FROM capacitacion.CursoVersion cv
      JOIN capacitacion.Curso c ON c.CursoId = cv.CursoId
      WHERE cv.CursoVersionId = @CursoVersionId AND cv.Estado = 'PUBLICADA' AND c.Activo = 1 AND c.Rfc IN (@Rfc, '*');
      """,
      new { CursoVersionId = courseVersionId, Rfc = rfc },
      tx,
      cancellationToken: ct));
    return count == 1;
  }

  private static async Task<bool> EmployeesBelongToRfcAsync(DbConnection conn, IDbTransaction tx, string rfc, int[] employeeIds, bool requireActive, CancellationToken ct)
  {
    if (employeeIds.Length == 0)
      return false;

    var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
      """
      SELECT COUNT(DISTINCT ID)
      FROM dbo.Capital_Humano WITH (UPDLOCK, HOLDLOCK)
      WHERE RFC = @Rfc AND ID IN @EmployeeIds
        AND (@RequireActive = 0 OR UPPER(LTRIM(RTRIM(ISNULL([Status], '')))) = 'ACTIVO');
      """,
      new { Rfc = rfc, EmployeeIds = employeeIds, RequireActive = requireActive },
      tx,
      cancellationToken: ct));
    return count == employeeIds.Length;
  }

  private static Task AddAuditAsync(
    DbConnection conn,
    IDbTransaction tx,
    string rfc,
    string entity,
    long entityId,
    string eventName,
    string? detail,
    int actorEmployeeId,
    string actor,
    CancellationToken ct)
    => conn.ExecuteAsync(new CommandDefinition(
      """
      INSERT INTO capacitacion.EventoAuditoria
        (Rfc, Entidad, EntidadId, Evento, Detalle, DatosJson, ActorEmployeeId, Actor)
      VALUES
        (@Rfc, @Entidad, @EntidadId, @Evento, @Detalle, NULL, @ActorEmployeeId, @Actor);
      """,
      new
      {
        Rfc = rfc,
        Entidad = Truncate(entity, 40),
        EntidadId = entityId,
        Evento = Truncate(eventName, 64),
        Detalle = TruncateNullable(detail, 2000),
        ActorEmployeeId = actorEmployeeId,
        Actor = actor
      },
      tx,
      cancellationToken: ct));

  private async Task<CurrentEmployeeContext> RequireCurrentAsync(
    string rfc,
    bool requireEmployee,
    CancellationToken ct,
    params string[] roles)
  {
    var current = await _currentEmployeeAccessor.GetCurrentAsync(ct)
      ?? throw new UnauthorizedAccessException("La sesión no está autenticada.");
    if (!current.CanAccessRfc(rfc))
      throw new UnauthorizedAccessException("El usuario no tiene acceso al RFC seleccionado.");
    if (requireEmployee && !current.EmployeeId.HasValue)
      throw new UnauthorizedAccessException("El usuario no está ligado a un colaborador de Capital Humano.");
    if (roles.Length > 0 && !current.IsInRole(roles))
      throw new UnauthorizedAccessException("El usuario no tiene permisos para esta operación de capacitación.");

    if (requireEmployee)
    {
      using var connection = CreateConnection();
      var employeeMatches = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
        "SELECT COUNT(1) FROM dbo.Capital_Humano WHERE ID = @EmployeeId AND RFC = @Rfc;",
        new { EmployeeId = current.EmployeeId!.Value, Rfc = rfc },
        cancellationToken: ct));
      if (employeeMatches != 1)
        throw new UnauthorizedAccessException("El colaborador ligado al usuario no pertenece al RFC seleccionado.");
    }

    return current;
  }

  private static void RequireSelf(CurrentEmployeeContext current, int employeeId)
  {
    if (current.EmployeeId != employeeId)
      throw new UnauthorizedAccessException("La identidad del colaborador no coincide con la sesión autenticada.");
  }

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica no devolvió una DbConnection.");

  private static CapacitacionSesionDto CopySession(CapacitacionSesionResumenDto source)
    => new()
    {
      SesionId = source.SesionId,
      Rfc = source.Rfc,
      CursoVersionId = source.CursoVersionId,
      CursoNombre = source.CursoNombre,
      Nombre = source.Nombre,
      CodigoAcceso = source.CodigoAcceso,
      Estado = source.Estado,
      InstructorEmployeeId = source.InstructorEmployeeId,
      InstructorName = source.InstructorName,
      ParticipanteCount = source.ParticipanteCount,
      BloqueActualId = source.BloqueActualId,
      ProgramadaEn = source.ProgramadaEn,
      IniciadaEn = source.IniciadaEn,
      FinalizadaEn = source.FinalizadaEn
    };

  private static CapacitacionEvaluacionResultadoDto EvaluationFailure(string message)
    => new() { Success = false, Message = message };

  private static string RequireRfc(string? value)
  {
    var normalized = NullIfWhiteSpace(value)?.ToUpperInvariant();
    if (normalized is null || normalized == CapacitacionCodes.RfcGlobal || normalized.Length > 50)
      throw new ArgumentException("El RFC de la compañía es obligatorio y no puede ser global.", nameof(value));
    return normalized;
  }

  private static int RequireEmployeeId(int value, string parameterName)
  {
    if (value <= 0)
      throw new ArgumentOutOfRangeException(parameterName, "Se requiere un colaborador válido.");
    return value;
  }

  private static string RequireActor(string? value)
  {
    var normalized = NullIfWhiteSpace(value);
    if (normalized is null)
      throw new ArgumentException("No se pudo identificar al usuario que realiza la acción.", nameof(value));
    return Truncate(normalized, 256);
  }

  private static string CreateAccessCode()
    => Convert.ToHexString(RandomNumberGenerator.GetBytes(4));

  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private static string Truncate(string value, int maxLength)
    => value.Length <= maxLength ? value : value[..maxLength];

  private static string? TruncateNullable(string? value, int maxLength)
  {
    var normalized = NullIfWhiteSpace(value);
    return normalized is null ? null : Truncate(normalized, maxLength);
  }

  private const string AssignmentSelectSql =
    """
    SELECT
      a.AsignacionId,
      a.Rfc,
      a.EmployeeId,
      COALESCE(NULLIF(LTRIM(RTRIM(employeeInfo.NombreCorto)), ''), CONCAT(employeeInfo.Nombre, ' ', employeeInfo.ApellidoPaterno)) AS EmployeeName,
      a.CursoVersionId,
      c.Clave AS CursoClave,
      c.Nombre AS CursoNombre,
      c.Categoria,
      cv.NumeroVersion,
      a.Estado,
      a.Porcentaje,
      a.AsignadaEn,
      a.FechaLimite,
      a.IniciadaEn,
      a.CompletadaEn,
      a.InstructorEmployeeId,
      COALESCE(NULLIF(LTRIM(RTRIM(instructorInfo.NombreCorto)), ''), CONCAT(instructorInfo.Nombre, ' ', instructorInfo.ApellidoPaterno)) AS InstructorName,
      assessment.Calificacion,
      latestProgress.BloqueId AS UltimoBloqueCompletadoId,
      CAST(CASE WHEN practical.Aprobada = 1 THEN 1 ELSE 0 END AS bit) AS PracticaAprobada,
      CAST(CASE WHEN signatureInfo.FirmaInstructorId IS NOT NULL THEN 1 ELSE 0 END AS bit) AS FirmaInstructor,
      CAST(CASE WHEN finalInfo.FinalizacionId IS NOT NULL THEN 1 ELSE 0 END AS bit) AS AcuseColaborador
    FROM capacitacion.Asignacion a
    JOIN capacitacion.CursoVersion cv ON cv.CursoVersionId = a.CursoVersionId
    JOIN capacitacion.Curso c ON c.CursoId = cv.CursoId
    JOIN dbo.Capital_Humano employeeInfo ON employeeInfo.ID = a.EmployeeId AND employeeInfo.RFC = a.Rfc
    LEFT JOIN dbo.Capital_Humano instructorInfo ON instructorInfo.ID = a.InstructorEmployeeId AND instructorInfo.RFC = a.Rfc
    OUTER APPLY
    (
      SELECT TOP (1) attempt.Calificacion
      FROM capacitacion.IntentoEvaluacion attempt
      WHERE attempt.AsignacionId = a.AsignacionId
      ORDER BY attempt.Aprobada DESC, attempt.PresentadaEn DESC, attempt.IntentoId DESC
    ) assessment
    OUTER APPLY
    (
      SELECT TOP (1) progressInfo.BloqueId
      FROM capacitacion.ProgresoBloque progressInfo
      WHERE progressInfo.AsignacionId = a.AsignacionId AND progressInfo.EmployeeId = a.EmployeeId AND progressInfo.Estado = 'COMPLETADO'
      ORDER BY progressInfo.CompletadoEn DESC, progressInfo.ProgresoBloqueId DESC
    ) latestProgress
    OUTER APPLY
    (
      SELECT TOP (1) resultInfo.Aprobada
      FROM capacitacion.ResultadoPractico resultInfo
      WHERE resultInfo.AsignacionId = a.AsignacionId
      ORDER BY resultInfo.Aprobada DESC, resultInfo.EvaluadaEn DESC, resultInfo.ResultadoPracticoId DESC
    ) practical
    OUTER APPLY
    (
      SELECT TOP (1) signatureRow.FirmaInstructorId
      FROM capacitacion.FirmaInstructor signatureRow
      WHERE signatureRow.AsignacionId = a.AsignacionId
    ) signatureInfo
    OUTER APPLY
    (
      SELECT TOP (1) finalRow.FinalizacionId
      FROM capacitacion.Finalizacion finalRow
      WHERE finalRow.AsignacionId = a.AsignacionId
    ) finalInfo
    """
    // El salto de línea final separa este fragmento del WHERE que le concatenan las consultas.
    + "\n";

  private const string SessionSelectSql =
    """
    SELECT
      s.SesionId,
      s.Rfc,
      s.CursoVersionId,
      c.Nombre AS CursoNombre,
      s.Nombre,
      s.CodigoAcceso,
      s.Estado,
      s.InstructorEmployeeId,
      COALESCE(NULLIF(LTRIM(RTRIM(instructorInfo.NombreCorto)), ''), CONCAT(instructorInfo.Nombre, ' ', instructorInfo.ApellidoPaterno)) AS InstructorName,
      (SELECT COUNT(1) FROM capacitacion.SesionParticipante countInfo WHERE countInfo.SesionId = s.SesionId AND countInfo.Rol = 'COLABORADOR') AS ParticipanteCount,
      s.BloqueActualId,
      s.ProgramadaEn,
      s.IniciadaEn,
      s.FinalizadaEn
    FROM capacitacion.Sesion s
    JOIN capacitacion.CursoVersion cv ON cv.CursoVersionId = s.CursoVersionId
    JOIN capacitacion.Curso c ON c.CursoId = cv.CursoId
    JOIN dbo.Capital_Humano instructorInfo ON instructorInfo.ID = s.InstructorEmployeeId AND instructorInfo.RFC = s.Rfc
    """
    // El salto de línea final separa este fragmento del WHERE que le concatenan las consultas.
    + "\n";

  private sealed class SessionStateRow
  {
    public long SesionId { get; set; }
    public int CursoVersionId { get; set; }
    public int InstructorEmployeeId { get; set; }
    public int? BloqueActualId { get; set; }
    public string Estado { get; set; } = string.Empty;
  }

  private sealed class EvaluationHeaderRow
  {
    public int EvaluacionId { get; set; }
    public decimal CalificacionMinima { get; set; }
  }

  private sealed class EvaluationOptionRow
  {
    public int PreguntaId { get; set; }
    public bool Critica { get; set; }
    public int OpcionId { get; set; }
    public bool EsCorrecta { get; set; }
  }

  private sealed class EvaluationAnswerRow
  {
    public int PreguntaId { get; set; }
    public int OpcionId { get; set; }
    public bool EsCorrecta { get; set; }
    public bool Critica { get; set; }
  }

  private sealed class PracticeStepRow
  {
    public int PracticaPasoId { get; set; }
    public bool Critico { get; set; }
  }

  private sealed class AssignmentSignRow
  {
    public long AsignacionId { get; set; }
    public int? InstructorEmployeeId { get; set; }
    public string Estado { get; set; } = string.Empty;
    public decimal Porcentaje { get; set; }
    public long? FirmaInstructorId { get; set; }
  }

  private sealed class ProgressTotalsRow
  {
    public string Estado { get; set; } = string.Empty;
    public int RequiredBlocks { get; set; }
    public int CompletedBlocks { get; set; }
    public int RequiredEvaluations { get; set; }
    public int PassedEvaluations { get; set; }
    public int RequiredPractices { get; set; }
    public int PassedPractices { get; set; }
    public bool HasSignature { get; set; }
  }
}
