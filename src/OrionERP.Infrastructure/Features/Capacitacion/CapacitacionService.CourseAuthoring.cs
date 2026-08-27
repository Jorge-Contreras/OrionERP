using System.Data;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using OrionERP.Application.Features.Capacitacion;

namespace OrionERP.Infrastructure.Features.Capacitacion;

public sealed partial class CapacitacionService
{
  private static readonly HashSet<string> EditableBlockTypes = new(StringComparer.OrdinalIgnoreCase)
  {
    CapacitacionCodes.BloqueTeoria,
    CapacitacionCodes.BloqueObjetivos,
    CapacitacionCodes.BloqueImagen,
    CapacitacionCodes.BloquePasos,
    CapacitacionCodes.BloqueDemostracion,
    CapacitacionCodes.BloquePractica,
    CapacitacionCodes.BloqueEvaluacion,
    CapacitacionCodes.BloqueResumen,
    CapacitacionCodes.BloqueAlerta
  };

  public async Task<IReadOnlyList<CapacitacionCursoAdministrableDto>> GetCursosAdministrablesAsync(
    string rfc,
    CancellationToken ct = default)
  {
    var normalizedRfc = RequireRfc(rfc);
    _ = await RequireCurrentAsync(
      normalizedRfc,
      requireEmployee: true,
      ct,
      CapacitacionCodes.RoleAdmin,
      CapacitacionCodes.RoleInstructor);

    const string sql =
      """
      SELECT
        c.CursoId,
        c.Rfc AS RfcPropietario,
        c.Clave,
        c.Categoria,
        c.Nombre,
        c.Descripcion,
        c.DuracionMinutos,
        c.Activo,
        selectedVersion.CursoVersionId,
        selectedVersion.NumeroVersion,
        selectedVersion.Estado AS EstadoVersion,
        selectedVersion.Objetivos,
        selectedVersion.Prerequisitos,
        selectedVersion.CalificacionMinima,
        selectedVersion.PublicadaEn,
        (SELECT COUNT(1) FROM capacitacion.Leccion lesson WHERE lesson.CursoVersionId = selectedVersion.CursoVersionId) AS LeccionCount,
        (SELECT COUNT(1) FROM capacitacion.Leccion lesson JOIN capacitacion.BloqueContenido blockInfo ON blockInfo.LeccionId = lesson.LeccionId WHERE lesson.CursoVersionId = selectedVersion.CursoVersionId) AS BloqueCount
      FROM capacitacion.Curso c
      CROSS APPLY
      (
        SELECT TOP (1)
          versionInfo.CursoVersionId,
          versionInfo.NumeroVersion,
          versionInfo.Estado,
          versionInfo.Objetivos,
          versionInfo.Prerequisitos,
          versionInfo.CalificacionMinima,
          versionInfo.PublicadaEn
        FROM capacitacion.CursoVersion versionInfo
        WHERE versionInfo.CursoId = c.CursoId
        ORDER BY
          CASE versionInfo.Estado WHEN 'BORRADOR' THEN 0 WHEN 'PUBLICADA' THEN 1 ELSE 2 END,
          versionInfo.NumeroVersion DESC
      ) selectedVersion
      WHERE c.Rfc = @Rfc
         OR
         (
           c.Rfc = '*'
           AND NOT EXISTS
           (
             SELECT 1
             FROM capacitacion.Curso companyCourse
             WHERE companyCourse.Rfc = @Rfc AND companyCourse.Clave = c.Clave
           )
         )
      ORDER BY
        CASE WHEN c.Activo = 1 THEN 0 ELSE 1 END,
        c.Categoria,
        c.Nombre;
      """;

    using var conn = CreateConnection();
    var courses = await conn.QueryAsync<CapacitacionCursoAdministrableDto>(new CommandDefinition(
      sql,
      new { Rfc = normalizedRfc },
      cancellationToken: ct));
    return courses.AsList();
  }

  public async Task<CapacitacionCursoAdministrableDto?> GetCursoAdministrableAsync(
    int cursoId,
    string rfc,
    CancellationToken ct = default)
  {
    if (cursoId <= 0)
      return null;

    var normalizedRfc = RequireRfc(rfc);
    _ = await RequireCurrentAsync(
      normalizedRfc,
      requireEmployee: true,
      ct,
      CapacitacionCodes.RoleAdmin,
      CapacitacionCodes.RoleInstructor);

    const string sql =
      """
      SELECT TOP (1)
        c.CursoId,
        c.Rfc AS RfcPropietario,
        c.Clave,
        c.Categoria,
        c.Nombre,
        c.Descripcion,
        c.DuracionMinutos,
        c.Activo,
        versionInfo.CursoVersionId,
        versionInfo.NumeroVersion,
        versionInfo.Estado AS EstadoVersion,
        versionInfo.Objetivos,
        versionInfo.Prerequisitos,
        versionInfo.CalificacionMinima,
        versionInfo.PublicadaEn,
        (SELECT COUNT(1) FROM capacitacion.Leccion lesson WHERE lesson.CursoVersionId = versionInfo.CursoVersionId) AS LeccionCount,
        (SELECT COUNT(1) FROM capacitacion.Leccion lesson JOIN capacitacion.BloqueContenido blockInfo ON blockInfo.LeccionId = lesson.LeccionId WHERE lesson.CursoVersionId = versionInfo.CursoVersionId) AS BloqueCount
      FROM capacitacion.Curso c
      CROSS APPLY
      (
        SELECT TOP (1) candidate.*
        FROM capacitacion.CursoVersion candidate
        WHERE candidate.CursoId = c.CursoId
        ORDER BY
          CASE candidate.Estado WHEN 'BORRADOR' THEN 0 WHEN 'PUBLICADA' THEN 1 ELSE 2 END,
          candidate.NumeroVersion DESC
      ) versionInfo
      WHERE c.CursoId = @CursoId AND c.Rfc IN (@Rfc, '*');
      """;

    using var conn = CreateConnection();
    var course = await conn.QueryFirstOrDefaultAsync<CapacitacionCursoAdministrableDto>(new CommandDefinition(
      sql,
      new { CursoId = cursoId, Rfc = normalizedRfc },
      cancellationToken: ct));
    if (course is null)
      return null;

    var content = await LoadCourseAsync(
      conn,
      null,
      course.CursoVersionId,
      normalizedRfc,
      allowPinned: true,
      ct);
    if (content is null)
      return null;

    course.Lecciones = content.Lecciones;
    course.Evaluaciones = content.Evaluaciones;
    course.Practicas = content.Practicas;
    return course;
  }

  public async Task<CapacitacionCommandResult> PrepararEdicionCursoAsync(
    CapacitacionCursoCommandRequest request,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = RequireRfc(request.Rfc);
    var actorEmployeeId = RequireEmployeeId(request.ActorEmployeeId, nameof(request.ActorEmployeeId));
    var current = await RequireCurrentAsync(
      rfc,
      requireEmployee: true,
      ct,
      CapacitacionCodes.RoleAdmin,
      CapacitacionCodes.RoleInstructor);
    RequireSelf(current, actorEmployeeId);
    var actor = RequireActor(current.UserName);
    if (request.CursoId <= 0)
      return CapacitacionCommandResult.Fail("Selecciona un curso válido.");

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var sourceCourse = await conn.QueryFirstOrDefaultAsync<CourseOwnerRow>(new CommandDefinition(
        """
        SELECT CursoId, Rfc AS RfcPropietario, Clave, Categoria, Nombre, Descripcion, DuracionMinutos, Activo
        FROM capacitacion.Curso WITH (UPDLOCK, HOLDLOCK)
        WHERE CursoId = @CursoId AND Rfc IN (@Rfc, '*');
        """,
        new { request.CursoId, Rfc = rfc },
        tx,
        cancellationToken: ct));
      if (sourceCourse is null)
      {
        await tx.RollbackAsync(ct);
        return CapacitacionCommandResult.Fail("El curso no existe o no está disponible para la empresa activa.");
      }

      var targetCourseId = sourceCourse.CursoId;
      int sourceVersionId;

      if (string.Equals(sourceCourse.RfcPropietario, CapacitacionCodes.RfcGlobal, StringComparison.OrdinalIgnoreCase))
      {
        var existingCompanyCourse = await conn.QueryFirstOrDefaultAsync<CourseOwnerRow>(new CommandDefinition(
          """
          SELECT CursoId, Rfc AS RfcPropietario, Clave, Categoria, Nombre, Descripcion, DuracionMinutos, Activo
          FROM capacitacion.Curso WITH (UPDLOCK, HOLDLOCK)
          WHERE Rfc = @Rfc AND Clave = @Clave;
          """,
          new { Rfc = rfc, sourceCourse.Clave },
          tx,
          cancellationToken: ct));

        if (existingCompanyCourse is not null)
        {
          if (!existingCompanyCourse.Activo)
          {
            await tx.RollbackAsync(ct);
            return CapacitacionCommandResult.Fail("La empresa ya tiene una versión desactivada de este curso. Reactívala antes de editarla.", existingCompanyCourse.CursoId);
          }
          targetCourseId = existingCompanyCourse.CursoId;
        }
        else
        {
          targetCourseId = await conn.QuerySingleAsync<int>(new CommandDefinition(
            """
            INSERT INTO capacitacion.Curso
              (Rfc, Clave, Categoria, Nombre, Descripcion, DuracionMinutos, Activo, CreadoPor)
            OUTPUT inserted.CursoId
            VALUES
              (@Rfc, @Clave, @Categoria, @Nombre, @Descripcion, @DuracionMinutos, 1, @Actor);
            """,
            new
            {
              Rfc = rfc,
              sourceCourse.Clave,
              sourceCourse.Categoria,
              sourceCourse.Nombre,
              sourceCourse.Descripcion,
              sourceCourse.DuracionMinutos,
              Actor = actor
            },
            tx,
            cancellationToken: ct));
        }

        sourceVersionId = await conn.QueryFirstOrDefaultAsync<int>(new CommandDefinition(
          """
          SELECT TOP (1) CursoVersionId
          FROM capacitacion.CursoVersion
          WHERE CursoId = @CursoId AND Estado IN ('PUBLICADA','RETIRADA')
          ORDER BY CASE Estado WHEN 'PUBLICADA' THEN 0 ELSE 1 END, NumeroVersion DESC;
          """,
          new { CursoId = sourceCourse.CursoId },
          tx,
          cancellationToken: ct));
      }
      else
      {
        if (!sourceCourse.Activo)
        {
          await tx.RollbackAsync(ct);
          return CapacitacionCommandResult.Fail("Reactiva el curso antes de editarlo.", sourceCourse.CursoId);
        }

        sourceVersionId = await conn.QueryFirstOrDefaultAsync<int>(new CommandDefinition(
          """
          SELECT TOP (1) CursoVersionId
          FROM capacitacion.CursoVersion
          WHERE CursoId = @CursoId AND Estado IN ('PUBLICADA','RETIRADA')
          ORDER BY CASE Estado WHEN 'PUBLICADA' THEN 0 ELSE 1 END, NumeroVersion DESC;
          """,
          new { CursoId = targetCourseId },
          tx,
          cancellationToken: ct));
      }

      var existingDraftId = await conn.QueryFirstOrDefaultAsync<int>(new CommandDefinition(
        """
        SELECT TOP (1) CursoVersionId
        FROM capacitacion.CursoVersion WITH (UPDLOCK, HOLDLOCK)
        WHERE CursoId = @CursoId AND Estado = 'BORRADOR'
        ORDER BY NumeroVersion DESC;
        """,
        new { CursoId = targetCourseId },
        tx,
        cancellationToken: ct));
      if (existingDraftId > 0)
      {
        await tx.CommitAsync(ct);
        return CapacitacionCommandResult.Ok("El borrador está listo para editarse.", targetCourseId);
      }

      if (sourceVersionId <= 0)
      {
        await tx.RollbackAsync(ct);
        return CapacitacionCommandResult.Fail("El curso no tiene una versión publicada que se pueda preparar para edición.");
      }

      var nextVersion = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
        "SELECT ISNULL(MAX(NumeroVersion), 0) + 1 FROM capacitacion.CursoVersion WITH (UPDLOCK, HOLDLOCK) WHERE CursoId = @CursoId;",
        new { CursoId = targetCourseId },
        tx,
        cancellationToken: ct));
      _ = await CloneCourseVersionAsync(
        conn,
        tx,
        sourceVersionId,
        targetCourseId,
        nextVersion,
        actor,
        ct);

      await AddAuditAsync(
        conn,
        tx,
        rfc,
        "CURSO",
        targetCourseId,
        "BORRADOR_PREPARADO",
        $"Se preparó la versión {nextVersion} desde la versión {sourceVersionId}.",
        actorEmployeeId,
        actor,
        ct);
      await tx.CommitAsync(ct);
      return CapacitacionCommandResult.Ok("Se creó un borrador editable sin modificar la versión publicada.", targetCourseId);
    }
    catch (SqlException exception) when (exception.Number is 2601 or 2627)
    {
      await tx.RollbackAsync(ct);
      return CapacitacionCommandResult.Fail("Ya existe un curso o borrador con esa clave para la empresa activa.");
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<CapacitacionCommandResult> GuardarCursoAsync(
    CapacitacionGuardarCursoRequest request,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = RequireRfc(request.Rfc);
    var actorEmployeeId = RequireEmployeeId(request.ActorEmployeeId, nameof(request.ActorEmployeeId));
    var current = await RequireCurrentAsync(
      rfc,
      requireEmployee: true,
      ct,
      CapacitacionCodes.RoleAdmin,
      CapacitacionCodes.RoleInstructor);
    RequireSelf(current, actorEmployeeId);
    var actor = RequireActor(current.UserName);

    var validationError = ValidateCourseDraft(request);
    if (validationError is not null)
      return CapacitacionCommandResult.Fail(validationError, request.CursoId > 0 ? request.CursoId : null);

    NormalizeCourseDraft(request);

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var courseId = request.CursoId;
      var versionId = request.CursoVersionId;
      var created = courseId <= 0;

      if (created)
      {
        courseId = await conn.QuerySingleAsync<int>(new CommandDefinition(
          """
          INSERT INTO capacitacion.Curso
            (Rfc, Clave, Categoria, Nombre, Descripcion, DuracionMinutos, Activo, CreadoPor)
          OUTPUT inserted.CursoId
          VALUES
            (@Rfc, @Clave, @Categoria, @Nombre, @Descripcion, @DuracionMinutos, 1, @Actor);
          """,
          new
          {
            Rfc = rfc,
            request.Clave,
            request.Categoria,
            request.Nombre,
            request.Descripcion,
            request.DuracionMinutos,
            Actor = actor
          },
          tx,
          cancellationToken: ct));

        versionId = await conn.QuerySingleAsync<int>(new CommandDefinition(
          """
          INSERT INTO capacitacion.CursoVersion
            (CursoId, NumeroVersion, Estado, Objetivos, Prerequisitos, CalificacionMinima, CreadaPor)
          OUTPUT inserted.CursoVersionId
          VALUES
            (@CursoId, 1, 'BORRADOR', @Objetivos, @Prerequisitos, @CalificacionMinima, @Actor);
          """,
          new
          {
            CursoId = courseId,
            request.Objetivos,
            request.Prerequisitos,
            request.CalificacionMinima,
            Actor = actor
          },
          tx,
          cancellationToken: ct));
      }
      else
      {
        var draftMatches = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
          """
          SELECT COUNT(1)
          FROM capacitacion.Curso c WITH (UPDLOCK, HOLDLOCK)
          JOIN capacitacion.CursoVersion versionInfo WITH (UPDLOCK, HOLDLOCK) ON versionInfo.CursoId = c.CursoId
          WHERE c.CursoId = @CursoId AND c.Rfc = @Rfc AND c.Activo = 1
            AND versionInfo.CursoVersionId = @CursoVersionId AND versionInfo.Estado = 'BORRADOR';
          """,
          new { CursoId = courseId, CursoVersionId = versionId, Rfc = rfc },
          tx,
          cancellationToken: ct));
        if (draftMatches != 1)
        {
          await tx.RollbackAsync(ct);
          return CapacitacionCommandResult.Fail("La versión seleccionada no es un borrador editable de la empresa activa.", courseId);
        }

        await conn.ExecuteAsync(new CommandDefinition(
          """
          UPDATE capacitacion.Curso
          SET Clave = @Clave,
              Categoria = @Categoria,
              Nombre = @Nombre,
              Descripcion = @Descripcion,
              DuracionMinutos = @DuracionMinutos
          WHERE CursoId = @CursoId AND Rfc = @Rfc;

          UPDATE capacitacion.CursoVersion
          SET Objetivos = @Objetivos,
              Prerequisitos = @Prerequisitos,
              CalificacionMinima = @CalificacionMinima
          WHERE CursoVersionId = @CursoVersionId AND CursoId = @CursoId AND Estado = 'BORRADOR';
          """,
          new
          {
            CursoId = courseId,
            CursoVersionId = versionId,
            Rfc = rfc,
            request.Clave,
            request.Categoria,
            request.Nombre,
            request.Descripcion,
            request.DuracionMinutos,
            request.Objetivos,
            request.Prerequisitos,
            request.CalificacionMinima
          },
          tx,
          cancellationToken: ct));
      }

      await SaveDraftStructureAsync(conn, tx, versionId, request.Lecciones, ct);
      await AddAuditAsync(
        conn,
        tx,
        rfc,
        "CURSO",
        courseId,
        created ? "CREADO" : "BORRADOR_GUARDADO",
        $"{request.Lecciones.Count} lección(es) y {request.Lecciones.Sum(lesson => lesson.Bloques.Count)} bloque(s).",
        actorEmployeeId,
        actor,
        ct);
      await tx.CommitAsync(ct);
      return CapacitacionCommandResult.Ok(
        created ? "Curso creado como borrador." : "Borrador guardado.",
        courseId);
    }
    catch (SqlException exception) when (exception.Number is 2601 or 2627)
    {
      await tx.RollbackAsync(ct);
      return CapacitacionCommandResult.Fail("La clave, el orden o el nombre de un elemento ya está en uso dentro del curso.", request.CursoId > 0 ? request.CursoId : null);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<CapacitacionCommandResult> PublicarCursoAsync(
    CapacitacionCursoCommandRequest request,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = RequireRfc(request.Rfc);
    var actorEmployeeId = RequireEmployeeId(request.ActorEmployeeId, nameof(request.ActorEmployeeId));
    var current = await RequireCurrentAsync(
      rfc,
      requireEmployee: true,
      ct,
      CapacitacionCodes.RoleAdmin,
      CapacitacionCodes.RoleInstructor);
    RequireSelf(current, actorEmployeeId);
    var actor = RequireActor(current.UserName);
    if (request.CursoId <= 0)
      return CapacitacionCommandResult.Fail("Selecciona un curso válido.");

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var draft = await conn.QueryFirstOrDefaultAsync<PublishableCourseRow>(new CommandDefinition(
        """
        SELECT TOP (1)
          c.CursoId,
          versionInfo.CursoVersionId,
          versionInfo.NumeroVersion,
          (SELECT COUNT(1) FROM capacitacion.Leccion lesson WHERE lesson.CursoVersionId = versionInfo.CursoVersionId) AS LeccionCount,
          (SELECT COUNT(1) FROM capacitacion.Leccion lesson JOIN capacitacion.BloqueContenido blockInfo ON blockInfo.LeccionId = lesson.LeccionId WHERE lesson.CursoVersionId = versionInfo.CursoVersionId) AS BloqueCount,
          (SELECT COUNT(1) FROM capacitacion.Leccion lesson WHERE lesson.CursoVersionId = versionInfo.CursoVersionId AND NOT EXISTS (SELECT 1 FROM capacitacion.BloqueContenido blockInfo WHERE blockInfo.LeccionId = lesson.LeccionId)) AS EmptyLessonCount
        FROM capacitacion.Curso c WITH (UPDLOCK, HOLDLOCK)
        JOIN capacitacion.CursoVersion versionInfo WITH (UPDLOCK, HOLDLOCK) ON versionInfo.CursoId = c.CursoId
        WHERE c.CursoId = @CursoId AND c.Rfc = @Rfc AND c.Activo = 1 AND versionInfo.Estado = 'BORRADOR'
        ORDER BY versionInfo.NumeroVersion DESC;
        """,
        new { request.CursoId, Rfc = rfc },
        tx,
        cancellationToken: ct));
      if (draft is null)
      {
        await tx.RollbackAsync(ct);
        return CapacitacionCommandResult.Fail("El curso no tiene un borrador publicable de la empresa activa.", request.CursoId);
      }
      if (draft.LeccionCount == 0 || draft.BloqueCount == 0 || draft.EmptyLessonCount > 0)
      {
        await tx.RollbackAsync(ct);
        return CapacitacionCommandResult.Fail("Cada lección necesita al menos un bloque antes de publicar.", request.CursoId);
      }

      await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE capacitacion.CursoVersion
        SET Estado = 'RETIRADA'
        WHERE CursoId = @CursoId AND Estado = 'PUBLICADA';

        UPDATE capacitacion.CursoVersion
        SET Estado = 'PUBLICADA', PublicadaEn = SYSUTCDATETIME(), PublicadaPor = @Actor
        WHERE CursoVersionId = @CursoVersionId AND CursoId = @CursoId AND Estado = 'BORRADOR';
        """,
        new { CursoId = draft.CursoId, draft.CursoVersionId, Actor = actor },
        tx,
        cancellationToken: ct));
      await AddAuditAsync(
        conn,
        tx,
        rfc,
        "CURSO",
        draft.CursoId,
        "PUBLICADO",
        $"Se publicó la versión {draft.NumeroVersion}.",
        actorEmployeeId,
        actor,
        ct);
      await tx.CommitAsync(ct);
      return CapacitacionCommandResult.Ok($"Versión {draft.NumeroVersion} publicada.", draft.CursoId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<CapacitacionCommandResult> CambiarEstadoCursoAsync(
    CapacitacionCambiarEstadoCursoRequest request,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = RequireRfc(request.Rfc);
    var actorEmployeeId = RequireEmployeeId(request.ActorEmployeeId, nameof(request.ActorEmployeeId));
    var current = await RequireCurrentAsync(
      rfc,
      requireEmployee: true,
      ct,
      CapacitacionCodes.RoleAdmin,
      CapacitacionCodes.RoleInstructor);
    RequireSelf(current, actorEmployeeId);
    var actor = RequireActor(current.UserName);
    if (request.CursoId <= 0)
      return CapacitacionCommandResult.Fail("Selecciona un curso válido.");

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var affected = await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE capacitacion.Curso
        SET Activo = @Activo
        WHERE CursoId = @CursoId AND Rfc = @Rfc AND Activo <> @Activo;
        """,
        new { request.CursoId, Rfc = rfc, request.Activo },
        tx,
        cancellationToken: ct));
      if (affected == 0)
      {
        var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
          "SELECT COUNT(1) FROM capacitacion.Curso WHERE CursoId = @CursoId AND Rfc = @Rfc;",
          new { request.CursoId, Rfc = rfc },
          tx,
          cancellationToken: ct));
        await tx.RollbackAsync(ct);
        return exists == 1
          ? CapacitacionCommandResult.Ok(request.Activo ? "El curso ya estaba activo." : "El curso ya estaba desactivado.", request.CursoId)
          : CapacitacionCommandResult.Fail("Los cursos generales de OrionERP no se pueden desactivar; personalízalos para tu empresa.");
      }

      await AddAuditAsync(
        conn,
        tx,
        rfc,
        "CURSO",
        request.CursoId,
        request.Activo ? "REACTIVADO" : "DESACTIVADO",
        request.Activo ? "El curso volvió a estar disponible." : "El curso se retiró del catálogo sin eliminar su historial.",
        actorEmployeeId,
        actor,
        ct);
      await tx.CommitAsync(ct);
      return CapacitacionCommandResult.Ok(
        request.Activo ? "Curso reactivado." : "Curso desactivado. Su historial y asignaciones se conservaron.",
        request.CursoId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  private static string? ValidateCourseDraft(CapacitacionGuardarCursoRequest request)
  {
    if (NullIfWhiteSpace(request.Clave) is not { Length: <= 64 })
      return "Indica una clave de hasta 64 caracteres.";
    if (NullIfWhiteSpace(request.Categoria) is not { Length: <= 80 })
      return "Indica una categoría de hasta 80 caracteres.";
    if (NullIfWhiteSpace(request.Nombre) is not { Length: <= 160 })
      return "Indica un nombre de hasta 160 caracteres.";
    if (NullIfWhiteSpace(request.Descripcion) is not { Length: <= 1000 })
      return "Indica una descripción de hasta 1,000 caracteres.";
    if (request.DuracionMinutos is < 1 or > 10080)
      return "La duración debe estar entre 1 minuto y 7 días.";
    if (NullIfWhiteSpace(request.Objetivos) is not { Length: <= 2000 })
      return "Indica objetivos de hasta 2,000 caracteres.";
    if (NullIfWhiteSpace(request.Prerequisitos) is { Length: > 1000 })
      return "Los prerrequisitos no pueden superar 1,000 caracteres.";
    if (request.CalificacionMinima is < 0 or > 100)
      return "La calificación mínima debe estar entre 0 y 100.";
    if (request.Lecciones.Count > 100)
      return "Un curso no puede tener más de 100 lecciones.";
    if (request.Lecciones.Where(lesson => lesson.LeccionId > 0).GroupBy(lesson => lesson.LeccionId).Any(group => group.Count() > 1))
      return "Una lección aparece más de una vez en el borrador.";
    if (request.Lecciones.Select(lesson => NullIfWhiteSpace(lesson.Clave)).Where(value => value is not null).GroupBy(value => value!, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
      return "Cada lección debe tener una clave distinta.";

    foreach (var lesson in request.Lecciones)
    {
      if (NullIfWhiteSpace(lesson.Clave) is not { Length: <= 64 })
        return "Cada lección necesita una clave de hasta 64 caracteres.";
      if (NullIfWhiteSpace(lesson.Titulo) is not { Length: <= 160 })
        return "Cada lección necesita un título de hasta 160 caracteres.";
      if (NullIfWhiteSpace(lesson.Objetivo) is not { Length: <= 1000 })
        return "Cada lección necesita un objetivo de hasta 1,000 caracteres.";
      if (lesson.DuracionMinutos is < 1 or > 1440)
        return "La duración de cada lección debe estar entre 1 minuto y 24 horas.";
      if (lesson.Bloques.Count > 200)
        return "Una lección no puede tener más de 200 bloques.";
      if (lesson.Bloques.Where(blockInfo => blockInfo.BloqueId > 0).GroupBy(blockInfo => blockInfo.BloqueId).Any(group => group.Count() > 1))
        return "Un bloque aparece más de una vez en el borrador.";

      foreach (var blockInfo in lesson.Bloques)
      {
        if (!EditableBlockTypes.Contains(blockInfo.Tipo))
          return "Selecciona un tipo válido para cada bloque.";
        if (NullIfWhiteSpace(blockInfo.Titulo) is not { Length: <= 160 })
          return "Cada bloque necesita un título de hasta 160 caracteres.";
        if (NullIfWhiteSpace(blockInfo.Contenido) is null)
          return "Cada bloque necesita contenido.";
        if (!string.IsNullOrWhiteSpace(blockInfo.ConfiguracionJson))
        {
          try { using var _ = JsonDocument.Parse(blockInfo.ConfiguracionJson); }
          catch (JsonException) { return "La configuración avanzada de un bloque no contiene JSON válido."; }
        }
      }
    }

    return null;
  }

  private static void NormalizeCourseDraft(CapacitacionGuardarCursoRequest request)
  {
    request.Clave = request.Clave.Trim().ToUpperInvariant();
    request.Categoria = request.Categoria.Trim();
    request.Nombre = request.Nombre.Trim();
    request.Descripcion = request.Descripcion.Trim();
    request.Objetivos = request.Objetivos.Trim();
    request.Prerequisitos = NullIfWhiteSpace(request.Prerequisitos);

    for (var lessonIndex = 0; lessonIndex < request.Lecciones.Count; lessonIndex++)
    {
      var lesson = request.Lecciones[lessonIndex];
      lesson.Orden = lessonIndex + 1;
      lesson.Clave = lesson.Clave.Trim().ToUpperInvariant();
      lesson.Titulo = lesson.Titulo.Trim();
      lesson.Objetivo = lesson.Objetivo.Trim();
      for (var blockIndex = 0; blockIndex < lesson.Bloques.Count; blockIndex++)
      {
        var blockInfo = lesson.Bloques[blockIndex];
        blockInfo.Orden = blockIndex + 1;
        blockInfo.Tipo = blockInfo.Tipo.Trim().ToUpperInvariant();
        blockInfo.Titulo = blockInfo.Titulo.Trim();
        blockInfo.Contenido = blockInfo.Contenido.Trim();
        blockInfo.ConfiguracionJson = NullIfWhiteSpace(blockInfo.ConfiguracionJson);
      }
    }
  }

  private static async Task SaveDraftStructureAsync(
    DbConnection conn,
    IDbTransaction tx,
    int versionId,
    IReadOnlyList<CapacitacionGuardarLeccionRequest> lessons,
    CancellationToken ct)
  {
    const string currentSql =
      """
      SELECT LeccionId, CursoVersionId
      FROM capacitacion.Leccion WITH (UPDLOCK, HOLDLOCK)
      WHERE CursoVersionId = @CursoVersionId;

      SELECT blockInfo.BloqueId, blockInfo.LeccionId
      FROM capacitacion.Leccion lesson WITH (UPDLOCK, HOLDLOCK)
      JOIN capacitacion.BloqueContenido blockInfo WITH (UPDLOCK, HOLDLOCK) ON blockInfo.LeccionId = lesson.LeccionId
      WHERE lesson.CursoVersionId = @CursoVersionId;
      """;
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(
      currentSql,
      new { CursoVersionId = versionId },
      tx,
      cancellationToken: ct));
    var currentLessons = (await multi.ReadAsync<StructureLessonRow>()).AsList();
    var currentBlocks = (await multi.ReadAsync<StructureBlockRow>()).AsList();
    var currentLessonIds = currentLessons.Select(item => item.LeccionId).ToHashSet();
    var currentBlockOwners = currentBlocks.ToDictionary(item => item.BloqueId, item => item.LeccionId);

    var requestedLessonIds = lessons.Where(item => item.LeccionId > 0).Select(item => item.LeccionId).ToHashSet();
    if (!requestedLessonIds.IsSubsetOf(currentLessonIds))
      throw new InvalidOperationException("El borrador contiene una lección que no pertenece a la versión editable.");

    foreach (var lesson in lessons)
    {
      foreach (var blockInfo in lesson.Bloques.Where(item => item.BloqueId > 0))
      {
        if (!currentBlockOwners.TryGetValue(blockInfo.BloqueId, out var ownerLessonId)
            || ownerLessonId != lesson.LeccionId)
          throw new InvalidOperationException("El borrador contiene un bloque que no pertenece a la lección editable.");
      }
    }

    var requestedBlockIds = lessons
      .SelectMany(item => item.Bloques)
      .Where(item => item.BloqueId > 0)
      .Select(item => item.BloqueId)
      .ToHashSet();
    foreach (var blockId in currentBlockOwners.Keys.Where(id => !requestedBlockIds.Contains(id)))
    {
      await conn.ExecuteAsync(new CommandDefinition(
        """
        DELETE FROM capacitacion.Recurso WHERE BloqueId = @BloqueId;
        DELETE FROM capacitacion.BloqueContenido WHERE BloqueId = @BloqueId;
        """,
        new { BloqueId = blockId },
        tx,
        cancellationToken: ct));
    }

    foreach (var lessonId in currentLessonIds.Where(id => !requestedLessonIds.Contains(id)))
    {
      await conn.ExecuteAsync(new CommandDefinition(
        "DELETE FROM capacitacion.Leccion WHERE LeccionId = @LeccionId AND CursoVersionId = @CursoVersionId;",
        new { LeccionId = lessonId, CursoVersionId = versionId },
        tx,
        cancellationToken: ct));
    }

    await conn.ExecuteAsync(new CommandDefinition(
      """
      UPDATE capacitacion.BloqueContenido
      SET Orden = Orden + 100000
      WHERE LeccionId IN (SELECT LeccionId FROM capacitacion.Leccion WHERE CursoVersionId = @CursoVersionId);

      UPDATE capacitacion.Leccion
      SET Orden = Orden + 100000
      WHERE CursoVersionId = @CursoVersionId;
      """,
      new { CursoVersionId = versionId },
      tx,
      cancellationToken: ct));

    foreach (var lesson in lessons)
    {
      if (lesson.LeccionId <= 0)
      {
        lesson.LeccionId = await conn.QuerySingleAsync<int>(new CommandDefinition(
          """
          INSERT INTO capacitacion.Leccion
            (CursoVersionId, Orden, Clave, Titulo, Objetivo, DuracionMinutos, Requerida)
          OUTPUT inserted.LeccionId
          VALUES
            (@CursoVersionId, @Orden, @Clave, @Titulo, @Objetivo, @DuracionMinutos, @Requerida);
          """,
          new
          {
            CursoVersionId = versionId,
            lesson.Orden,
            lesson.Clave,
            lesson.Titulo,
            lesson.Objetivo,
            lesson.DuracionMinutos,
            lesson.Requerida
          },
          tx,
          cancellationToken: ct));
      }
      else
      {
        await conn.ExecuteAsync(new CommandDefinition(
          """
          UPDATE capacitacion.Leccion
          SET Orden = @Orden,
              Clave = @Clave,
              Titulo = @Titulo,
              Objetivo = @Objetivo,
              DuracionMinutos = @DuracionMinutos,
              Requerida = @Requerida
          WHERE LeccionId = @LeccionId AND CursoVersionId = @CursoVersionId;
          """,
          new
          {
            CursoVersionId = versionId,
            lesson.LeccionId,
            lesson.Orden,
            lesson.Clave,
            lesson.Titulo,
            lesson.Objetivo,
            lesson.DuracionMinutos,
            lesson.Requerida
          },
          tx,
          cancellationToken: ct));
      }

      foreach (var blockInfo in lesson.Bloques)
      {
        if (blockInfo.BloqueId <= 0)
        {
          blockInfo.BloqueId = await conn.QuerySingleAsync<int>(new CommandDefinition(
            """
            INSERT INTO capacitacion.BloqueContenido
              (LeccionId, Orden, Tipo, Titulo, Contenido, ConfiguracionJson, Requerido)
            OUTPUT inserted.BloqueId
            VALUES
              (@LeccionId, @Orden, @Tipo, @Titulo, @Contenido, @ConfiguracionJson, @Requerido);
            """,
            new
            {
              lesson.LeccionId,
              blockInfo.Orden,
              blockInfo.Tipo,
              blockInfo.Titulo,
              blockInfo.Contenido,
              blockInfo.ConfiguracionJson,
              blockInfo.Requerido
            },
            tx,
            cancellationToken: ct));
        }
        else
        {
          await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE capacitacion.BloqueContenido
            SET Orden = @Orden,
                Tipo = @Tipo,
                Titulo = @Titulo,
                Contenido = @Contenido,
                ConfiguracionJson = @ConfiguracionJson,
                Requerido = @Requerido
            WHERE BloqueId = @BloqueId AND LeccionId = @LeccionId;
            """,
            new
            {
              lesson.LeccionId,
              blockInfo.BloqueId,
              blockInfo.Orden,
              blockInfo.Tipo,
              blockInfo.Titulo,
              blockInfo.Contenido,
              blockInfo.ConfiguracionJson,
              blockInfo.Requerido
            },
            tx,
            cancellationToken: ct));
        }
      }
    }
  }

  private static async Task<int> CloneCourseVersionAsync(
    DbConnection conn,
    IDbTransaction tx,
    int sourceVersionId,
    int targetCourseId,
    int targetVersionNumber,
    string actor,
    CancellationToken ct)
  {
    var sourceVersion = await conn.QuerySingleAsync<CloneVersionRow>(new CommandDefinition(
      "SELECT Objetivos, Prerequisitos, CalificacionMinima FROM capacitacion.CursoVersion WHERE CursoVersionId = @CursoVersionId;",
      new { CursoVersionId = sourceVersionId },
      tx,
      cancellationToken: ct));
    var targetVersionId = await conn.QuerySingleAsync<int>(new CommandDefinition(
      """
      INSERT INTO capacitacion.CursoVersion
        (CursoId, NumeroVersion, Estado, Objetivos, Prerequisitos, CalificacionMinima, CreadaPor)
      OUTPUT inserted.CursoVersionId
      VALUES
        (@CursoId, @NumeroVersion, 'BORRADOR', @Objetivos, @Prerequisitos, @CalificacionMinima, @Actor);
      """,
      new
      {
        CursoId = targetCourseId,
        NumeroVersion = targetVersionNumber,
        sourceVersion.Objetivos,
        sourceVersion.Prerequisitos,
        sourceVersion.CalificacionMinima,
        Actor = actor
      },
      tx,
      cancellationToken: ct));

    var lessonMap = new Dictionary<int, int>();
    var lessons = await conn.QueryAsync<CloneLessonRow>(new CommandDefinition(
      "SELECT LeccionId, Orden, Clave, Titulo, Objetivo, DuracionMinutos, Requerida FROM capacitacion.Leccion WHERE CursoVersionId = @CursoVersionId ORDER BY Orden;",
      new { CursoVersionId = sourceVersionId },
      tx,
      cancellationToken: ct));
    foreach (var lesson in lessons)
    {
      var newId = await conn.QuerySingleAsync<int>(new CommandDefinition(
        """
        INSERT INTO capacitacion.Leccion (CursoVersionId, Orden, Clave, Titulo, Objetivo, DuracionMinutos, Requerida)
        OUTPUT inserted.LeccionId
        VALUES (@CursoVersionId, @Orden, @Clave, @Titulo, @Objetivo, @DuracionMinutos, @Requerida);
        """,
        new
        {
          CursoVersionId = targetVersionId,
          lesson.Orden,
          lesson.Clave,
          lesson.Titulo,
          lesson.Objetivo,
          lesson.DuracionMinutos,
          lesson.Requerida
        },
        tx,
        cancellationToken: ct));
      lessonMap[lesson.LeccionId] = newId;
    }

    var blockMap = new Dictionary<int, int>();
    var blocks = await conn.QueryAsync<CloneBlockRow>(new CommandDefinition(
      """
      SELECT blockInfo.BloqueId, blockInfo.LeccionId, blockInfo.Orden, blockInfo.Tipo, blockInfo.Titulo, blockInfo.Contenido, blockInfo.ConfiguracionJson, blockInfo.Requerido
      FROM capacitacion.Leccion lesson
      JOIN capacitacion.BloqueContenido blockInfo ON blockInfo.LeccionId = lesson.LeccionId
      WHERE lesson.CursoVersionId = @CursoVersionId
      ORDER BY lesson.Orden, blockInfo.Orden;
      """,
      new { CursoVersionId = sourceVersionId },
      tx,
      cancellationToken: ct));
    foreach (var blockInfo in blocks)
    {
      var newId = await conn.QuerySingleAsync<int>(new CommandDefinition(
        """
        INSERT INTO capacitacion.BloqueContenido (LeccionId, Orden, Tipo, Titulo, Contenido, ConfiguracionJson, Requerido)
        OUTPUT inserted.BloqueId
        VALUES (@LeccionId, @Orden, @Tipo, @Titulo, @Contenido, @ConfiguracionJson, @Requerido);
        """,
        new
        {
          LeccionId = lessonMap[blockInfo.LeccionId],
          blockInfo.Orden,
          blockInfo.Tipo,
          blockInfo.Titulo,
          blockInfo.Contenido,
          blockInfo.ConfiguracionJson,
          blockInfo.Requerido
        },
        tx,
        cancellationToken: ct));
      blockMap[blockInfo.BloqueId] = newId;
    }

    var resources = await conn.QueryAsync<CloneResourceRow>(new CommandDefinition(
      """
      SELECT resource.RecursoId, resource.BloqueId, resource.Orden, resource.Tipo, resource.Titulo, resource.Ruta,
             resource.TextoAlternativo, resource.HashContenido, resource.CapturadoEn, resource.VersionAplicacion
      FROM capacitacion.Leccion lesson
      JOIN capacitacion.BloqueContenido blockInfo ON blockInfo.LeccionId = lesson.LeccionId
      JOIN capacitacion.Recurso resource ON resource.BloqueId = blockInfo.BloqueId
      WHERE lesson.CursoVersionId = @CursoVersionId
      ORDER BY lesson.Orden, blockInfo.Orden, resource.Orden;
      """,
      new { CursoVersionId = sourceVersionId },
      tx,
      cancellationToken: ct));
    foreach (var resource in resources)
    {
      await conn.ExecuteAsync(new CommandDefinition(
        """
        INSERT INTO capacitacion.Recurso
          (BloqueId, Orden, Tipo, Titulo, Ruta, TextoAlternativo, HashContenido, CapturadoEn, VersionAplicacion)
        VALUES
          (@BloqueId, @Orden, @Tipo, @Titulo, @Ruta, @TextoAlternativo, @HashContenido, @CapturadoEn, @VersionAplicacion);
        """,
        new
        {
          BloqueId = blockMap[resource.BloqueId],
          resource.Orden,
          resource.Tipo,
          resource.Titulo,
          resource.Ruta,
          resource.TextoAlternativo,
          resource.HashContenido,
          resource.CapturadoEn,
          resource.VersionAplicacion
        },
        tx,
        cancellationToken: ct));
    }

    var evaluationMap = new Dictionary<int, int>();
    var evaluations = await conn.QueryAsync<CloneEvaluationRow>(new CommandDefinition(
      "SELECT EvaluacionId, Titulo, Instrucciones, CalificacionMinima, Requerida FROM capacitacion.Evaluacion WHERE CursoVersionId = @CursoVersionId ORDER BY EvaluacionId;",
      new { CursoVersionId = sourceVersionId },
      tx,
      cancellationToken: ct));
    foreach (var evaluation in evaluations)
    {
      var newId = await conn.QuerySingleAsync<int>(new CommandDefinition(
        """
        INSERT INTO capacitacion.Evaluacion (CursoVersionId, Titulo, Instrucciones, CalificacionMinima, Requerida)
        OUTPUT inserted.EvaluacionId
        VALUES (@CursoVersionId, @Titulo, @Instrucciones, @CalificacionMinima, @Requerida);
        """,
        new
        {
          CursoVersionId = targetVersionId,
          evaluation.Titulo,
          evaluation.Instrucciones,
          evaluation.CalificacionMinima,
          evaluation.Requerida
        },
        tx,
        cancellationToken: ct));
      evaluationMap[evaluation.EvaluacionId] = newId;
    }

    var questionMap = new Dictionary<int, int>();
    var questions = await conn.QueryAsync<CloneQuestionRow>(new CommandDefinition(
      """
      SELECT question.PreguntaId, question.EvaluacionId, question.Orden, question.Texto, question.Explicacion, question.Critica
      FROM capacitacion.Evaluacion evaluation
      JOIN capacitacion.Pregunta question ON question.EvaluacionId = evaluation.EvaluacionId
      WHERE evaluation.CursoVersionId = @CursoVersionId
      ORDER BY evaluation.EvaluacionId, question.Orden;
      """,
      new { CursoVersionId = sourceVersionId },
      tx,
      cancellationToken: ct));
    foreach (var question in questions)
    {
      var newId = await conn.QuerySingleAsync<int>(new CommandDefinition(
        """
        INSERT INTO capacitacion.Pregunta (EvaluacionId, Orden, Texto, Explicacion, Critica)
        OUTPUT inserted.PreguntaId
        VALUES (@EvaluacionId, @Orden, @Texto, @Explicacion, @Critica);
        """,
        new
        {
          EvaluacionId = evaluationMap[question.EvaluacionId],
          question.Orden,
          question.Texto,
          question.Explicacion,
          question.Critica
        },
        tx,
        cancellationToken: ct));
      questionMap[question.PreguntaId] = newId;
    }

    var options = await conn.QueryAsync<CloneOptionRow>(new CommandDefinition(
      """
      SELECT optionInfo.OpcionId, optionInfo.PreguntaId, optionInfo.Orden, optionInfo.Texto, optionInfo.EsCorrecta
      FROM capacitacion.Evaluacion evaluation
      JOIN capacitacion.Pregunta question ON question.EvaluacionId = evaluation.EvaluacionId
      JOIN capacitacion.OpcionPregunta optionInfo ON optionInfo.PreguntaId = question.PreguntaId
      WHERE evaluation.CursoVersionId = @CursoVersionId
      ORDER BY evaluation.EvaluacionId, question.Orden, optionInfo.Orden;
      """,
      new { CursoVersionId = sourceVersionId },
      tx,
      cancellationToken: ct));
    foreach (var optionInfo in options)
    {
      await conn.ExecuteAsync(new CommandDefinition(
        "INSERT INTO capacitacion.OpcionPregunta (PreguntaId, Orden, Texto, EsCorrecta) VALUES (@PreguntaId, @Orden, @Texto, @EsCorrecta);",
        new
        {
          PreguntaId = questionMap[optionInfo.PreguntaId],
          optionInfo.Orden,
          optionInfo.Texto,
          optionInfo.EsCorrecta
        },
        tx,
        cancellationToken: ct));
    }

    var practiceMap = new Dictionary<int, int>();
    var practices = await conn.QueryAsync<ClonePracticeRow>(new CommandDefinition(
      "SELECT PracticaId, Titulo, Instrucciones, RutaSandbox, Requerida FROM capacitacion.Practica WHERE CursoVersionId = @CursoVersionId ORDER BY PracticaId;",
      new { CursoVersionId = sourceVersionId },
      tx,
      cancellationToken: ct));
    foreach (var practice in practices)
    {
      var newId = await conn.QuerySingleAsync<int>(new CommandDefinition(
        """
        INSERT INTO capacitacion.Practica (CursoVersionId, Titulo, Instrucciones, RutaSandbox, Requerida)
        OUTPUT inserted.PracticaId
        VALUES (@CursoVersionId, @Titulo, @Instrucciones, @RutaSandbox, @Requerida);
        """,
        new
        {
          CursoVersionId = targetVersionId,
          practice.Titulo,
          practice.Instrucciones,
          practice.RutaSandbox,
          practice.Requerida
        },
        tx,
        cancellationToken: ct));
      practiceMap[practice.PracticaId] = newId;
    }

    var practiceSteps = await conn.QueryAsync<ClonePracticeStepRow>(new CommandDefinition(
      """
      SELECT stepInfo.PracticaPasoId, stepInfo.PracticaId, stepInfo.Orden, stepInfo.Descripcion, stepInfo.Critico
      FROM capacitacion.Practica practice
      JOIN capacitacion.PracticaPaso stepInfo ON stepInfo.PracticaId = practice.PracticaId
      WHERE practice.CursoVersionId = @CursoVersionId
      ORDER BY practice.PracticaId, stepInfo.Orden;
      """,
      new { CursoVersionId = sourceVersionId },
      tx,
      cancellationToken: ct));
    foreach (var stepInfo in practiceSteps)
    {
      await conn.ExecuteAsync(new CommandDefinition(
        "INSERT INTO capacitacion.PracticaPaso (PracticaId, Orden, Descripcion, Critico) VALUES (@PracticaId, @Orden, @Descripcion, @Critico);",
        new
        {
          PracticaId = practiceMap[stepInfo.PracticaId],
          stepInfo.Orden,
          stepInfo.Descripcion,
          stepInfo.Critico
        },
        tx,
        cancellationToken: ct));
    }

    return targetVersionId;
  }

  private sealed class CourseOwnerRow
  {
    public int CursoId { get; set; }
    public string RfcPropietario { get; set; } = string.Empty;
    public string Clave { get; set; } = string.Empty;
    public string Categoria { get; set; } = string.Empty;
    public string Nombre { get; set; } = string.Empty;
    public string Descripcion { get; set; } = string.Empty;
    public int DuracionMinutos { get; set; }
    public bool Activo { get; set; }
  }

  private sealed class PublishableCourseRow
  {
    public int CursoId { get; set; }
    public int CursoVersionId { get; set; }
    public int NumeroVersion { get; set; }
    public int LeccionCount { get; set; }
    public int BloqueCount { get; set; }
    public int EmptyLessonCount { get; set; }
  }

  private sealed class StructureLessonRow
  {
    public int LeccionId { get; set; }
    public int CursoVersionId { get; set; }
  }

  private sealed class StructureBlockRow
  {
    public int BloqueId { get; set; }
    public int LeccionId { get; set; }
  }

  private sealed class CloneVersionRow
  {
    public string Objetivos { get; set; } = string.Empty;
    public string? Prerequisitos { get; set; }
    public decimal CalificacionMinima { get; set; }
  }

  private sealed class CloneLessonRow
  {
    public int LeccionId { get; set; }
    public int Orden { get; set; }
    public string Clave { get; set; } = string.Empty;
    public string Titulo { get; set; } = string.Empty;
    public string Objetivo { get; set; } = string.Empty;
    public int DuracionMinutos { get; set; }
    public bool Requerida { get; set; }
  }

  private sealed class CloneBlockRow
  {
    public int BloqueId { get; set; }
    public int LeccionId { get; set; }
    public int Orden { get; set; }
    public string Tipo { get; set; } = string.Empty;
    public string Titulo { get; set; } = string.Empty;
    public string Contenido { get; set; } = string.Empty;
    public string? ConfiguracionJson { get; set; }
    public bool Requerido { get; set; }
  }

  private sealed class CloneResourceRow
  {
    public int RecursoId { get; set; }
    public int BloqueId { get; set; }
    public int Orden { get; set; }
    public string Tipo { get; set; } = string.Empty;
    public string Titulo { get; set; } = string.Empty;
    public string Ruta { get; set; } = string.Empty;
    public string? TextoAlternativo { get; set; }
    public string? HashContenido { get; set; }
    public DateTime? CapturadoEn { get; set; }
    public string? VersionAplicacion { get; set; }
  }

  private sealed class CloneEvaluationRow
  {
    public int EvaluacionId { get; set; }
    public string Titulo { get; set; } = string.Empty;
    public string Instrucciones { get; set; } = string.Empty;
    public decimal CalificacionMinima { get; set; }
    public bool Requerida { get; set; }
  }

  private sealed class CloneQuestionRow
  {
    public int PreguntaId { get; set; }
    public int EvaluacionId { get; set; }
    public int Orden { get; set; }
    public string Texto { get; set; } = string.Empty;
    public string? Explicacion { get; set; }
    public bool Critica { get; set; }
  }

  private sealed class CloneOptionRow
  {
    public int OpcionId { get; set; }
    public int PreguntaId { get; set; }
    public int Orden { get; set; }
    public string Texto { get; set; } = string.Empty;
    public bool EsCorrecta { get; set; }
  }

  private sealed class ClonePracticeRow
  {
    public int PracticaId { get; set; }
    public string Titulo { get; set; } = string.Empty;
    public string Instrucciones { get; set; } = string.Empty;
    public string? RutaSandbox { get; set; }
    public bool Requerida { get; set; }
  }

  private sealed class ClonePracticeStepRow
  {
    public int PracticaPasoId { get; set; }
    public int PracticaId { get; set; }
    public int Orden { get; set; }
    public string Descripcion { get; set; } = string.Empty;
    public bool Critico { get; set; }
  }
}
