using System.Data;
using System.Data.Common;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using OrionERP.Application.Common;
using OrionERP.Application.Features.OrdenesTrabajo;

namespace OrionERP.Infrastructure.Features.OrdenesTrabajo;

public sealed class OrdenTrabajoService : IOrdenTrabajoService
{
  private const string MissingSchemaMessage = "Falta aplicar el esquema de Ordenes de Trabajo. Ejecuta el script src/OrionERP.Infrastructure/Features/OrdenesTrabajo/Sql/20260425_ordenes_trabajo_v1.sql antes de usar este modulo.";

  private static readonly HashSet<string> EditableStatuses = new(StringComparer.OrdinalIgnoreCase)
  {
    OrdenTrabajoCodes.EstadoBorrador,
    OrdenTrabajoCodes.EstadoAsignada,
    OrdenTrabajoCodes.EstadoEnProceso,
    OrdenTrabajoCodes.EstadoRechazada
  };

  private static readonly HashSet<string> ExecutionStatuses = new(StringComparer.OrdinalIgnoreCase)
  {
    OrdenTrabajoCodes.EstadoBorrador,
    OrdenTrabajoCodes.EstadoAsignada,
    OrdenTrabajoCodes.EstadoEnProceso,
    OrdenTrabajoCodes.EstadoRechazada
  };

  private readonly IDbConnectionFactory _connectionFactory;

  public OrdenTrabajoService(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  }

  public async Task<IReadOnlyList<OrdenTrabajoCategoriaDto>> GetCategoriesAsync(CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT Id, Codigo, Nombre, Activa, Orden
      FROM dbo.OrdenTrabajoCategoria
      ORDER BY Orden, Nombre;
      """;

    try
    {
      using var conn = CreateConnection();
      var rows = await conn.QueryAsync<OrdenTrabajoCategoriaDto>(new CommandDefinition(sql, cancellationToken: ct));
      return rows.AsList();
    }
    catch (Exception ex) when (IsMissingWorkOrderSchemaException(ex))
    {
      throw CreateMissingSchemaException(ex);
    }
  }

  public async Task<IReadOnlyList<OrdenTrabajoLookupDto>> GetActiveEmployeeOptionsAsync(string? rfc = null, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          ch.ID AS Id,
          COALESCE(NULLIF(LTRIM(RTRIM(ch.NombreCorto)), ''), CONCAT(ch.Nombre, ' ', ch.ApellidoPaterno)) AS [Name],
          ch.Puesto AS Code
      FROM dbo.Capital_Humano ch
      WHERE UPPER(LTRIM(RTRIM(ISNULL(ch.[Status], '')))) = 'ACTIVO'
        AND (@Rfc IS NULL OR ch.RFC = @Rfc)
      ORDER BY [Name], ch.ID;
      """;

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<OrdenTrabajoLookupDto>(
      new CommandDefinition(sql, new { Rfc = NullIfWhiteSpace(rfc) }, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<IReadOnlyList<OrdenTrabajoLookupDto>> GetRoomOptionsAsync(CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          r.ID AS Id,
          r.ROOM_NAME AS [Name],
          r.ROOM_TYPE AS Code
      FROM dbo.ROOM r
      ORDER BY r.ROOM_TYPE, r.ROOM_NAME;
      """;

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<OrdenTrabajoLookupDto>(new CommandDefinition(sql, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<OrdenTrabajoDashboardDto> GetDashboardAsync(OrdenTrabajoDashboardFilter filter, CancellationToken ct = default)
  {
    filter ??= new OrdenTrabajoDashboardFilter();
    var where = new StringBuilder("WHERE ot.Estado <> 'CANCELADA'");
    var p = new DynamicParameters();
    AppendDashboardScope(where, p, filter);

    var sql =
      $$"""
      SELECT COUNT(*)
      FROM dbo.OrdenTrabajo ot
      JOIN dbo.OrdenTrabajoCategoria c ON c.Id = ot.CategoriaId
      {{where}}
        AND ot.Estado IN ('BORRADOR','ASIGNADA','EN_PROCESO','EN_REVISION','RECHAZADA');

      SELECT COUNT(*)
      FROM dbo.OrdenTrabajo ot
      JOIN dbo.OrdenTrabajoCategoria c ON c.Id = ot.CategoriaId
      {{where}}
        AND ot.Estado IN ('BORRADOR','ASIGNADA','EN_PROCESO','RECHAZADA')
        AND ISNULL(ot.FechaVencimiento, ot.FechaProgramada) < CONVERT(date, GETDATE());

      SELECT COUNT(*)
      FROM dbo.OrdenTrabajo ot
      JOIN dbo.OrdenTrabajoCategoria c ON c.Id = ot.CategoriaId
      {{where}}
        AND ot.Estado = 'EN_REVISION';

      SELECT COUNT(*)
      FROM dbo.OrdenTrabajo ot
      JOIN dbo.OrdenTrabajoCategoria c ON c.Id = ot.CategoriaId
      {{where}}
        AND c.Codigo = 'LIMPIEZA'
        AND ot.FechaProgramada = CONVERT(date, GETDATE());

      SELECT ot.Estado, COUNT(*) AS [Count]
      FROM dbo.OrdenTrabajo ot
      JOIN dbo.OrdenTrabajoCategoria c ON c.Id = ot.CategoriaId
      {{where}}
      GROUP BY ot.Estado
      ORDER BY COUNT(*) DESC, ot.Estado;

      SELECT TOP (20)
          ot.OwnerEmployeeId AS EmployeeId,
          COALESCE(NULLIF(LTRIM(RTRIM(ch.NombreCorto)), ''), CONCAT(ch.Nombre, ' ', ch.ApellidoPaterno)) AS EmployeeName,
          SUM(CASE WHEN ot.Estado IN ('BORRADOR','ASIGNADA','EN_PROCESO','EN_REVISION','RECHAZADA') THEN 1 ELSE 0 END) AS OpenCount,
          SUM(CASE WHEN ot.Estado = 'EN_REVISION' THEN 1 ELSE 0 END) AS PendingReviewCount,
          SUM(CASE WHEN ot.Estado IN ('BORRADOR','ASIGNADA','EN_PROCESO','RECHAZADA') AND ISNULL(ot.FechaVencimiento, ot.FechaProgramada) < CONVERT(date, GETDATE()) THEN 1 ELSE 0 END) AS OverdueCount
      FROM dbo.OrdenTrabajo ot
      JOIN dbo.OrdenTrabajoCategoria c ON c.Id = ot.CategoriaId
      JOIN dbo.Capital_Humano ch ON ch.ID = ot.OwnerEmployeeId
      {{where}}
      GROUP BY ot.OwnerEmployeeId, ch.NombreCorto, ch.Nombre, ch.ApellidoPaterno
      ORDER BY OpenCount DESC, EmployeeName;

      SELECT TOP (25)
          ot.Id,
          ot.Folio,
          ot.Estado,
          ot.Prioridad,
          c.Codigo AS CategoriaCodigo,
          c.Nombre AS CategoriaNombre,
          ot.Titulo,
          ot.Ubicacion,
          ot.OwnerEmployeeId,
          COALESCE(NULLIF(LTRIM(RTRIM(ch.NombreCorto)), ''), CONCAT(ch.Nombre, ' ', ch.ApellidoPaterno)) AS OwnerName,
          ot.FechaProgramada,
          ot.HoraInicioProgramada,
          ot.HoraFinProgramada,
          ot.FechaVencimiento,
          ot.RoomId,
          room.ROOM_NAME AS RoomName,
          ot.RoomCalendarId,
          ot.ReservationId,
          (SELECT COUNT(*) FROM dbo.OrdenTrabajoPaso p2 WHERE p2.OrdenTrabajoId = ot.Id) AS StepCount,
          (SELECT COUNT(*) FROM dbo.OrdenTrabajoPaso p2 WHERE p2.OrdenTrabajoId = ot.Id AND p2.Estado IN ('HECHO','INCIDENCIA','NO_APLICA')) AS CompletedStepCount,
          (SELECT COUNT(*) FROM dbo.OrdenTrabajoPaso p2 WHERE p2.OrdenTrabajoId = ot.Id AND p2.Estado = 'INCIDENCIA') AS IssueStepCount,
          ot.EstimatedCost,
          CAST(ISNULL(tx.ActualCost, 0) AS decimal(18,2)) AS ActualCost,
          CAST(CASE WHEN ot.Estado IN ('BORRADOR','ASIGNADA','EN_PROCESO','RECHAZADA') AND ISNULL(ot.FechaVencimiento, ot.FechaProgramada) < CONVERT(date, GETDATE()) THEN 1 ELSE 0 END AS bit) AS IsOverdue
      FROM dbo.OrdenTrabajo ot
      JOIN dbo.OrdenTrabajoCategoria c ON c.Id = ot.CategoriaId
      JOIN dbo.Capital_Humano ch ON ch.ID = ot.OwnerEmployeeId
      LEFT JOIN dbo.ROOM room ON room.ID = ot.RoomId
      OUTER APPLY (
          SELECT SUM(CAST(t.Monto AS decimal(18,2))) AS ActualCost
          FROM dbo.OrdenTrabajoTransaccion link
          JOIN dbo.Transacciones t ON t.ID = link.TransaccionId
          WHERE link.OrdenTrabajoId = ot.Id
      ) tx
      {{where}}
        AND c.Codigo = 'LIMPIEZA'
        AND ot.FechaProgramada = CONVERT(date, GETDATE())
      ORDER BY ot.HoraInicioProgramada, ot.Folio;
      """;

    try
    {
      using var conn = CreateConnection();
      using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, p, cancellationToken: ct));
      return new OrdenTrabajoDashboardDto
      {
        OpenCount = await multi.ReadFirstAsync<int>(),
        OverdueCount = await multi.ReadFirstAsync<int>(),
        PendingReviewCount = await multi.ReadFirstAsync<int>(),
        TodayCleaningCount = await multi.ReadFirstAsync<int>(),
        StatusCounts = (await multi.ReadAsync<OrdenTrabajoStatusCountDto>()).AsList(),
        AssigneeLoads = (await multi.ReadAsync<OrdenTrabajoAssigneeLoadDto>()).AsList(),
        TodayCleaningOrders = (await multi.ReadAsync<OrdenTrabajoListItemDto>()).AsList()
      };
    }
    catch (Exception ex) when (IsMissingWorkOrderSchemaException(ex))
    {
      throw CreateMissingSchemaException(ex);
    }
  }

  public async Task<IReadOnlyList<OrdenTrabajoListItemDto>> SearchWorkOrdersAsync(OrdenTrabajoSearchFilter filter, CancellationToken ct = default)
  {
    filter ??= new OrdenTrabajoSearchFilter();
    var p = new DynamicParameters();
    var sql = new StringBuilder(
      """
      SELECT
          ot.Id,
          ot.Folio,
          ot.Estado,
          ot.Prioridad,
          c.Codigo AS CategoriaCodigo,
          c.Nombre AS CategoriaNombre,
          ot.Titulo,
          ot.Ubicacion,
          ot.OwnerEmployeeId,
          COALESCE(NULLIF(LTRIM(RTRIM(ch.NombreCorto)), ''), CONCAT(ch.Nombre, ' ', ch.ApellidoPaterno)) AS OwnerName,
          ot.FechaProgramada,
          ot.HoraInicioProgramada,
          ot.HoraFinProgramada,
          ot.FechaVencimiento,
          ot.RoomId,
          room.ROOM_NAME AS RoomName,
          ot.RoomCalendarId,
          ot.ReservationId,
          (SELECT COUNT(*) FROM dbo.OrdenTrabajoPaso p2 WHERE p2.OrdenTrabajoId = ot.Id) AS StepCount,
          (SELECT COUNT(*) FROM dbo.OrdenTrabajoPaso p2 WHERE p2.OrdenTrabajoId = ot.Id AND p2.Estado IN ('HECHO','INCIDENCIA','NO_APLICA')) AS CompletedStepCount,
          (SELECT COUNT(*) FROM dbo.OrdenTrabajoPaso p2 WHERE p2.OrdenTrabajoId = ot.Id AND p2.Estado = 'INCIDENCIA') AS IssueStepCount,
          ot.EstimatedCost,
          CAST(ISNULL(tx.ActualCost, 0) AS decimal(18,2)) AS ActualCost,
          CAST(CASE WHEN ot.Estado IN ('BORRADOR','ASIGNADA','EN_PROCESO','RECHAZADA') AND ISNULL(ot.FechaVencimiento, ot.FechaProgramada) < CONVERT(date, GETDATE()) THEN 1 ELSE 0 END AS bit) AS IsOverdue
      FROM dbo.OrdenTrabajo ot
      JOIN dbo.OrdenTrabajoCategoria c ON c.Id = ot.CategoriaId
      JOIN dbo.Capital_Humano ch ON ch.ID = ot.OwnerEmployeeId
      LEFT JOIN dbo.ROOM room ON room.ID = ot.RoomId
      OUTER APPLY (
          SELECT SUM(CAST(t.Monto AS decimal(18,2))) AS ActualCost
          FROM dbo.OrdenTrabajoTransaccion link
          JOIN dbo.Transacciones t ON t.ID = link.TransaccionId
          WHERE link.OrdenTrabajoId = ot.Id
      ) tx
      WHERE 1 = 1
      """);

    AppendWorkOrderFilters(sql, p, filter);
    sql.Append(
      """
      ORDER BY ot.FechaProgramada DESC, ot.Id DESC
      OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
      """);
    p.Add("@Skip", Math.Max(filter.Skip, 0), DbType.Int32);
    p.Add("@Take", Math.Clamp(filter.Take, 1, 500), DbType.Int32);

    try
    {
      using var conn = CreateConnection();
      var rows = await conn.QueryAsync<OrdenTrabajoListItemDto>(new CommandDefinition(sql.ToString(), p, cancellationToken: ct));
      return rows.AsList();
    }
    catch (Exception ex) when (IsMissingWorkOrderSchemaException(ex))
    {
      throw CreateMissingSchemaException(ex);
    }
  }

  public async Task<OrdenTrabajoDetailDto?> GetWorkOrderDetailAsync(int id, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          ot.Id,
          ot.Folio,
          ot.Rfc,
          ot.Estado,
          ot.Prioridad,
          c.Codigo AS CategoriaCodigo,
          c.Nombre AS CategoriaNombre,
          ot.Titulo,
          ot.Descripcion,
          ot.Ubicacion,
          ot.OwnerEmployeeId,
          COALESCE(NULLIF(LTRIM(RTRIM(ch.NombreCorto)), ''), CONCAT(ch.Nombre, ' ', ch.ApellidoPaterno)) AS OwnerName,
          ot.FechaProgramada,
          ot.HoraInicioProgramada,
          ot.HoraFinProgramada,
          ot.FechaVencimiento,
          ot.InicioReal,
          ot.FinReal,
          ot.RoomId,
          room.ROOM_NAME AS RoomName,
          ot.RoomCalendarId,
          ot.ReservationId,
          ot.PlantillaId,
          ot.PlantillaVersionId,
          tpl.Nombre AS PlantillaNombre,
          ver.NumeroVersion AS PlantillaVersionNumero,
          (SELECT COUNT(*) FROM dbo.OrdenTrabajoPaso p2 WHERE p2.OrdenTrabajoId = ot.Id) AS StepCount,
          (SELECT COUNT(*) FROM dbo.OrdenTrabajoPaso p2 WHERE p2.OrdenTrabajoId = ot.Id AND p2.Estado IN ('HECHO','INCIDENCIA','NO_APLICA')) AS CompletedStepCount,
          (SELECT COUNT(*) FROM dbo.OrdenTrabajoPaso p2 WHERE p2.OrdenTrabajoId = ot.Id AND p2.Estado = 'INCIDENCIA') AS IssueStepCount,
          ot.EstimatedCost,
          CAST(ISNULL(tx.ActualCost, 0) AS decimal(18,2)) AS ActualCost,
          CAST(CASE WHEN ot.Estado IN ('BORRADOR','ASIGNADA','EN_PROCESO','RECHAZADA') AND ISNULL(ot.FechaVencimiento, ot.FechaProgramada) < CONVERT(date, GETDATE()) THEN 1 ELSE 0 END AS bit) AS IsOverdue,
          ot.CanceladaPor,
          ot.CanceladaEn,
          ot.MotivoCancelacion,
          ot.RechazadaPor,
          ot.RechazadaEn,
          ot.MotivoRechazo,
          CAST(CASE WHEN EXISTS (
              SELECT 1
              FROM dbo.OrdenTrabajoAuditoria audit
              WHERE audit.OrdenTrabajoId = ot.Id
                AND audit.Evento = 'ENVIADA_REVISION'
          ) THEN 1 ELSE 0 END AS bit) AS HasBeenSubmittedForReview,
          ot.CreadaEn,
          ot.CreadaPor
      FROM dbo.OrdenTrabajo ot
      JOIN dbo.OrdenTrabajoCategoria c ON c.Id = ot.CategoriaId
      JOIN dbo.Capital_Humano ch ON ch.ID = ot.OwnerEmployeeId
      LEFT JOIN dbo.ROOM room ON room.ID = ot.RoomId
      LEFT JOIN dbo.OrdenTrabajoPlantilla tpl ON tpl.Id = ot.PlantillaId
      LEFT JOIN dbo.OrdenTrabajoPlantillaVersion ver ON ver.Id = ot.PlantillaVersionId
      OUTER APPLY (
          SELECT SUM(CAST(t.Monto AS decimal(18,2))) AS ActualCost
          FROM dbo.OrdenTrabajoTransaccion link
          JOIN dbo.Transacciones t ON t.ID = link.TransaccionId
          WHERE link.OrdenTrabajoId = ot.Id
      ) tx
      WHERE ot.Id = @Id;

      SELECT
          p.EmployeeId,
          COALESCE(NULLIF(LTRIM(RTRIM(ch.NombreCorto)), ''), CONCAT(ch.Nombre, ' ', ch.ApellidoPaterno)) AS EmployeeName
      FROM dbo.OrdenTrabajoParticipante p
      JOIN dbo.Capital_Humano ch ON ch.ID = p.EmployeeId
      WHERE p.OrdenTrabajoId = @Id
      ORDER BY EmployeeName;

      SELECT
          p.Id,
          p.OrdenTrabajoId,
          p.Secuencia,
          p.Titulo,
          p.Descripcion,
          p.Estado,
          p.PoliticaFoto,
          p.RequiereNotasEnIncidencia,
          p.RequiereNotasEnNoAplica,
          p.ProcedimientoId,
          p.Notas,
          p.CompletadoEn,
          p.CompletadoPor,
          (SELECT COUNT(*) FROM dbo.OrdenTrabajoEvidencia ev WHERE ev.PasoId = p.Id AND ev.Eliminada = 0) AS ActiveEvidenceCount
      FROM dbo.OrdenTrabajoPaso p
      WHERE p.OrdenTrabajoId = @Id
      ORDER BY p.Secuencia, p.Id;

      SELECT
          ev.Id,
          ev.PasoId,
          ev.FileName,
          ev.ContentType,
          ev.CaptureSource,
          ev.ThumbnailBytes,
          ev.ThumbnailContentType,
          ev.SizeBytes,
          ev.CapturadaEn,
          ev.CapturadaPor,
          ev.Eliminada
      FROM dbo.OrdenTrabajoEvidencia ev
      JOIN dbo.OrdenTrabajoPaso p ON p.Id = ev.PasoId
      WHERE p.OrdenTrabajoId = @Id
        AND ev.Eliminada = 0
      ORDER BY ev.CapturadaEn DESC, ev.Id DESC;

      SELECT
          t.ID AS TransaccionId,
          t.Fecha,
          t.Concepto,
          CAST(t.Monto AS decimal(18,2)) AS Monto,
          t.Estatus
      FROM dbo.OrdenTrabajoTransaccion link
      JOIN dbo.Transacciones t ON t.ID = link.TransaccionId
      WHERE link.OrdenTrabajoId = @Id
      ORDER BY t.Fecha DESC, t.ID DESC;

      SELECT TOP (50)
          Id,
          Evento,
          Detalle,
          CreadoPor,
          CreadoEn
      FROM dbo.OrdenTrabajoAuditoria
      WHERE OrdenTrabajoId = @Id
      ORDER BY CreadoEn DESC, Id DESC;
      """;

    try
    {
      using var conn = CreateConnection();
      using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
      var detail = await multi.ReadFirstOrDefaultAsync<OrdenTrabajoDetailDto>();
      if (detail is null)
      {
        return null;
      }

      detail.Helpers = (await multi.ReadAsync<OrdenTrabajoParticipantDto>()).AsList();
      var steps = (await multi.ReadAsync<OrdenTrabajoStepDto>()).AsList();
      var evidence = (await multi.ReadAsync<OrdenTrabajoEvidenceDto>()).AsList();
      var evidenceByStep = evidence
        .GroupBy(item => item.PasoId)
        .ToDictionary(group => group.Key, group => (IReadOnlyList<OrdenTrabajoEvidenceDto>)group.ToList());
      foreach (var step in steps)
      {
        step.Evidence = evidenceByStep.TryGetValue(step.Id, out var stepEvidence)
          ? stepEvidence
          : Array.Empty<OrdenTrabajoEvidenceDto>();
      }

      detail.Steps = steps;
      detail.Transactions = (await multi.ReadAsync<OrdenTrabajoTransactionDto>()).AsList();
      detail.Audit = (await multi.ReadAsync<OrdenTrabajoAuditDto>()).AsList();
      return detail;
    }
    catch (Exception ex) when (IsMissingWorkOrderSchemaException(ex))
    {
      throw CreateMissingSchemaException(ex);
    }
  }

  public async Task<OrdenTrabajoCommandResult> CreateManualAsync(OrdenTrabajoCreateRequest request, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    var actor = NormalizeActor(request.CreatedBy);
    var normalizedCategory = NormalizeCode(request.CategoriaCodigo, OrdenTrabajoCodes.CategoriaMantenimiento);

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);

    try
    {
      var categoryId = await ResolveCategoryIdAsync(conn, tx, normalizedCategory, ct);
      if (categoryId is null)
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("La categoria seleccionada no existe.");
      }

      if (!await EmployeeExistsAsync(conn, tx, request.OwnerEmployeeId, ct))
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("El responsable seleccionado no existe o no esta activo.");
      }

      var template = request.TemplateId.HasValue
        ? await GetPublishedTemplateAsync(conn, tx, request.TemplateId.Value, request.Rfc, normalizedCategory, ct)
        : null;
      if (request.TemplateId.HasValue && template is null)
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("La plantilla seleccionada no tiene version publicada para la categoria y RFC.");
      }

      var folio = await GenerateFolioAsync(conn, tx, request.FechaProgramada.Year, ct);
      var workOrderId = await InsertWorkOrderAsync(
        conn,
        tx,
        new WorkOrderInsertArgs
        {
          Folio = folio,
          Rfc = RequireText(request.Rfc, "El RFC es obligatorio."),
          CategoryId = categoryId.Value,
          Estado = OrdenTrabajoCodes.EstadoAsignada,
          Prioridad = NormalizePriority(request.Prioridad),
          Titulo = RequireText(request.Titulo, "El titulo es obligatorio."),
          Descripcion = NullIfWhiteSpace(request.Descripcion),
          OwnerEmployeeId = request.OwnerEmployeeId,
          FechaProgramada = request.FechaProgramada.Date,
          HoraInicioProgramada = request.HoraInicioProgramada,
          HoraFinProgramada = request.HoraFinProgramada,
          FechaVencimiento = request.FechaVencimiento?.Date,
          RoomId = request.RoomId,
          RoomCalendarId = request.RoomCalendarId,
          ReservationId = request.ReservationId,
          Ubicacion = NullIfWhiteSpace(request.Ubicacion),
          PlantillaId = template?.TemplateId,
          PlantillaVersionId = template?.VersionId,
          EstimatedCost = request.EstimatedCost,
          Actor = actor
        },
        ct);

      await ReplaceHelpersAsync(conn, tx, workOrderId, request.HelperEmployeeIds, actor, ct);
      await CreateStepsFromTemplateOrDefaultAsync(conn, tx, workOrderId, template?.VersionId, request.Titulo, request.Descripcion, ct);
      await AddAuditAsync(conn, tx, workOrderId, "CREADA", $"Orden creada manualmente con folio {folio}.", actor, ct);

      await tx.CommitAsync(ct);
      return OrdenTrabajoCommandResult.Ok($"Orden {folio} creada correctamente.", workOrderId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<OrdenTrabajoCalendarCreateResult> CreateCleaningFromCalendarAsync(OrdenTrabajoCalendarCreateRequest request, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    var actor = NormalizeActor(request.CreatedBy);
    var requestedIds = request.RoomCalendarIds.Distinct().Where(id => id > 0).ToList();
    if (requestedIds.Count == 0)
    {
      return new OrdenTrabajoCalendarCreateResult
      {
        Success = false,
        Message = "Selecciona al menos una celda del calendario.",
        Cells = Array.Empty<OrdenTrabajoCalendarCellResult>()
      };
    }

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);

    var results = new List<OrdenTrabajoCalendarCellResult>();

    try
    {
      var categoryId = await ResolveCategoryIdAsync(conn, tx, OrdenTrabajoCodes.CategoriaLimpieza, ct);
      if (categoryId is null)
      {
        await tx.RollbackAsync(ct);
        return new OrdenTrabajoCalendarCreateResult { Success = false, Message = "No existe la categoria Limpieza." };
      }

      if (!await EmployeeExistsAsync(conn, tx, request.OwnerEmployeeId, ct))
      {
        await tx.RollbackAsync(ct);
        return new OrdenTrabajoCalendarCreateResult { Success = false, Message = "El responsable seleccionado no existe o no esta activo." };
      }

      foreach (var roomCalendarId in requestedIds)
      {
        var context = await conn.QueryFirstOrDefaultAsync<CalendarCellContext>(
          new CommandDefinition(
            """
            SELECT
                rc.id AS RoomCalendarId,
                rc.ROOM_DATE AS RoomDate,
                rc.ROOM AS RoomName,
                room.ID AS RoomId,
                TRY_CONVERT(int, NULLIF(LTRIM(RTRIM(CAST(rc.LOCK_DESCRIPTION AS varchar(50)))), '')) AS ReservationId
            FROM dbo.ROOM_CALENDAR rc
            LEFT JOIN dbo.ROOM room ON room.ROOM_NAME = rc.ROOM
            WHERE rc.id = @RoomCalendarId;
            """,
            new { RoomCalendarId = roomCalendarId },
            tx,
            cancellationToken: ct));

        if (context is null || context.RoomId is null)
        {
          results.Add(CellResult(roomCalendarId, false, "La celda no existe o no tiene room configurado."));
          continue;
        }

        var cleaningDate = context.RoomDate.Date.AddDays(1);
        var duplicate = await conn.QueryFirstOrDefaultAsync<(int Id, string Folio)>(
          new CommandDefinition(
            """
            SELECT TOP (1) ot.Id, ot.Folio
            FROM dbo.OrdenTrabajo ot
            WHERE ot.RoomId = @RoomId
              AND ot.FechaProgramada = @CleaningDate
              AND ot.CategoriaId = @CategoryId
              AND ot.Estado IN ('BORRADOR','ASIGNADA','EN_PROCESO','EN_REVISION','RECHAZADA')
            ORDER BY ot.Id DESC;
            """,
            new { RoomId = context.RoomId.Value, CleaningDate = cleaningDate, CategoryId = categoryId.Value },
            tx,
            cancellationToken: ct));

        if (duplicate.Id > 0)
        {
          results.Add(CellResult(roomCalendarId, false, $"Ya existe la orden abierta {duplicate.Folio}.", duplicate.Id, duplicate.Folio));
          continue;
        }

        var template = await conn.QueryFirstOrDefaultAsync<PublishedTemplateRow>(
          new CommandDefinition(
            """
            SELECT
                tpl.Id AS TemplateId,
                ver.Id AS VersionId,
                tpl.Nombre AS TemplateName
            FROM dbo.OrdenTrabajoPlantillaRoom map
            JOIN dbo.OrdenTrabajoPlantilla tpl ON tpl.Id = map.PlantillaId
            JOIN dbo.OrdenTrabajoCategoria cat ON cat.Id = tpl.CategoriaId
            JOIN dbo.OrdenTrabajoPlantillaVersion ver ON ver.PlantillaId = tpl.Id AND ver.Estado = 'PUBLICADA'
            WHERE map.RoomId = @RoomId
              AND tpl.Activa = 1
              AND cat.Codigo = 'LIMPIEZA'
              AND tpl.Rfc = @Rfc;
            """,
            new { RoomId = context.RoomId.Value, Rfc = RequireText(request.Rfc, "El RFC es obligatorio.") },
            tx,
            cancellationToken: ct));

        if (template is null)
        {
          results.Add(CellResult(roomCalendarId, false, $"La suite {context.RoomName} no tiene plantilla de limpieza publicada."));
          continue;
        }

        var folio = await GenerateFolioAsync(conn, tx, cleaningDate.Year, ct);
        var workOrderId = await InsertWorkOrderAsync(
          conn,
          tx,
          new WorkOrderInsertArgs
          {
            Folio = folio,
            Rfc = request.Rfc,
            CategoryId = categoryId.Value,
            Estado = OrdenTrabajoCodes.EstadoAsignada,
            Prioridad = OrdenTrabajoCodes.PrioridadNormal,
            Titulo = $"Limpieza {context.RoomName} {cleaningDate:yyyy-MM-dd}",
            Descripcion = $"Orden de limpieza creada desde calendario para {context.RoomName}.",
            OwnerEmployeeId = request.OwnerEmployeeId,
            FechaProgramada = cleaningDate,
            HoraInicioProgramada = new TimeSpan(11, 0, 0),
            HoraFinProgramada = new TimeSpan(15, 0, 0),
            FechaVencimiento = cleaningDate,
            RoomId = context.RoomId,
            RoomCalendarId = context.RoomCalendarId,
            ReservationId = context.ReservationId,
            Ubicacion = context.RoomName,
            PlantillaId = template.TemplateId,
            PlantillaVersionId = template.VersionId,
            EstimatedCost = 0m,
            Actor = actor
          },
          ct);

        await ReplaceHelpersAsync(conn, tx, workOrderId, request.HelperEmployeeIds, actor, ct);
        await CreateStepsFromTemplateOrDefaultAsync(conn, tx, workOrderId, template.VersionId, template.TemplateName, null, ct);
        await AddAuditAsync(conn, tx, workOrderId, "CREADA_CALENDARIO", $"Orden creada desde celda {roomCalendarId}.", actor, ct);
        results.Add(CellResult(roomCalendarId, true, $"Orden {folio} creada.", workOrderId, folio));
      }

      await tx.CommitAsync(ct);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }

    var created = results.Count(item => item.Success);
    return new OrdenTrabajoCalendarCreateResult
    {
      Success = created > 0,
      Message = created > 0
        ? $"Se crearon {created} ordenes de limpieza."
        : "No se pudo crear ninguna orden de limpieza.",
      EntityId = results.FirstOrDefault(item => item.Success)?.WorkOrderId,
      Cells = results
    };
  }

  public async Task<OrdenTrabajoCommandResult> UpdateWorkOrderAsync(int id, OrdenTrabajoUpdateRequest request, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    var actor = NormalizeActor(request.UpdatedBy);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      var status = await GetWorkOrderStatusAsync(conn, tx, id, ct);
      if (status is null)
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("La orden no existe.");
      }

      if (!EditableStatuses.Contains(status))
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("La orden solo se puede editar antes de enviarla a revision.");
      }

      if (!await EmployeeExistsAsync(conn, tx, request.OwnerEmployeeId, ct))
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("El responsable seleccionado no existe o no esta activo.");
      }

      await conn.ExecuteAsync(
        new CommandDefinition(
          """
          UPDATE dbo.OrdenTrabajo
          SET Titulo = @Titulo,
              Descripcion = @Descripcion,
              OwnerEmployeeId = @OwnerEmployeeId,
              FechaProgramada = @FechaProgramada,
              HoraInicioProgramada = @HoraInicioProgramada,
              HoraFinProgramada = @HoraFinProgramada,
              FechaVencimiento = @FechaVencimiento,
              Prioridad = @Prioridad,
              Ubicacion = @Ubicacion,
              EstimatedCost = @EstimatedCost,
              ActualizadaEn = SYSUTCDATETIME(),
              ActualizadaPor = @Actor
          WHERE Id = @Id;
          """,
          new
          {
            Id = id,
            Titulo = RequireText(request.Titulo, "El titulo es obligatorio."),
            Descripcion = NullIfWhiteSpace(request.Descripcion),
            request.OwnerEmployeeId,
            FechaProgramada = request.FechaProgramada.Date,
            request.HoraInicioProgramada,
            request.HoraFinProgramada,
            FechaVencimiento = request.FechaVencimiento?.Date,
            Prioridad = NormalizePriority(request.Prioridad),
            Ubicacion = NullIfWhiteSpace(request.Ubicacion),
            request.EstimatedCost,
            Actor = actor
          },
          tx,
          cancellationToken: ct));

      await ReplaceHelpersAsync(conn, tx, id, request.HelperEmployeeIds, actor, ct);
      await AddAuditAsync(conn, tx, id, "ACTUALIZADA", "Orden actualizada.", actor, ct);
      await tx.CommitAsync(ct);
      return OrdenTrabajoCommandResult.Ok("Orden actualizada correctamente.", id);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<OrdenTrabajoCommandResult> ReplaceWorkOrderStepsAsync(int id, OrdenTrabajoStepsSaveRequest request, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    var actor = NormalizeActor(request.SavedBy);
    var steps = request.Steps
      .Where(step => !string.IsNullOrWhiteSpace(step.Titulo))
      .Select((step, index) => new
      {
        Secuencia = step.Secuencia > 0 ? step.Secuencia : index + 1,
        Titulo = Truncate(RequireText(step.Titulo, "Cada paso necesita titulo."), 200),
        Descripcion = Truncate(RequireText(step.Descripcion, "Cada paso necesita descripcion."), 1000),
        PoliticaFoto = NormalizePhotoPolicy(step.PoliticaFoto),
        step.RequiereNotasEnIncidencia,
        step.RequiereNotasEnNoAplica,
        step.ProcedimientoId
      })
      .OrderBy(step => step.Secuencia)
      .ToList();

    if (steps.Count == 0)
    {
      return OrdenTrabajoCommandResult.Fail("Agrega al menos un paso a la ruta critica.");
    }

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      var status = await GetWorkOrderStatusAsync(conn, tx, id, ct);
      if (status is null)
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("La orden no existe.");
      }

      if (!EditableStatuses.Contains(status))
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("La ruta critica solo se puede editar antes de enviar la orden a revision.");
      }

      if (await HasBeenSubmittedForReviewAsync(conn, tx, id, ct))
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("La ruta critica ya no se puede editar despues de enviarla a revision.");
      }

      var evidenceCount = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(
          """
          SELECT COUNT(*)
          FROM dbo.OrdenTrabajoEvidencia ev
          JOIN dbo.OrdenTrabajoPaso p ON p.Id = ev.PasoId
          WHERE p.OrdenTrabajoId = @WorkOrderId
            AND ev.Eliminada = 0;
          """,
          new { WorkOrderId = id },
          tx,
          cancellationToken: ct));
      if (evidenceCount > 0)
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("No se puede reemplazar la ruta critica porque ya tiene evidencia capturada.");
      }

      await conn.ExecuteAsync(
        new CommandDefinition(
          "DELETE FROM dbo.OrdenTrabajoPaso WHERE OrdenTrabajoId = @WorkOrderId;",
          new { WorkOrderId = id },
          tx,
          cancellationToken: ct));

      foreach (var step in steps)
      {
        await conn.ExecuteAsync(
          new CommandDefinition(
            """
            INSERT INTO dbo.OrdenTrabajoPaso
            (
                OrdenTrabajoId,
                Secuencia,
                Titulo,
                Descripcion,
                Estado,
                PoliticaFoto,
                RequiereNotasEnIncidencia,
                RequiereNotasEnNoAplica,
                ProcedimientoId
            )
            VALUES
            (
                @WorkOrderId,
                @Secuencia,
                @Titulo,
                @Descripcion,
                'PENDIENTE',
                @PoliticaFoto,
                @RequiereNotasEnIncidencia,
                @RequiereNotasEnNoAplica,
                @ProcedimientoId
            );
            """,
            new
            {
              WorkOrderId = id,
              step.Secuencia,
              step.Titulo,
              step.Descripcion,
              step.PoliticaFoto,
              step.RequiereNotasEnIncidencia,
              step.RequiereNotasEnNoAplica,
              step.ProcedimientoId
            },
            tx,
            cancellationToken: ct));
      }

      await AddAuditAsync(conn, tx, id, "RUTA_CRITICA_ACTUALIZADA", $"Ruta critica actualizada con {steps.Count} paso(s).", actor, ct);
      await tx.CommitAsync(ct);
      return OrdenTrabajoCommandResult.Ok("Ruta critica actualizada correctamente.");
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<OrdenTrabajoCommandResult> CancelWorkOrderAsync(int id, string reason, string actor, CancellationToken ct = default)
  {
    var safeActor = NormalizeActor(actor);
    var safeReason = RequireText(reason, "El motivo de cancelacion es obligatorio.");
    const string sql =
      """
      UPDATE dbo.OrdenTrabajo
      SET Estado = 'CANCELADA',
          CanceladaEn = SYSUTCDATETIME(),
          CanceladaPor = @Actor,
          MotivoCancelacion = @Reason,
          ActualizadaEn = SYSUTCDATETIME(),
          ActualizadaPor = @Actor
      WHERE Id = @Id
        AND Estado <> 'CERRADA'
        AND Estado <> 'CANCELADA';
      """;

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct);
    var affected = await conn.ExecuteAsync(new CommandDefinition(sql, new { Id = id, Actor = safeActor, Reason = safeReason }, tx, cancellationToken: ct));
    if (affected == 0)
    {
      await tx.RollbackAsync(ct);
      return OrdenTrabajoCommandResult.Fail("La orden no existe o ya esta cerrada/cancelada.");
    }

    await AddAuditAsync(conn, tx, id, "CANCELADA", safeReason, safeActor, ct);
    await tx.CommitAsync(ct);
    return OrdenTrabajoCommandResult.Ok("Orden cancelada correctamente.", id);
  }

  public async Task<OrdenTrabajoCommandResult> DeleteWorkOrderAsync(int id, string actor, CancellationToken ct = default)
  {
    _ = NormalizeActor(actor);

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      var folio = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
        """
        SELECT Folio
        FROM dbo.OrdenTrabajo
        WHERE Id = @Id;
        """,
        new { Id = id },
        tx,
        cancellationToken: ct));

      if (string.IsNullOrWhiteSpace(folio))
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("La orden no existe.");
      }

      await conn.ExecuteAsync(new CommandDefinition(
        """
        DELETE FROM dbo.OrdenTrabajoTransaccion
        WHERE OrdenTrabajoId = @Id;
        """,
        new { Id = id },
        tx,
        cancellationToken: ct));

      var affected = await conn.ExecuteAsync(new CommandDefinition(
        """
        DELETE FROM dbo.OrdenTrabajo
        WHERE Id = @Id;
        """,
        new { Id = id },
        tx,
        cancellationToken: ct));

      if (affected == 0)
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("La orden no existe.");
      }

      await tx.CommitAsync(ct);
      return OrdenTrabajoCommandResult.Ok($"Orden {folio} eliminada correctamente.", id);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<OrdenTrabajoCommandResult> StartWorkOrderAsync(int id, string actor, int? actorEmployeeId = null, CancellationToken ct = default)
  {
    var safeActor = NormalizeActor(actor);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct);

    if (!await CanActorWorkAsync(conn, tx, id, actorEmployeeId, ct))
    {
      await tx.RollbackAsync(ct);
      return OrdenTrabajoCommandResult.Fail("Solo el responsable o ayudantes pueden iniciar esta orden.");
    }

    var affected = await conn.ExecuteAsync(
      new CommandDefinition(
        """
        UPDATE dbo.OrdenTrabajo
        SET Estado = 'EN_PROCESO',
            InicioReal = COALESCE(InicioReal, SYSUTCDATETIME()),
            ActualizadaEn = SYSUTCDATETIME(),
            ActualizadaPor = @Actor
        WHERE Id = @Id
          AND Estado IN ('BORRADOR','ASIGNADA','RECHAZADA');
        """,
        new { Id = id, Actor = safeActor },
        tx,
        cancellationToken: ct));

    if (affected == 0)
    {
      await tx.RollbackAsync(ct);
      return OrdenTrabajoCommandResult.Fail("La orden no esta en un estado que permita iniciar trabajo.");
    }

    await AddAuditAsync(conn, tx, id, "INICIADA", "Orden iniciada.", safeActor, ct);
    await tx.CommitAsync(ct);
    return OrdenTrabajoCommandResult.Ok("Orden iniciada.", id);
  }

  public async Task<OrdenTrabajoCommandResult> UpdateStepAsync(int workOrderId, int stepId, OrdenTrabajoStepUpdateRequest request, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    var safeActor = NormalizeActor(request.UpdatedBy);
    var stepStatus = NormalizeStepStatus(request.Estado);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      if (!await CanActorWorkAsync(conn, tx, workOrderId, request.ActorEmployeeId, ct))
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("Solo el responsable o ayudantes pueden actualizar pasos.");
      }

      var workOrderStatus = await GetWorkOrderStatusAsync(conn, tx, workOrderId, ct);
      if (workOrderStatus is null || !ExecutionStatuses.Contains(workOrderStatus))
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("La orden no permite actualizar pasos en su estado actual.");
      }

      var step = await conn.QueryFirstOrDefaultAsync<StepRuleRow>(
        new CommandDefinition(
          """
          SELECT Id, RequiereNotasEnIncidencia, RequiereNotasEnNoAplica
          FROM dbo.OrdenTrabajoPaso
          WHERE Id = @StepId
            AND OrdenTrabajoId = @WorkOrderId;
          """,
          new { StepId = stepId, WorkOrderId = workOrderId },
          tx,
          cancellationToken: ct));

      if (step is null)
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("El paso seleccionado no existe.");
      }

      var notes = NullIfWhiteSpace(request.Notas);
      if (stepStatus == OrdenTrabajoCodes.PasoIncidencia && step.RequiereNotasEnIncidencia && notes is null)
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("Los pasos con incidencia requieren notas.");
      }

      if (stepStatus == OrdenTrabajoCodes.PasoNoAplica && step.RequiereNotasEnNoAplica && notes is null)
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("Los pasos no aplicables requieren notas.");
      }

      await conn.ExecuteAsync(
        new CommandDefinition(
          """
          UPDATE dbo.OrdenTrabajo
          SET Estado = CASE WHEN Estado IN ('BORRADOR','ASIGNADA','RECHAZADA') THEN 'EN_PROCESO' ELSE Estado END,
              InicioReal = CASE WHEN InicioReal IS NULL THEN SYSUTCDATETIME() ELSE InicioReal END,
              ActualizadaEn = SYSUTCDATETIME(),
              ActualizadaPor = @Actor
          WHERE Id = @WorkOrderId;

          UPDATE dbo.OrdenTrabajoPaso
          SET Estado = @Estado,
              Notas = @Notas,
              CompletadoEn = CASE WHEN @Estado = 'PENDIENTE' THEN NULL ELSE SYSUTCDATETIME() END,
              CompletadoPor = CASE WHEN @Estado = 'PENDIENTE' THEN NULL ELSE @Actor END
          WHERE Id = @StepId
            AND OrdenTrabajoId = @WorkOrderId;
          """,
          new
          {
            WorkOrderId = workOrderId,
            StepId = stepId,
            Estado = stepStatus,
            Notas = notes,
            Actor = safeActor
          },
          tx,
          cancellationToken: ct));

      await AddAuditAsync(conn, tx, workOrderId, "PASO_ACTUALIZADO", $"Paso {stepId}: {stepStatus}.", safeActor, ct);
      await tx.CommitAsync(ct);
      return OrdenTrabajoCommandResult.Ok("Paso actualizado correctamente.", stepId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<OrdenTrabajoCommandResult> AddStepEvidenceAsync(int workOrderId, int stepId, OrdenTrabajoEvidenceCreateRequest request, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    if (request.ImageBytes.Length == 0)
    {
      return OrdenTrabajoCommandResult.Fail("Selecciona un archivo para guardar evidencia.");
    }

    var safeActor = NormalizeActor(request.CapturedBy);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      if (!await CanActorWorkAsync(conn, tx, workOrderId, request.ActorEmployeeId, ct))
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("Solo el responsable o ayudantes pueden agregar evidencia.");
      }

      var policy = await conn.ExecuteScalarAsync<string?>(
        new CommandDefinition(
          """
          SELECT p.PoliticaFoto
          FROM dbo.OrdenTrabajoPaso p
          JOIN dbo.OrdenTrabajo ot ON ot.Id = p.OrdenTrabajoId
          WHERE p.Id = @StepId
            AND p.OrdenTrabajoId = @WorkOrderId
            AND ot.Estado IN ('BORRADOR','ASIGNADA','EN_PROCESO','RECHAZADA');
          """,
          new { StepId = stepId, WorkOrderId = workOrderId },
          tx,
          cancellationToken: ct));

      if (policy is null)
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("La orden o el paso no permiten agregar evidencia en su estado actual.");
      }

      if (string.Equals(policy, OrdenTrabajoCodes.FotoNoPermitida, StringComparison.OrdinalIgnoreCase))
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("Este paso no permite fotografias.");
      }

      var evidenceId = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(
          """
          INSERT INTO dbo.OrdenTrabajoEvidencia
          (
              PasoId,
              FileName,
              ContentType,
              CaptureSource,
              ImageBytes,
              ThumbnailBytes,
              ThumbnailContentType,
              SizeBytes,
              DeviceInfo,
              CapturadaPor
          )
          VALUES
          (
              @StepId,
              @FileName,
              @ContentType,
              @CaptureSource,
              @ImageBytes,
              @ThumbnailBytes,
              @ThumbnailContentType,
              @SizeBytes,
              @DeviceInfo,
              @Actor
          );

          SELECT CAST(SCOPE_IDENTITY() AS int);
          """,
          new
          {
            StepId = stepId,
            FileName = NullIfWhiteSpace(request.FileName) ?? BuildDefaultEvidenceFileName(workOrderId, stepId, request.ContentType),
            ContentType = NormalizeEvidenceContentType(request.ContentType, request.FileName),
            CaptureSource = NormalizeCaptureSource(request.CaptureSource),
            request.ImageBytes,
            ThumbnailBytes = request.ThumbnailBytes,
            ThumbnailContentType = request.ThumbnailBytes is { Length: > 0 }
              ? NormalizeEvidenceContentType(request.ThumbnailContentType ?? request.ContentType, request.FileName)
              : null,
            SizeBytes = request.ImageBytes.LongLength,
            DeviceInfo = NullIfWhiteSpace(request.DeviceInfo),
            Actor = safeActor
          },
          tx,
          cancellationToken: ct));

      await AddAuditAsync(conn, tx, workOrderId, "EVIDENCIA_AGREGADA", $"Evidencia {evidenceId} agregada al paso {stepId}.", safeActor, ct);
      await tx.CommitAsync(ct);
      return OrdenTrabajoCommandResult.Ok("Evidencia guardada correctamente.", evidenceId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<OrdenTrabajoCommandResult> RemoveStepEvidenceAsync(int workOrderId, int stepId, int evidenceId, string actor, int? actorEmployeeId = null, CancellationToken ct = default)
  {
    var safeActor = NormalizeActor(actor);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct);

    if (!await CanActorWorkAsync(conn, tx, workOrderId, actorEmployeeId, ct))
    {
      await tx.RollbackAsync(ct);
      return OrdenTrabajoCommandResult.Fail("Solo el responsable o ayudantes pueden quitar evidencia antes de enviar.");
    }

    var affected = await conn.ExecuteAsync(
      new CommandDefinition(
        """
        UPDATE ev
        SET Eliminada = 1,
            EliminadaEn = SYSUTCDATETIME(),
            EliminadaPor = @Actor
        FROM dbo.OrdenTrabajoEvidencia ev
        JOIN dbo.OrdenTrabajoPaso p ON p.Id = ev.PasoId
        JOIN dbo.OrdenTrabajo ot ON ot.Id = p.OrdenTrabajoId
        WHERE ev.Id = @EvidenceId
          AND p.Id = @StepId
          AND ot.Id = @WorkOrderId
          AND ev.Eliminada = 0
          AND ot.Estado IN ('BORRADOR','ASIGNADA','EN_PROCESO','RECHAZADA')
          AND NOT EXISTS (
              SELECT 1
              FROM dbo.OrdenTrabajoAuditoria audit
              WHERE audit.OrdenTrabajoId = ot.Id
                AND audit.Evento = 'ENVIADA_REVISION'
          );
        """,
        new { EvidenceId = evidenceId, StepId = stepId, WorkOrderId = workOrderId, Actor = safeActor },
        tx,
        cancellationToken: ct));

    if (affected == 0)
    {
      await tx.RollbackAsync(ct);
      return OrdenTrabajoCommandResult.Fail("La evidencia no existe, ya fue enviada a revision o ya no puede eliminarse.");
    }

    await AddAuditAsync(conn, tx, workOrderId, "EVIDENCIA_ELIMINADA", $"Evidencia {evidenceId} eliminada.", safeActor, ct);
    await tx.CommitAsync(ct);
    return OrdenTrabajoCommandResult.Ok("Evidencia eliminada.", evidenceId);
  }

  public async Task<OrdenTrabajoBinaryContent?> GetEvidenceContentAsync(int evidenceId, bool thumbnail = false, CancellationToken ct = default)
  {
    var sql = thumbnail
      ? """
        SELECT
            Id,
            FileName,
            COALESCE(ThumbnailContentType, ContentType) AS ContentType,
            COALESCE(ThumbnailBytes, ImageBytes) AS Bytes
        FROM dbo.OrdenTrabajoEvidencia
        WHERE Id = @EvidenceId
          AND Eliminada = 0;
        """
      : """
        SELECT
            Id,
            FileName,
            ContentType,
            ImageBytes AS Bytes
        FROM dbo.OrdenTrabajoEvidencia
        WHERE Id = @EvidenceId
          AND Eliminada = 0;
        """;

    using var conn = CreateConnection();
    return await conn.QueryFirstOrDefaultAsync<OrdenTrabajoBinaryContent>(
      new CommandDefinition(sql, new { EvidenceId = evidenceId }, cancellationToken: ct));
  }

  public async Task<OrdenTrabajoCommandResult> SubmitForReviewAsync(int id, string actor, int? actorEmployeeId = null, CancellationToken ct = default)
  {
    var safeActor = NormalizeActor(actor);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      if (!await CanActorWorkAsync(conn, tx, id, actorEmployeeId, ct))
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("Solo el responsable o ayudantes pueden enviar esta orden a revision.");
      }

      var status = await GetWorkOrderStatusAsync(conn, tx, id, ct);
      if (status is null || !ExecutionStatuses.Contains(status))
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("La orden no puede enviarse a revision en su estado actual.");
      }

      var validationMessage = await ValidateReadyForReviewAsync(conn, tx, id, ct);
      if (validationMessage is not null)
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail(validationMessage);
      }

      await conn.ExecuteAsync(
        new CommandDefinition(
          """
          UPDATE dbo.OrdenTrabajo
          SET Estado = 'EN_REVISION',
              ActualizadaEn = SYSUTCDATETIME(),
              ActualizadaPor = @Actor
          WHERE Id = @Id;
          """,
          new { Id = id, Actor = safeActor },
          tx,
          cancellationToken: ct));

      await AddAuditAsync(conn, tx, id, "ENVIADA_REVISION", "Orden enviada a revision.", safeActor, ct);
      await tx.CommitAsync(ct);
      return OrdenTrabajoCommandResult.Ok("Orden enviada a revision.", id);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<OrdenTrabajoCommandResult> ApproveAsync(int id, string actor, CancellationToken ct = default)
  {
    var safeActor = NormalizeActor(actor);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct);

    var affected = await conn.ExecuteAsync(
      new CommandDefinition(
        """
        UPDATE dbo.OrdenTrabajo
        SET Estado = 'CERRADA',
            FinReal = COALESCE(FinReal, SYSUTCDATETIME()),
            ActualizadaEn = SYSUTCDATETIME(),
            ActualizadaPor = @Actor
        WHERE Id = @Id
          AND Estado = 'EN_REVISION';
        """,
        new { Id = id, Actor = safeActor },
        tx,
        cancellationToken: ct));

    if (affected == 0)
    {
      await tx.RollbackAsync(ct);
      return OrdenTrabajoCommandResult.Fail("La orden debe estar en revision para aprobarla.");
    }

    await AddAuditAsync(conn, tx, id, "APROBADA", "Orden aprobada y cerrada.", safeActor, ct);
    await tx.CommitAsync(ct);
    return OrdenTrabajoCommandResult.Ok("Orden aprobada y cerrada.", id);
  }

  public async Task<OrdenTrabajoCommandResult> RejectAsync(int id, string reason, string actor, CancellationToken ct = default)
  {
    var safeActor = NormalizeActor(actor);
    var safeReason = RequireText(reason, "El motivo de rechazo es obligatorio.");
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct);

    var affected = await conn.ExecuteAsync(
      new CommandDefinition(
        """
        UPDATE dbo.OrdenTrabajo
        SET Estado = 'EN_PROCESO',
            RechazadaEn = SYSUTCDATETIME(),
            RechazadaPor = @Actor,
            MotivoRechazo = @Reason,
            ActualizadaEn = SYSUTCDATETIME(),
            ActualizadaPor = @Actor
        WHERE Id = @Id
          AND Estado = 'EN_REVISION';
        """,
        new { Id = id, Reason = safeReason, Actor = safeActor },
        tx,
        cancellationToken: ct));

    if (affected == 0)
    {
      await tx.RollbackAsync(ct);
      return OrdenTrabajoCommandResult.Fail("La orden debe estar en revision para rechazarla.");
    }

    await AddAuditAsync(conn, tx, id, "RECHAZADA", safeReason, safeActor, ct);
    await tx.CommitAsync(ct);
    return OrdenTrabajoCommandResult.Ok("Orden rechazada y devuelta a trabajo.", id);
  }

  public async Task<IReadOnlyList<OrdenTrabajoTransactionSearchItemDto>> SearchTransactionsAsync(int workOrderId, string? search, CancellationToken ct = default)
  {
    const string sql =
      """
      DECLARE @Rfc varchar(50) = (SELECT Rfc FROM dbo.OrdenTrabajo WHERE Id = @WorkOrderId);

      SELECT TOP (25)
          t.ID AS Id,
          t.Fecha,
          t.Concepto,
          CAST(t.Monto AS decimal(18,2)) AS Monto,
          t.Estatus,
          t.RFC AS Rfc
      FROM dbo.Transacciones t
      WHERE t.RFC = @Rfc
        AND (
          @Search IS NULL
          OR t.Concepto LIKE @SearchLike
          OR t.Referencia LIKE @SearchLike
          OR CONVERT(varchar(20), t.ID) = @Search
        )
      ORDER BY t.Fecha DESC, t.ID DESC;
      """;

    var normalizedSearch = NullIfWhiteSpace(search);
    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<OrdenTrabajoTransactionSearchItemDto>(
      new CommandDefinition(
        sql,
        new { WorkOrderId = workOrderId, Search = normalizedSearch, SearchLike = normalizedSearch is null ? null : $"%{normalizedSearch}%" },
        cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<OrdenTrabajoCommandResult> LinkTransactionAsync(int workOrderId, int transaccionId, string actor, CancellationToken ct = default)
  {
    var safeActor = NormalizeActor(actor);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct);

    var sameRfc = await conn.ExecuteScalarAsync<bool>(
      new CommandDefinition(
        """
        SELECT CAST(CASE WHEN EXISTS (
          SELECT 1
          FROM dbo.OrdenTrabajo ot
          JOIN dbo.Transacciones t ON t.ID = @TransaccionId
          WHERE ot.Id = @WorkOrderId
            AND ot.Rfc = t.RFC
        ) THEN 1 ELSE 0 END AS bit);
        """,
        new { WorkOrderId = workOrderId, TransaccionId = transaccionId },
        tx,
        cancellationToken: ct));

    if (!sameRfc)
    {
      await tx.RollbackAsync(ct);
      return OrdenTrabajoCommandResult.Fail("La transaccion debe pertenecer al mismo RFC que la orden.");
    }

    await conn.ExecuteAsync(
      new CommandDefinition(
        """
        IF NOT EXISTS (
          SELECT 1
          FROM dbo.OrdenTrabajoTransaccion
          WHERE OrdenTrabajoId = @WorkOrderId
            AND TransaccionId = @TransaccionId
        )
        BEGIN
          INSERT INTO dbo.OrdenTrabajoTransaccion (OrdenTrabajoId, TransaccionId, CreadoPor)
          VALUES (@WorkOrderId, @TransaccionId, @Actor);
        END;
        """,
        new { WorkOrderId = workOrderId, TransaccionId = transaccionId, Actor = safeActor },
        tx,
        cancellationToken: ct));

    await AddAuditAsync(conn, tx, workOrderId, "TRANSACCION_LIGADA", $"Transaccion {transaccionId} ligada.", safeActor, ct);
    await tx.CommitAsync(ct);
    return OrdenTrabajoCommandResult.Ok("Transaccion ligada correctamente.", workOrderId);
  }

  public async Task<OrdenTrabajoCommandResult> UnlinkTransactionAsync(int workOrderId, int transaccionId, string actor, CancellationToken ct = default)
  {
    var safeActor = NormalizeActor(actor);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct);
    var affected = await conn.ExecuteAsync(
      new CommandDefinition(
        """
        DELETE FROM dbo.OrdenTrabajoTransaccion
        WHERE OrdenTrabajoId = @WorkOrderId
          AND TransaccionId = @TransaccionId;
        """,
        new { WorkOrderId = workOrderId, TransaccionId = transaccionId },
        tx,
        cancellationToken: ct));

    if (affected == 0)
    {
      await tx.RollbackAsync(ct);
      return OrdenTrabajoCommandResult.Fail("La liga de transaccion no existe.");
    }

    await AddAuditAsync(conn, tx, workOrderId, "TRANSACCION_DESLIGADA", $"Transaccion {transaccionId} desligada.", safeActor, ct);
    await tx.CommitAsync(ct);
    return OrdenTrabajoCommandResult.Ok("Transaccion desligada correctamente.", workOrderId);
  }

  public async Task<IReadOnlyList<OrdenTrabajoTemplateSummaryDto>> GetTemplatesAsync(string? rfc = null, string? categoryCode = null, CancellationToken ct = default)
  {
    var sql = new StringBuilder(
      """
      SELECT
          tpl.Id,
          tpl.Nombre,
          cat.Codigo AS CategoriaCodigo,
          cat.Nombre AS CategoriaNombre,
          tpl.Rfc,
          tpl.Activa,
          published.Id AS PublishedVersionId,
          published.NumeroVersion AS PublishedVersionNumber,
          (SELECT COUNT(*) FROM dbo.OrdenTrabajoPlantillaVersion v WHERE v.PlantillaId = tpl.Id AND v.Estado = 'BORRADOR') AS DraftVersionCount,
          (SELECT COUNT(*) FROM dbo.OrdenTrabajoPlantillaPaso step WHERE step.PlantillaVersionId = published.Id) AS StepCount
      FROM dbo.OrdenTrabajoPlantilla tpl
      JOIN dbo.OrdenTrabajoCategoria cat ON cat.Id = tpl.CategoriaId
      LEFT JOIN dbo.OrdenTrabajoPlantillaVersion published ON published.PlantillaId = tpl.Id AND published.Estado = 'PUBLICADA'
      WHERE 1 = 1
      """);
    var p = new DynamicParameters();
    if (!string.IsNullOrWhiteSpace(rfc))
    {
      sql.AppendLine(" AND tpl.Rfc = @Rfc");
      p.Add("@Rfc", rfc.Trim());
    }

    if (!string.IsNullOrWhiteSpace(categoryCode))
    {
      sql.AppendLine(" AND cat.Codigo = @CategoryCode");
      p.Add("@CategoryCode", categoryCode.Trim().ToUpperInvariant());
    }

    sql.AppendLine(" ORDER BY cat.Orden, tpl.Nombre;");

    try
    {
      using var conn = CreateConnection();
      var rows = await conn.QueryAsync<OrdenTrabajoTemplateSummaryDto>(
        new CommandDefinition(sql.ToString(), p, cancellationToken: ct));
      return rows.AsList();
    }
    catch (Exception ex) when (IsMissingWorkOrderSchemaException(ex))
    {
      throw CreateMissingSchemaException(ex);
    }
  }

  public async Task<OrdenTrabajoTemplateDetailDto?> GetTemplateDetailAsync(int id, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          tpl.Id,
          tpl.Nombre,
          cat.Codigo AS CategoriaCodigo,
          cat.Nombre AS CategoriaNombre,
          tpl.Rfc,
          tpl.Activa,
          published.Id AS PublishedVersionId,
          published.NumeroVersion AS PublishedVersionNumber,
          draft.Id AS DraftVersionId,
          draft.NumeroVersion AS DraftVersionNumber,
          (SELECT COUNT(*) FROM dbo.OrdenTrabajoPlantillaVersion v WHERE v.PlantillaId = tpl.Id AND v.Estado = 'BORRADOR') AS DraftVersionCount,
          (SELECT COUNT(*) FROM dbo.OrdenTrabajoPlantillaPaso step WHERE step.PlantillaVersionId = COALESCE(draft.Id, published.Id)) AS StepCount
      FROM dbo.OrdenTrabajoPlantilla tpl
      JOIN dbo.OrdenTrabajoCategoria cat ON cat.Id = tpl.CategoriaId
      LEFT JOIN dbo.OrdenTrabajoPlantillaVersion published ON published.PlantillaId = tpl.Id AND published.Estado = 'PUBLICADA'
      OUTER APPLY (
          SELECT TOP (1) v.Id, v.NumeroVersion
          FROM dbo.OrdenTrabajoPlantillaVersion v
          WHERE v.PlantillaId = tpl.Id
            AND v.Estado = 'BORRADOR'
          ORDER BY v.NumeroVersion DESC
      ) draft
      WHERE tpl.Id = @Id;

      SELECT
          step.Id,
          step.Secuencia,
          step.Titulo,
          step.Descripcion,
          step.PoliticaFoto,
          step.RequiereNotasEnIncidencia,
          step.RequiereNotasEnNoAplica,
          step.ProcedimientoId
      FROM dbo.OrdenTrabajoPlantillaVersion ver
      JOIN dbo.OrdenTrabajoPlantillaPaso step ON step.PlantillaVersionId = ver.Id
      WHERE ver.Id = (
          SELECT TOP (1) v.Id
          FROM dbo.OrdenTrabajoPlantillaVersion v
          WHERE v.PlantillaId = @Id
            AND v.Estado IN ('BORRADOR','PUBLICADA')
          ORDER BY CASE WHEN v.Estado = 'BORRADOR' THEN 0 ELSE 1 END, v.NumeroVersion DESC
      )
      ORDER BY step.Secuencia, step.Id;

      SELECT
          room.ID AS RoomId,
          room.ROOM_NAME AS RoomName,
          room.ROOM_TYPE AS RoomType,
          map.PlantillaId AS TemplateId,
          mapped.Nombre AS TemplateName
      FROM dbo.ROOM room
      LEFT JOIN dbo.OrdenTrabajoPlantillaRoom map ON map.RoomId = room.ID
      LEFT JOIN dbo.OrdenTrabajoPlantilla mapped ON mapped.Id = map.PlantillaId
      WHERE room.ROOM_TYPE = 'SUITE'
      ORDER BY room.ROOM_NAME;
      """;

    try
    {
      using var conn = CreateConnection();
      using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
      var detail = await multi.ReadFirstOrDefaultAsync<OrdenTrabajoTemplateDetailDto>();
      if (detail is null)
      {
        return null;
      }

      detail.DraftSteps = (await multi.ReadAsync<OrdenTrabajoTemplateStepDto>()).AsList();
      detail.RoomMappings = (await multi.ReadAsync<OrdenTrabajoRoomTemplateMappingDto>()).AsList();
      return detail;
    }
    catch (Exception ex) when (IsMissingWorkOrderSchemaException(ex))
    {
      throw CreateMissingSchemaException(ex);
    }
  }

  public async Task<OrdenTrabajoCommandResult> SaveTemplateDraftAsync(OrdenTrabajoTemplateSaveRequest request, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    var actor = NormalizeActor(request.SavedBy);
    var categoryCode = NormalizeCode(request.CategoriaCodigo, OrdenTrabajoCodes.CategoriaLimpieza);

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      var categoryId = await ResolveCategoryIdAsync(conn, tx, categoryCode, ct);
      if (categoryId is null)
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("La categoria seleccionada no existe.");
      }

      int templateId;
      if (request.TemplateId.HasValue)
      {
        templateId = request.TemplateId.Value;
        var updated = await conn.ExecuteAsync(
          new CommandDefinition(
            """
            UPDATE dbo.OrdenTrabajoPlantilla
            SET Nombre = @Nombre,
                CategoriaId = @CategoriaId,
                Rfc = @Rfc,
                Activa = @Activa,
                ActualizadaEn = SYSUTCDATETIME(),
                ActualizadaPor = @Actor
            WHERE Id = @TemplateId;
            """,
            new
            {
              TemplateId = templateId,
              Nombre = RequireText(request.Nombre, "El nombre de la plantilla es obligatorio."),
              CategoriaId = categoryId.Value,
              Rfc = RequireText(request.Rfc, "El RFC es obligatorio."),
              request.Activa,
              Actor = actor
            },
            tx,
            cancellationToken: ct));

        if (updated == 0)
        {
          await tx.RollbackAsync(ct);
          return OrdenTrabajoCommandResult.Fail("La plantilla no existe.");
        }
      }
      else
      {
        templateId = await conn.ExecuteScalarAsync<int>(
          new CommandDefinition(
            """
            INSERT INTO dbo.OrdenTrabajoPlantilla (CategoriaId, Rfc, Nombre, Activa, CreadaPor)
            VALUES (@CategoriaId, @Rfc, @Nombre, @Activa, @Actor);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new
            {
              CategoriaId = categoryId.Value,
              Rfc = RequireText(request.Rfc, "El RFC es obligatorio."),
              Nombre = RequireText(request.Nombre, "El nombre de la plantilla es obligatorio."),
              request.Activa,
              Actor = actor
            },
            tx,
            cancellationToken: ct));
      }

      var draftVersionId = await conn.ExecuteScalarAsync<int?>(
        new CommandDefinition(
          """
          SELECT TOP (1) Id
          FROM dbo.OrdenTrabajoPlantillaVersion
          WHERE PlantillaId = @TemplateId
            AND Estado = 'BORRADOR'
          ORDER BY NumeroVersion DESC;
          """,
          new { TemplateId = templateId },
          tx,
          cancellationToken: ct));

      if (!draftVersionId.HasValue)
      {
        draftVersionId = await conn.ExecuteScalarAsync<int>(
          new CommandDefinition(
            """
            DECLARE @NextVersion int =
            (
              SELECT ISNULL(MAX(NumeroVersion), 0) + 1
              FROM dbo.OrdenTrabajoPlantillaVersion
              WHERE PlantillaId = @TemplateId
            );

            INSERT INTO dbo.OrdenTrabajoPlantillaVersion (PlantillaId, NumeroVersion, Estado, CreadaPor)
            VALUES (@TemplateId, @NextVersion, 'BORRADOR', @Actor);

            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new { TemplateId = templateId, Actor = actor },
            tx,
            cancellationToken: ct));
      }

      await conn.ExecuteAsync(
        new CommandDefinition(
          "DELETE FROM dbo.OrdenTrabajoPlantillaPaso WHERE PlantillaVersionId = @VersionId;",
          new { VersionId = draftVersionId.Value },
          tx,
          cancellationToken: ct));

      foreach (var step in request.Steps.OrderBy(step => step.Secuencia))
      {
        await conn.ExecuteAsync(
          new CommandDefinition(
            """
            INSERT INTO dbo.OrdenTrabajoPlantillaPaso
            (
                PlantillaVersionId,
                Secuencia,
                Titulo,
                Descripcion,
                PoliticaFoto,
                RequiereNotasEnIncidencia,
                RequiereNotasEnNoAplica,
                ProcedimientoId
            )
            VALUES
            (
                @VersionId,
                @Secuencia,
                @Titulo,
                @Descripcion,
                @PoliticaFoto,
                @RequiereNotasEnIncidencia,
                @RequiereNotasEnNoAplica,
                @ProcedimientoId
            );
            """,
            new
            {
              VersionId = draftVersionId.Value,
              step.Secuencia,
              Titulo = RequireText(step.Titulo, "El titulo del paso es obligatorio."),
              Descripcion = RequireText(step.Descripcion, "La descripcion del paso es obligatoria."),
              PoliticaFoto = NormalizePhotoPolicy(step.PoliticaFoto),
              step.RequiereNotasEnIncidencia,
              step.RequiereNotasEnNoAplica,
              step.ProcedimientoId
            },
            tx,
            cancellationToken: ct));
      }

      await tx.CommitAsync(ct);
      return OrdenTrabajoCommandResult.Ok("Borrador de plantilla guardado.", templateId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<OrdenTrabajoCommandResult> PublishTemplateAsync(int templateId, string actor, CancellationToken ct = default)
  {
    var safeActor = NormalizeActor(actor);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      var draft = await conn.QueryFirstOrDefaultAsync<(int Id, int StepCount)>(
        new CommandDefinition(
          """
          SELECT TOP (1)
              v.Id,
              (SELECT COUNT(*) FROM dbo.OrdenTrabajoPlantillaPaso p WHERE p.PlantillaVersionId = v.Id) AS StepCount
          FROM dbo.OrdenTrabajoPlantillaVersion v
          WHERE v.PlantillaId = @TemplateId
            AND v.Estado = 'BORRADOR'
          ORDER BY v.NumeroVersion DESC;
          """,
          new { TemplateId = templateId },
          tx,
          cancellationToken: ct));

      if (draft.Id == 0)
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("La plantilla no tiene borrador para publicar.");
      }

      if (draft.StepCount == 0)
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("La plantilla debe tener al menos un paso para publicarse.");
      }

      await conn.ExecuteAsync(
        new CommandDefinition(
          """
          UPDATE dbo.OrdenTrabajoPlantillaVersion
          SET Estado = 'ARCHIVADA'
          WHERE PlantillaId = @TemplateId
            AND Estado = 'PUBLICADA';

          UPDATE dbo.OrdenTrabajoPlantillaVersion
          SET Estado = 'PUBLICADA',
              PublicadaEn = SYSUTCDATETIME(),
              PublicadaPor = @Actor
          WHERE Id = @DraftId;
          """,
          new { TemplateId = templateId, DraftId = draft.Id, Actor = safeActor },
          tx,
          cancellationToken: ct));

      await tx.CommitAsync(ct);
      return OrdenTrabajoCommandResult.Ok("Plantilla publicada.", templateId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<OrdenTrabajoCommandResult> MapRoomTemplateAsync(int roomId, int templateId, string actor, CancellationToken ct = default)
  {
    var safeActor = NormalizeActor(actor);
    const string sql =
      """
      IF NOT EXISTS (
        SELECT 1
        FROM dbo.ROOM
        WHERE ID = @RoomId
          AND ROOM_TYPE = 'SUITE'
      )
      BEGIN
        SELECT CAST(0 AS int);
        RETURN;
      END;

      IF NOT EXISTS (
        SELECT 1
        FROM dbo.OrdenTrabajoPlantilla tpl
        JOIN dbo.OrdenTrabajoCategoria cat ON cat.Id = tpl.CategoriaId
        JOIN dbo.OrdenTrabajoPlantillaVersion ver ON ver.PlantillaId = tpl.Id AND ver.Estado = 'PUBLICADA'
        WHERE tpl.Id = @TemplateId
          AND cat.Codigo = 'LIMPIEZA'
          AND tpl.Activa = 1
      )
      BEGIN
        SELECT CAST(-1 AS int);
        RETURN;
      END;

      MERGE dbo.OrdenTrabajoPlantillaRoom AS target
      USING (SELECT @RoomId AS RoomId, @TemplateId AS PlantillaId) AS source
      ON target.RoomId = source.RoomId
      WHEN MATCHED THEN
        UPDATE SET PlantillaId = source.PlantillaId, ActualizadaEn = SYSUTCDATETIME(), ActualizadaPor = @Actor
      WHEN NOT MATCHED THEN
        INSERT (RoomId, PlantillaId, ActualizadaPor)
        VALUES (source.RoomId, source.PlantillaId, @Actor);

      SELECT CAST(1 AS int);
      """;

    using var conn = CreateConnection();
    var result = await conn.ExecuteScalarAsync<int>(
      new CommandDefinition(sql, new { RoomId = roomId, TemplateId = templateId, Actor = safeActor }, cancellationToken: ct));

    return result switch
    {
      1 => OrdenTrabajoCommandResult.Ok("Plantilla asignada a suite.", roomId),
      0 => OrdenTrabajoCommandResult.Fail("La suite seleccionada no existe."),
      _ => OrdenTrabajoCommandResult.Fail("La plantilla debe ser de limpieza y tener version publicada.")
    };
  }

  public async Task<OrdenTrabajoCommandResult> SeedCleaningTemplatesFromLegacyAsync(string rfc, string actor, CancellationToken ct = default)
  {
    var safeRfc = RequireText(rfc, "El RFC es obligatorio.");
    var safeActor = NormalizeActor(actor);

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);

    try
    {
      var categoryId = await ResolveCategoryIdAsync(conn, tx, OrdenTrabajoCodes.CategoriaLimpieza, ct);
      if (categoryId is null)
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("No existe la categoria Limpieza.");
      }

      var legacyTemplates = (await conn.QueryAsync<LegacyTemplateHeader>(
        new CommandDefinition(
          """
          ;WITH legacy_sources AS (
            SELECT
                a.ID AS ActividadId,
                a.Descripcion AS TemplateName,
                LTRIM(RTRIM(REPLACE(a.Descripcion, 'PLANTILLA PARA LIMPIEZA ', ''))) AS RoomName,
                0 AS SourceRank
            FROM dbo.Actividad a
            WHERE a.Descripcion LIKE 'PLANTILLA PARA LIMPIEZA %'

            UNION ALL

            SELECT
                a.ID AS ActividadId,
                CONCAT('PLANTILLA PARA LIMPIEZA ', room.ROOM_NAME) AS TemplateName,
                room.ROOM_NAME AS RoomName,
                1 AS SourceRank
            FROM dbo.Actividad a
            JOIN dbo.ROOM room
              ON room.ROOM_TYPE = 'SUITE'
             AND a.Descripcion LIKE '%LIMPIEZA%'
             AND a.Descripcion LIKE '%' + room.ROOM_NAME + '%'
            WHERE a.Descripcion NOT LIKE 'PLANTILLA PARA LIMPIEZA %'
          ),
          candidates AS (
            SELECT
                src.ActividadId,
                src.TemplateName,
                src.RoomName,
                src.SourceRank,
                COUNT(rc.ID) AS StepCount
            FROM legacy_sources src
            LEFT JOIN dbo.Actividad_Ruta_Critica rc ON rc.Actividad_ID = src.ActividadId
            GROUP BY src.ActividadId, src.TemplateName, src.RoomName, src.SourceRank
          ),
          ranked AS (
            SELECT
                ActividadId,
                TemplateName,
                RoomName,
                StepCount,
                ROW_NUMBER() OVER (
                  PARTITION BY RoomName
                  ORDER BY
                    CASE WHEN SourceRank = 0 AND StepCount > 0 THEN 0 ELSE SourceRank END,
                    StepCount DESC,
                    ActividadId DESC
                ) AS rn
            FROM candidates
            WHERE StepCount > 0
          )
          SELECT ActividadId, TemplateName, RoomName, StepCount
          FROM ranked
          WHERE rn = 1
          ORDER BY RoomName;
          """,
          transaction: tx,
          cancellationToken: ct))).AsList();

      var created = 0;
      var mapped = 0;
      foreach (var legacy in legacyTemplates)
      {
        var templateId = await conn.ExecuteScalarAsync<int?>(
          new CommandDefinition(
            "SELECT Id FROM dbo.OrdenTrabajoPlantilla WHERE Rfc = @Rfc AND Nombre = @Name;",
            new { Rfc = safeRfc, Name = legacy.TemplateName },
            tx,
            cancellationToken: ct));

        if (!templateId.HasValue)
        {
          templateId = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(
              """
              INSERT INTO dbo.OrdenTrabajoPlantilla (CategoriaId, Rfc, Nombre, Activa, CreadaPor)
              VALUES (@CategoryId, @Rfc, @Name, 1, @Actor);
              SELECT CAST(SCOPE_IDENTITY() AS int);
              """,
              new { CategoryId = categoryId.Value, Rfc = safeRfc, Name = legacy.TemplateName, Actor = safeActor },
              tx,
              cancellationToken: ct));
          created++;
        }

        var hasPublishedWithSteps = await conn.ExecuteScalarAsync<bool>(
          new CommandDefinition(
            """
            SELECT CAST(CASE WHEN EXISTS (
              SELECT 1
              FROM dbo.OrdenTrabajoPlantillaVersion ver
              WHERE ver.PlantillaId = @TemplateId
                AND ver.Estado = 'PUBLICADA'
                AND EXISTS (
                  SELECT 1
                  FROM dbo.OrdenTrabajoPlantillaPaso step
                  WHERE step.PlantillaVersionId = ver.Id
                )
            ) THEN 1 ELSE 0 END AS bit);
            """,
            new { TemplateId = templateId.Value },
            tx,
            cancellationToken: ct));

        if (!hasPublishedWithSteps)
        {
          await conn.ExecuteAsync(
            new CommandDefinition(
              """
              UPDATE dbo.OrdenTrabajoPlantillaVersion
              SET Estado = 'ARCHIVADA'
              WHERE PlantillaId = @TemplateId
                AND Estado = 'PUBLICADA';
              """,
              new { TemplateId = templateId.Value },
              tx,
              cancellationToken: ct));

          var versionId = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(
              """
              INSERT INTO dbo.OrdenTrabajoPlantillaVersion (PlantillaId, NumeroVersion, Estado, CreadaPor, PublicadaEn, PublicadaPor)
              SELECT @TemplateId, ISNULL(MAX(NumeroVersion), 0) + 1, 'PUBLICADA', @Actor, SYSUTCDATETIME(), @Actor
              FROM dbo.OrdenTrabajoPlantillaVersion
              WHERE PlantillaId = @TemplateId;
              SELECT CAST(SCOPE_IDENTITY() AS int);
              """,
              new { TemplateId = templateId.Value, Actor = safeActor },
              tx,
              cancellationToken: ct));

          var legacySteps = (await conn.QueryAsync<LegacyTemplateStep>(
            new CommandDefinition(
              """
              SELECT
                  ROW_NUMBER() OVER (ORDER BY rc.Paso_Numero, rc.ID) AS RowNumber,
                  CAST(rc.Paso_Numero AS decimal(9,2)) AS Secuencia,
                  rc.Descripcion,
                  rc.Procedimiento_ID AS ProcedimientoId
              FROM dbo.Actividad_Ruta_Critica rc
              WHERE rc.Actividad_ID = @ActividadId
              ORDER BY rc.Paso_Numero, rc.ID;
              """,
              new { legacy.ActividadId },
              tx,
              cancellationToken: ct))).AsList();

          foreach (var step in legacySteps)
          {
            var title = BuildStepTitle(step.RowNumber, step.Descripcion);
            await conn.ExecuteAsync(
              new CommandDefinition(
                """
                INSERT INTO dbo.OrdenTrabajoPlantillaPaso
                (
                    PlantillaVersionId,
                    Secuencia,
                    Titulo,
                    Descripcion,
                    PoliticaFoto,
                    RequiereNotasEnIncidencia,
                    RequiereNotasEnNoAplica,
                    ProcedimientoId
                )
                VALUES
                (
                    @VersionId,
                    @Secuencia,
                    @Titulo,
                    @Descripcion,
                    @PoliticaFoto,
                    1,
                    1,
                    @ProcedimientoId
                );
                """,
                new
                {
                  VersionId = versionId,
                  Secuencia = step.Secuencia == 0 ? step.RowNumber : step.Secuencia,
                  Titulo = title,
                  Descripcion = RequireText(step.Descripcion, "Paso sin descripcion."),
                  PoliticaFoto = InferLegacyPhotoPolicy(step.Descripcion),
                  step.ProcedimientoId
                },
                tx,
                cancellationToken: ct));
          }
        }

        var roomId = await conn.ExecuteScalarAsync<int?>(
          new CommandDefinition(
            "SELECT TOP (1) ID FROM dbo.ROOM WHERE ROOM_TYPE = 'SUITE' AND ROOM_NAME = @RoomName;",
            new { legacy.RoomName },
            tx,
            cancellationToken: ct));

        if (roomId.HasValue)
        {
          await conn.ExecuteAsync(
            new CommandDefinition(
              """
              MERGE dbo.OrdenTrabajoPlantillaRoom AS target
              USING (SELECT @RoomId AS RoomId, @TemplateId AS PlantillaId) AS source
              ON target.RoomId = source.RoomId
              WHEN MATCHED THEN
                UPDATE SET PlantillaId = source.PlantillaId, ActualizadaEn = SYSUTCDATETIME(), ActualizadaPor = @Actor
              WHEN NOT MATCHED THEN
                INSERT (RoomId, PlantillaId, ActualizadaPor)
                VALUES (source.RoomId, source.PlantillaId, @Actor);
              """,
              new { RoomId = roomId.Value, TemplateId = templateId.Value, Actor = safeActor },
              tx,
              cancellationToken: ct));
          mapped++;
        }
      }

      await tx.CommitAsync(ct);
      return OrdenTrabajoCommandResult.Ok($"Seed terminado. Plantillas nuevas: {created}. Mapeos de suite: {mapped}.");
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<OrdenTrabajoCommandResult> SeedChecklistTemplatesFromLegacyAsync(string rfc, string actor, int asignacion = 36, CancellationToken ct = default)
  {
    if (asignacion <= 0)
    {
      return OrdenTrabajoCommandResult.Fail("La asignacion legacy debe ser mayor a cero.");
    }

    var safeRfc = RequireText(rfc, "El RFC es obligatorio.");
    var safeActor = NormalizeActor(actor);

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);

    try
    {
      var categoryId = await ResolveCategoryIdAsync(conn, tx, OrdenTrabajoCodes.CategoriaChecklist, ct);
      if (categoryId is null)
      {
        await tx.RollbackAsync(ct);
        return OrdenTrabajoCommandResult.Fail("No existe la categoria Checklist.");
      }

      var legacyTemplates = (await conn.QueryAsync<LegacyTemplateHeader>(
        new CommandDefinition(
          """
          ;WITH checklist_sources AS (
            SELECT
                a.ID AS ActividadId,
                COALESCE(NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(200), a.Descripcion))), ''), CONCAT(N'Checklist ', a.ID)) AS BaseTemplateName,
                COUNT(rc.ID) AS StepCount
            FROM dbo.Actividad a
            LEFT JOIN dbo.Actividad_Ruta_Critica rc ON rc.Actividad_ID = a.ID
            WHERE UPPER(LTRIM(RTRIM(ISNULL(CONVERT(nvarchar(50), a.Tipo_Proyecto), N'')))) = N'CHECKLIST'
              AND a.Asignacion = @Asignacion
            GROUP BY
                a.ID,
                COALESCE(NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(200), a.Descripcion))), ''), CONCAT(N'Checklist ', a.ID))
          ),
          ranked AS (
            SELECT
                ActividadId,
                BaseTemplateName,
                StepCount,
                COUNT(*) OVER (PARTITION BY BaseTemplateName) AS DuplicateNameCount
            FROM checklist_sources
          )
          SELECT
              ActividadId,
              CASE
                WHEN DuplicateNameCount > 1
                  THEN CONVERT(nvarchar(200), CONCAT(LEFT(BaseTemplateName, 175), N' (Actividad ', ActividadId, N')'))
                ELSE BaseTemplateName
              END AS TemplateName,
              CAST(N'' AS nvarchar(100)) AS RoomName,
              StepCount
          FROM ranked
          ORDER BY TemplateName, ActividadId;
          """,
          new { Asignacion = asignacion },
          tx,
          cancellationToken: ct))).AsList();

      if (legacyTemplates.Count == 0)
      {
        await tx.CommitAsync(ct);
        return OrdenTrabajoCommandResult.Ok("No se encontraron checklists legacy para importar.");
      }

      var created = 0;
      var published = 0;
      var fallbackSteps = 0;
      foreach (var legacy in legacyTemplates)
      {
        var templateName = Truncate(RequireText(legacy.TemplateName, "Actividad sin descripcion."), 200);
        var templateMatch = await FindTemplateByNameAsync(conn, tx, safeRfc, templateName, ct);
        if (templateMatch is not null
          && !string.Equals(templateMatch.CategoriaCodigo, OrdenTrabajoCodes.CategoriaChecklist, StringComparison.OrdinalIgnoreCase))
        {
          templateName = Truncate($"{Truncate(templateName, 170)} (Actividad {legacy.ActividadId})", 200);
          templateMatch = await FindTemplateByNameAsync(conn, tx, safeRfc, templateName, ct);
        }

        if (templateMatch is not null
          && !string.Equals(templateMatch.CategoriaCodigo, OrdenTrabajoCodes.CategoriaChecklist, StringComparison.OrdinalIgnoreCase))
        {
          await tx.RollbackAsync(ct);
          return OrdenTrabajoCommandResult.Fail($"Ya existe una plantilla llamada {templateName} en otra categoria.");
        }

        var templateId = templateMatch?.Id;
        if (!templateId.HasValue)
        {
          templateId = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(
              """
              INSERT INTO dbo.OrdenTrabajoPlantilla (CategoriaId, Rfc, Nombre, Activa, CreadaPor)
              VALUES (@CategoryId, @Rfc, @Name, 1, @Actor);
              SELECT CAST(SCOPE_IDENTITY() AS int);
              """,
              new { CategoryId = categoryId.Value, Rfc = safeRfc, Name = templateName, Actor = safeActor },
              tx,
              cancellationToken: ct));
          created++;
        }
        else
        {
          await conn.ExecuteAsync(
            new CommandDefinition(
              """
              UPDATE dbo.OrdenTrabajoPlantilla
              SET Activa = 1,
                  ActualizadaEn = SYSUTCDATETIME(),
                  ActualizadaPor = @Actor
              WHERE Id = @TemplateId
                AND Activa = 0;
              """,
              new { TemplateId = templateId.Value, Actor = safeActor },
              tx,
              cancellationToken: ct));
        }

        var hasPublishedWithSteps = await conn.ExecuteScalarAsync<bool>(
          new CommandDefinition(
            """
            SELECT CAST(CASE WHEN EXISTS (
              SELECT 1
              FROM dbo.OrdenTrabajoPlantillaVersion ver
              WHERE ver.PlantillaId = @TemplateId
                AND ver.Estado = 'PUBLICADA'
                AND EXISTS (
                  SELECT 1
                  FROM dbo.OrdenTrabajoPlantillaPaso step
                  WHERE step.PlantillaVersionId = ver.Id
                )
            ) THEN 1 ELSE 0 END AS bit);
            """,
            new { TemplateId = templateId.Value },
            tx,
            cancellationToken: ct));

        if (hasPublishedWithSteps)
        {
          continue;
        }

        await conn.ExecuteAsync(
          new CommandDefinition(
            """
            UPDATE dbo.OrdenTrabajoPlantillaVersion
            SET Estado = 'ARCHIVADA'
            WHERE PlantillaId = @TemplateId
              AND Estado = 'PUBLICADA';
            """,
            new { TemplateId = templateId.Value },
            tx,
            cancellationToken: ct));

        var versionId = await conn.ExecuteScalarAsync<int>(
          new CommandDefinition(
            """
            INSERT INTO dbo.OrdenTrabajoPlantillaVersion (PlantillaId, NumeroVersion, Estado, CreadaPor, PublicadaEn, PublicadaPor)
            SELECT @TemplateId, ISNULL(MAX(NumeroVersion), 0) + 1, 'PUBLICADA', @Actor, SYSUTCDATETIME(), @Actor
            FROM dbo.OrdenTrabajoPlantillaVersion
            WHERE PlantillaId = @TemplateId;
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new { TemplateId = templateId.Value, Actor = safeActor },
            tx,
            cancellationToken: ct));
        published++;

        var legacySteps = (await conn.QueryAsync<LegacyTemplateStep>(
          new CommandDefinition(
            """
            ;WITH route_steps AS (
              SELECT
                  ROW_NUMBER() OVER (ORDER BY rc.Paso_Numero, rc.ID) AS RowNumber,
                  CAST(ISNULL(rc.Paso_Numero, 0) AS decimal(9,2)) AS Secuencia,
                  COALESCE(NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(1000), rc.Descripcion))), ''), @TemplateName) AS Descripcion,
                  rc.Procedimiento_ID AS ProcedimientoId
              FROM dbo.Actividad_Ruta_Critica rc
              WHERE rc.Actividad_ID = @ActividadId
            ),
            fallback_step AS (
              SELECT
                  1 AS RowNumber,
                  CAST(1 AS decimal(9,2)) AS Secuencia,
                  @TemplateName AS Descripcion,
                  CAST(NULL AS int) AS ProcedimientoId
              WHERE NOT EXISTS (
                SELECT 1
                FROM dbo.Actividad_Ruta_Critica rc
                WHERE rc.Actividad_ID = @ActividadId
              )
            )
            SELECT RowNumber, Secuencia, Descripcion, ProcedimientoId
            FROM route_steps
            UNION ALL
            SELECT RowNumber, Secuencia, Descripcion, ProcedimientoId
            FROM fallback_step
            ORDER BY RowNumber;
            """,
            new { legacy.ActividadId, TemplateName = templateName },
            tx,
            cancellationToken: ct))).AsList();

        if (legacy.StepCount == 0)
        {
          fallbackSteps++;
        }

        foreach (var step in legacySteps)
        {
          var title = BuildStepTitle(step.RowNumber, step.Descripcion);
          await conn.ExecuteAsync(
            new CommandDefinition(
              """
              INSERT INTO dbo.OrdenTrabajoPlantillaPaso
              (
                  PlantillaVersionId,
                  Secuencia,
                  Titulo,
                  Descripcion,
                  PoliticaFoto,
                  RequiereNotasEnIncidencia,
                  RequiereNotasEnNoAplica,
                  ProcedimientoId
              )
              VALUES
              (
                  @VersionId,
                  @Secuencia,
                  @Titulo,
                  @Descripcion,
                  @PoliticaFoto,
                  1,
                  1,
                  @ProcedimientoId
              );
              """,
              new
              {
                VersionId = versionId,
                Secuencia = step.Secuencia == 0 ? step.RowNumber : step.Secuencia,
                Titulo = title,
                Descripcion = Truncate(RequireText(step.Descripcion, "Paso sin descripcion."), 1000),
                PoliticaFoto = InferLegacyPhotoPolicy(step.Descripcion),
                step.ProcedimientoId
              },
              tx,
              cancellationToken: ct));
        }
      }

      await tx.CommitAsync(ct);
      return OrdenTrabajoCommandResult.Ok($"Importacion checklist terminada. Actividades: {legacyTemplates.Count}. Plantillas nuevas: {created}. Versiones publicadas: {published}. Pasos fallback: {fallbackSteps}.");
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<IReadOnlyList<OrdenTrabajoCalendarBadgeDto>> GetCalendarBadgesAsync(DateTime startDate, DateTime endDateExclusive, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          ot.RoomCalendarId,
          ot.Id AS WorkOrderId,
          ot.Folio,
          ot.Estado,
          cat.Codigo AS CategoriaCodigo,
          COALESCE(NULLIF(LTRIM(RTRIM(ownerEmployee.NombreCorto)), ''), CONCAT(ownerEmployee.Nombre, ' ', ownerEmployee.ApellidoPaterno)) AS OwnerName,
          helpers.HelperNames
      FROM dbo.OrdenTrabajo ot
      JOIN dbo.OrdenTrabajoCategoria cat ON cat.Id = ot.CategoriaId
      JOIN dbo.Capital_Humano ownerEmployee ON ownerEmployee.ID = ot.OwnerEmployeeId
      OUTER APPLY (
          SELECT STUFF((
              SELECT '/' + COALESCE(NULLIF(LTRIM(RTRIM(helperEmployee.NombreCorto)), ''), CONCAT(helperEmployee.Nombre, ' ', helperEmployee.ApellidoPaterno))
              FROM dbo.OrdenTrabajoParticipante participant
              JOIN dbo.Capital_Humano helperEmployee ON helperEmployee.ID = participant.EmployeeId
              WHERE participant.OrdenTrabajoId = ot.Id
              ORDER BY COALESCE(NULLIF(LTRIM(RTRIM(helperEmployee.NombreCorto)), ''), CONCAT(helperEmployee.Nombre, ' ', helperEmployee.ApellidoPaterno))
              FOR XML PATH(''), TYPE
          ).value('.', 'nvarchar(max)'), 1, 1, '') AS HelperNames
      ) helpers
      WHERE ot.RoomCalendarId IS NOT NULL
        AND ot.FechaProgramada >= @StartDate
        AND ot.FechaProgramada < @EndDateExclusive
        AND cat.Codigo = 'LIMPIEZA'
        AND ot.Estado <> 'CANCELADA'
      ORDER BY ot.FechaProgramada, ot.Id;
      """;

    try
    {
      using var conn = CreateConnection();
      var rows = await conn.QueryAsync<OrdenTrabajoCalendarBadgeDto>(
        new CommandDefinition(sql, new { StartDate = startDate.Date, EndDateExclusive = endDateExclusive.Date }, cancellationToken: ct));
      return rows.AsList();
    }
    catch (Exception ex) when (IsMissingWorkOrderSchemaException(ex))
    {
      throw CreateMissingSchemaException(ex);
    }
  }

  private async Task<int?> ResolveCategoryIdAsync(DbConnection conn, IDbTransaction tx, string categoryCode, CancellationToken ct)
    => await conn.ExecuteScalarAsync<int?>(
      new CommandDefinition(
        "SELECT Id FROM dbo.OrdenTrabajoCategoria WHERE Codigo = @CategoryCode AND Activa = 1;",
        new { CategoryCode = categoryCode },
        tx,
        cancellationToken: ct));

  private async Task<bool> EmployeeExistsAsync(DbConnection conn, IDbTransaction tx, int employeeId, CancellationToken ct)
    => await conn.ExecuteScalarAsync<bool>(
      new CommandDefinition(
        """
        SELECT CAST(CASE WHEN EXISTS (
            SELECT 1
            FROM dbo.Capital_Humano
            WHERE ID = @EmployeeId
              AND UPPER(LTRIM(RTRIM(ISNULL([Status], '')))) = 'ACTIVO'
        ) THEN 1 ELSE 0 END AS bit);
        """,
        new { EmployeeId = employeeId },
        tx,
        cancellationToken: ct));

  private async Task<LegacyTemplateMatch?> FindTemplateByNameAsync(DbConnection conn, IDbTransaction tx, string rfc, string name, CancellationToken ct)
    => await conn.QueryFirstOrDefaultAsync<LegacyTemplateMatch>(
      new CommandDefinition(
        """
        SELECT
            tpl.Id,
            cat.Codigo AS CategoriaCodigo
        FROM dbo.OrdenTrabajoPlantilla tpl
        JOIN dbo.OrdenTrabajoCategoria cat ON cat.Id = tpl.CategoriaId
        WHERE tpl.Rfc = @Rfc
          AND tpl.Nombre = @Name;
        """,
        new { Rfc = rfc, Name = name },
        tx,
        cancellationToken: ct));

  private async Task<PublishedTemplateRow?> GetPublishedTemplateAsync(DbConnection conn, IDbTransaction tx, int templateId, string rfc, string categoryCode, CancellationToken ct)
    => await conn.QueryFirstOrDefaultAsync<PublishedTemplateRow>(
      new CommandDefinition(
        """
        SELECT
            tpl.Id AS TemplateId,
            ver.Id AS VersionId,
            tpl.Nombre AS TemplateName
        FROM dbo.OrdenTrabajoPlantilla tpl
        JOIN dbo.OrdenTrabajoCategoria cat ON cat.Id = tpl.CategoriaId
        JOIN dbo.OrdenTrabajoPlantillaVersion ver ON ver.PlantillaId = tpl.Id AND ver.Estado = 'PUBLICADA'
        WHERE tpl.Id = @TemplateId
          AND tpl.Activa = 1
          AND tpl.Rfc = @Rfc
          AND cat.Codigo = @CategoryCode;
        """,
        new { TemplateId = templateId, Rfc = rfc, CategoryCode = categoryCode },
        tx,
        cancellationToken: ct));

  private async Task<string> GenerateFolioAsync(DbConnection conn, IDbTransaction tx, int year, CancellationToken ct)
  {
    const string sql =
      """
      DECLARE @Next int;

      UPDATE dbo.OrdenTrabajoFolioAnual WITH (UPDLOCK, HOLDLOCK)
      SET @Next = UltimoConsecutivo = UltimoConsecutivo + 1,
          ActualizadoEn = SYSUTCDATETIME()
      WHERE Anio = @Year;

      IF @Next IS NULL
      BEGIN
          SET @Next = 1;
          INSERT INTO dbo.OrdenTrabajoFolioAnual (Anio, UltimoConsecutivo)
          VALUES (@Year, @Next);
      END;

      SELECT CONCAT('OT-', @Year, '-', RIGHT(REPLICATE('0', 6) + CAST(@Next AS varchar(20)), 6));
      """;

    return await conn.ExecuteScalarAsync<string>(
      new CommandDefinition(sql, new { Year = year }, tx, cancellationToken: ct))
      ?? throw new InvalidOperationException("No se pudo generar el folio.");
  }

  private async Task<int> InsertWorkOrderAsync(DbConnection conn, IDbTransaction tx, WorkOrderInsertArgs args, CancellationToken ct)
    => await conn.ExecuteScalarAsync<int>(
      new CommandDefinition(
        """
        INSERT INTO dbo.OrdenTrabajo
        (
            Folio,
            Rfc,
            CategoriaId,
            Estado,
            Prioridad,
            Titulo,
            Descripcion,
            OwnerEmployeeId,
            FechaProgramada,
            HoraInicioProgramada,
            HoraFinProgramada,
            FechaVencimiento,
            RoomId,
            RoomCalendarId,
            ReservationId,
            Ubicacion,
            PlantillaId,
            PlantillaVersionId,
            EstimatedCost,
            CreadaPor
        )
        VALUES
        (
            @Folio,
            @Rfc,
            @CategoryId,
            @Estado,
            @Prioridad,
            @Titulo,
            @Descripcion,
            @OwnerEmployeeId,
            @FechaProgramada,
            @HoraInicioProgramada,
            @HoraFinProgramada,
            @FechaVencimiento,
            @RoomId,
            @RoomCalendarId,
            @ReservationId,
            @Ubicacion,
            @PlantillaId,
            @PlantillaVersionId,
            @EstimatedCost,
            @Actor
        );

        SELECT CAST(SCOPE_IDENTITY() AS int);
        """,
        args,
        tx,
        cancellationToken: ct));

  private async Task ReplaceHelpersAsync(DbConnection conn, IDbTransaction tx, int workOrderId, IEnumerable<int> helperEmployeeIds, string actor, CancellationToken ct)
  {
    await conn.ExecuteAsync(
      new CommandDefinition(
        "DELETE FROM dbo.OrdenTrabajoParticipante WHERE OrdenTrabajoId = @WorkOrderId;",
        new { WorkOrderId = workOrderId },
        tx,
        cancellationToken: ct));

    foreach (var helperId in helperEmployeeIds.Distinct().Where(id => id > 0))
    {
      if (!await EmployeeExistsAsync(conn, tx, helperId, ct))
      {
        continue;
      }

      await conn.ExecuteAsync(
        new CommandDefinition(
          """
          INSERT INTO dbo.OrdenTrabajoParticipante (OrdenTrabajoId, EmployeeId, CreadoPor)
          VALUES (@WorkOrderId, @EmployeeId, @Actor);
          """,
          new { WorkOrderId = workOrderId, EmployeeId = helperId, Actor = actor },
          tx,
          cancellationToken: ct));
    }
  }

  private async Task CreateStepsFromTemplateOrDefaultAsync(
    DbConnection conn,
    IDbTransaction tx,
    int workOrderId,
    int? templateVersionId,
    string title,
    string? description,
    CancellationToken ct)
  {
    if (templateVersionId.HasValue)
    {
      await conn.ExecuteAsync(
        new CommandDefinition(
          """
          INSERT INTO dbo.OrdenTrabajoPaso
          (
              OrdenTrabajoId,
              PlantillaPasoId,
              Secuencia,
              Titulo,
              Descripcion,
              Estado,
              PoliticaFoto,
              RequiereNotasEnIncidencia,
              RequiereNotasEnNoAplica,
              ProcedimientoId
          )
          SELECT
              @WorkOrderId,
              step.Id,
              step.Secuencia,
              step.Titulo,
              step.Descripcion,
              'PENDIENTE',
              step.PoliticaFoto,
              step.RequiereNotasEnIncidencia,
              step.RequiereNotasEnNoAplica,
              step.ProcedimientoId
          FROM dbo.OrdenTrabajoPlantillaPaso step
          WHERE step.PlantillaVersionId = @TemplateVersionId
          ORDER BY step.Secuencia, step.Id;
          """,
          new { WorkOrderId = workOrderId, TemplateVersionId = templateVersionId.Value },
          tx,
          cancellationToken: ct));
      return;
    }

    await conn.ExecuteAsync(
      new CommandDefinition(
        """
        INSERT INTO dbo.OrdenTrabajoPaso
        (
            OrdenTrabajoId,
            Secuencia,
            Titulo,
            Descripcion,
            Estado,
            PoliticaFoto,
            RequiereNotasEnIncidencia,
            RequiereNotasEnNoAplica
        )
        VALUES
        (
            @WorkOrderId,
            1,
            @Title,
            @Description,
            'PENDIENTE',
            'NO_PERMITIDA',
            1,
            1
        );
        """,
        new
        {
          WorkOrderId = workOrderId,
          Title = Truncate(NullIfWhiteSpace(title) ?? "Actividad general", 200),
          Description = Truncate(NullIfWhiteSpace(description) ?? NullIfWhiteSpace(title) ?? "Actividad general", 1000)
        },
        tx,
        cancellationToken: ct));
  }

  private async Task AddAuditAsync(DbConnection conn, IDbTransaction tx, int workOrderId, string eventName, string? detail, string actor, CancellationToken ct)
    => await conn.ExecuteAsync(
      new CommandDefinition(
        """
        INSERT INTO dbo.OrdenTrabajoAuditoria (OrdenTrabajoId, Evento, Detalle, CreadoPor)
        VALUES (@WorkOrderId, @EventName, @Detail, @Actor);
        """,
        new
        {
          WorkOrderId = workOrderId,
          EventName = eventName,
          Detail = NullIfWhiteSpace(detail),
          Actor = NormalizeActor(actor)
        },
        tx,
        cancellationToken: ct));

  private async Task<string?> GetWorkOrderStatusAsync(DbConnection conn, IDbTransaction tx, int workOrderId, CancellationToken ct)
    => await conn.ExecuteScalarAsync<string?>(
      new CommandDefinition(
        "SELECT Estado FROM dbo.OrdenTrabajo WHERE Id = @WorkOrderId;",
        new { WorkOrderId = workOrderId },
        tx,
        cancellationToken: ct));

  private async Task<bool> HasBeenSubmittedForReviewAsync(DbConnection conn, IDbTransaction tx, int workOrderId, CancellationToken ct)
    => await conn.ExecuteScalarAsync<bool>(
      new CommandDefinition(
        """
        SELECT CAST(CASE WHEN EXISTS (
            SELECT 1
            FROM dbo.OrdenTrabajoAuditoria
            WHERE OrdenTrabajoId = @WorkOrderId
              AND Evento = 'ENVIADA_REVISION'
        ) THEN 1 ELSE 0 END AS bit);
        """,
        new { WorkOrderId = workOrderId },
        tx,
        cancellationToken: ct));

  private async Task<bool> CanActorWorkAsync(DbConnection conn, IDbTransaction tx, int workOrderId, int? actorEmployeeId, CancellationToken ct)
  {
    if (!actorEmployeeId.HasValue)
    {
      return false;
    }

    return await conn.ExecuteScalarAsync<bool>(
      new CommandDefinition(
        """
        SELECT CAST(CASE WHEN EXISTS (
          SELECT 1
          FROM dbo.OrdenTrabajo ot
          WHERE ot.Id = @WorkOrderId
            AND ot.OwnerEmployeeId = @ActorEmployeeId

          UNION ALL

          SELECT 1
          FROM dbo.OrdenTrabajoParticipante p
          WHERE p.OrdenTrabajoId = @WorkOrderId
            AND p.EmployeeId = @ActorEmployeeId
        ) THEN 1 ELSE 0 END AS bit);
        """,
        new { WorkOrderId = workOrderId, ActorEmployeeId = actorEmployeeId.Value },
        tx,
        cancellationToken: ct));
  }

  private async Task<string?> ValidateReadyForReviewAsync(DbConnection conn, IDbTransaction tx, int workOrderId, CancellationToken ct)
  {
    var pendingSteps = await conn.ExecuteScalarAsync<int>(
      new CommandDefinition(
        """
        SELECT COUNT(*)
        FROM dbo.OrdenTrabajoPaso
        WHERE OrdenTrabajoId = @WorkOrderId
          AND Estado = 'PENDIENTE';
        """,
        new { WorkOrderId = workOrderId },
        tx,
        cancellationToken: ct));
    if (pendingSteps > 0)
    {
      return "Todos los pasos deben estar HECHO, INCIDENCIA o NO_APLICA antes de enviar.";
    }

    var missingRequiredPhotos = await conn.ExecuteScalarAsync<int>(
      new CommandDefinition(
        """
        SELECT COUNT(*)
        FROM dbo.OrdenTrabajoPaso p
        WHERE p.OrdenTrabajoId = @WorkOrderId
          AND p.PoliticaFoto = 'REQUERIDA'
          AND NOT EXISTS (
            SELECT 1
            FROM dbo.OrdenTrabajoEvidencia ev
            WHERE ev.PasoId = p.Id
              AND ev.Eliminada = 0
          );
        """,
        new { WorkOrderId = workOrderId },
        tx,
        cancellationToken: ct));
    if (missingRequiredPhotos > 0)
    {
      return "Hay pasos con fotografia requerida sin evidencia.";
    }

    var missingNotes = await conn.ExecuteScalarAsync<int>(
      new CommandDefinition(
        """
        SELECT COUNT(*)
        FROM dbo.OrdenTrabajoPaso
        WHERE OrdenTrabajoId = @WorkOrderId
          AND (
            (Estado = 'INCIDENCIA' AND RequiereNotasEnIncidencia = 1 AND NULLIF(LTRIM(RTRIM(ISNULL(Notas, ''))), '') IS NULL)
            OR
            (Estado = 'NO_APLICA' AND RequiereNotasEnNoAplica = 1 AND NULLIF(LTRIM(RTRIM(ISNULL(Notas, ''))), '') IS NULL)
          );
        """,
        new { WorkOrderId = workOrderId },
        tx,
        cancellationToken: ct));
    return missingNotes > 0
      ? "Los pasos con incidencia o no aplica requieren notas."
      : null;
  }

  private void AppendWorkOrderFilters(StringBuilder sql, DynamicParameters p, OrdenTrabajoSearchFilter filter)
  {
    if (!string.IsNullOrWhiteSpace(filter.Rfc))
    {
      sql.AppendLine(" AND ot.Rfc = @Rfc");
      p.Add("@Rfc", filter.Rfc.Trim());
    }

    if (!filter.IncludeClosed)
    {
      sql.AppendLine(" AND ot.Estado <> 'CERRADA' AND ot.Estado <> 'CANCELADA'");
    }

    if (!string.IsNullOrWhiteSpace(filter.Estado))
    {
      sql.AppendLine(" AND ot.Estado = @Estado");
      p.Add("@Estado", filter.Estado.Trim().ToUpperInvariant());
    }

    if (!string.IsNullOrWhiteSpace(filter.CategoriaCodigo))
    {
      sql.AppendLine(" AND c.Codigo = @CategoriaCodigo");
      p.Add("@CategoriaCodigo", filter.CategoriaCodigo.Trim().ToUpperInvariant());
    }

    if (filter.OwnerEmployeeId.HasValue)
    {
      sql.AppendLine(" AND ot.OwnerEmployeeId = @OwnerEmployeeId");
      p.Add("@OwnerEmployeeId", filter.OwnerEmployeeId.Value);
    }

    if (filter.ParticipantEmployeeId.HasValue)
    {
      sql.AppendLine();
      sql.Append(
        """
        AND (
          ot.OwnerEmployeeId = @ParticipantEmployeeId
          OR EXISTS (
            SELECT 1
            FROM dbo.OrdenTrabajoParticipante part
            WHERE part.OrdenTrabajoId = ot.Id
              AND part.EmployeeId = @ParticipantEmployeeId
          )
        )
        """);
      p.Add("@ParticipantEmployeeId", filter.ParticipantEmployeeId.Value);
    }

    var createdByActor = NullIfWhiteSpace(filter.CreatedByActor);
    if (createdByActor is not null)
    {
      sql.AppendLine(" AND LTRIM(RTRIM(ot.CreadaPor)) = @CreatedByActor");
      p.Add("@CreatedByActor", createdByActor);
    }

    if (filter.ScheduledFrom.HasValue)
    {
      sql.AppendLine(" AND ot.FechaProgramada >= @ScheduledFrom");
      p.Add("@ScheduledFrom", filter.ScheduledFrom.Value.Date);
    }

    if (filter.ScheduledTo.HasValue)
    {
      sql.AppendLine(" AND ot.FechaProgramada < @ScheduledToExclusive");
      p.Add("@ScheduledToExclusive", filter.ScheduledTo.Value.Date.AddDays(1));
    }

    var searchText = NullIfWhiteSpace(filter.SearchText);
    if (searchText is not null)
    {
      sql.AppendLine();
      sql.Append(
        """
        AND (
          ot.Folio LIKE @Search
          OR ot.Titulo LIKE @Search
          OR ot.Descripcion LIKE @Search
          OR ot.Ubicacion LIKE @Search
          OR room.ROOM_NAME LIKE @Search
        )
        """);
      p.Add("@Search", $"%{searchText}%");
    }
  }

  private static void AppendDashboardScope(StringBuilder where, DynamicParameters p, OrdenTrabajoDashboardFilter filter)
  {
    if (!string.IsNullOrWhiteSpace(filter.Rfc))
    {
      where.AppendLine();
      where.AppendLine("  AND ot.Rfc = @Rfc");
      p.Add("@Rfc", filter.Rfc.Trim());
    }

    if (filter.EmployeeId.HasValue)
    {
      where.AppendLine();
      where.Append(
        """
        AND (
          ot.OwnerEmployeeId = @EmployeeId
          OR EXISTS (
            SELECT 1
            FROM dbo.OrdenTrabajoParticipante part
            WHERE part.OrdenTrabajoId = ot.Id
              AND part.EmployeeId = @EmployeeId
          )
        )
        """);
      p.Add("@EmployeeId", filter.EmployeeId.Value);
    }
  }

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fabrica de conexiones no devolvio una DbConnection.");

  private static InvalidOperationException CreateMissingSchemaException(Exception ex)
    => new(MissingSchemaMessage, ex);

  private static bool IsMissingWorkOrderSchemaException(Exception ex)
  {
    if (ex is SqlException && IsMissingWorkOrderSchemaMessage(ex.Message))
    {
      return true;
    }

    return ex.InnerException is not null && IsMissingWorkOrderSchemaException(ex.InnerException);
  }

  private static bool IsMissingWorkOrderSchemaMessage(string message)
    => message.Contains("OrdenTrabajo", StringComparison.OrdinalIgnoreCase)
      && (message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase)
        || message.Contains("objeto", StringComparison.OrdinalIgnoreCase)
        || message.Contains("object", StringComparison.OrdinalIgnoreCase));

  private static OrdenTrabajoCalendarCellResult CellResult(int roomCalendarId, bool success, string message, int? workOrderId = null, string? folio = null)
    => new()
    {
      RoomCalendarId = roomCalendarId,
      Success = success,
      Message = message,
      WorkOrderId = workOrderId,
      Folio = folio
    };

  private static string NormalizeActor(string? actor)
    => NullIfWhiteSpace(actor) ?? "OrionERP";

  private static string NormalizeCode(string? value, string fallback)
    => NullIfWhiteSpace(value)?.ToUpperInvariant() ?? fallback;

  private static string NormalizePriority(string? value)
  {
    var normalized = NormalizeCode(value, OrdenTrabajoCodes.PrioridadNormal);
    return normalized is OrdenTrabajoCodes.PrioridadBaja or OrdenTrabajoCodes.PrioridadNormal or OrdenTrabajoCodes.PrioridadAlta or OrdenTrabajoCodes.PrioridadUrgente
      ? normalized
      : OrdenTrabajoCodes.PrioridadNormal;
  }

  private static string NormalizeStepStatus(string? value)
  {
    var normalized = NormalizeCode(value, OrdenTrabajoCodes.PasoHecho);
    return normalized is OrdenTrabajoCodes.PasoPendiente or OrdenTrabajoCodes.PasoHecho or OrdenTrabajoCodes.PasoIncidencia or OrdenTrabajoCodes.PasoNoAplica
      ? normalized
      : OrdenTrabajoCodes.PasoHecho;
  }

  private static string NormalizePhotoPolicy(string? value)
  {
    var normalized = NormalizeCode(value, OrdenTrabajoCodes.FotoOpcional);
    return normalized is OrdenTrabajoCodes.FotoNoPermitida or OrdenTrabajoCodes.FotoOpcional or OrdenTrabajoCodes.FotoRequerida
      ? normalized
      : OrdenTrabajoCodes.FotoOpcional;
  }

  private static string NormalizeImageContentType(string? contentType)
  {
    var normalized = NullIfWhiteSpace(contentType)?.ToLowerInvariant();
    return normalized is "image/png" or "image/webp" or "image/gif" or "image/bmp"
      ? normalized
      : "image/jpeg";
  }

  private static string NormalizeEvidenceContentType(string? contentType, string? fileName)
  {
    var normalized = NullIfWhiteSpace(contentType)?.ToLowerInvariant();
    var extensionContentType = ResolveContentTypeFromExtension(Path.GetExtension(fileName));
    if (string.Equals(normalized, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
      && !string.Equals(extensionContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
    {
      return extensionContentType;
    }

    if (IsSafeContentType(normalized))
    {
      return normalized!;
    }

    return extensionContentType;
  }

  private static bool IsSafeContentType(string? contentType)
  {
    if (contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
    {
      return true;
    }

    return contentType is "application/pdf"
      or "application/msword"
      or "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
      or "application/vnd.ms-excel"
      or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
      or "application/vnd.ms-powerpoint"
      or "application/vnd.openxmlformats-officedocument.presentationml.presentation"
      or "text/plain"
      or "text/csv"
      or "application/xml"
      or "text/xml"
      or "application/json"
      or "application/zip"
      or "application/octet-stream";
  }

  private static string ResolveContentTypeFromExtension(string? extension)
    => NullIfWhiteSpace(extension)?.TrimStart('.').ToLowerInvariant() switch
    {
      "pdf" => "application/pdf",
      "doc" => "application/msword",
      "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
      "xls" => "application/vnd.ms-excel",
      "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
      "ppt" => "application/vnd.ms-powerpoint",
      "pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
      "csv" => "text/csv",
      "txt" => "text/plain",
      "xml" => "application/xml",
      "json" => "application/json",
      "jpg" or "jpeg" => "image/jpeg",
      "png" => "image/png",
      "gif" => "image/gif",
      "webp" => "image/webp",
      "bmp" => "image/bmp",
      "zip" => "application/zip",
      _ => "application/octet-stream"
    };

  private static string BuildDefaultEvidenceFileName(int workOrderId, int stepId, string? contentType)
  {
    var extension = NullIfWhiteSpace(contentType)?.ToLowerInvariant() switch
    {
      "application/pdf" => "pdf",
      "application/msword" => "doc",
      "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => "docx",
      "application/vnd.ms-excel" => "xls",
      "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => "xlsx",
      "application/vnd.ms-powerpoint" => "ppt",
      "application/vnd.openxmlformats-officedocument.presentationml.presentation" => "pptx",
      "text/plain" => "txt",
      "text/csv" => "csv",
      "application/xml" or "text/xml" => "xml",
      "application/json" => "json",
      "image/png" => "png",
      "image/gif" => "gif",
      "image/webp" => "webp",
      "image/bmp" => "bmp",
      "application/zip" => "zip",
      _ => "bin"
    };

    return $"evidencia-{workOrderId}-{stepId}.{extension}";
  }

  private static string NormalizeCaptureSource(string? captureSource)
  {
    var normalized = NormalizeCode(captureSource, OrdenTrabajoCodes.EvidenciaUnknown);
    return normalized is OrdenTrabajoCodes.EvidenciaCamera or OrdenTrabajoCodes.EvidenciaFile or OrdenTrabajoCodes.EvidenciaUnknown
      ? normalized
      : OrdenTrabajoCodes.EvidenciaUnknown;
  }

  private static string RequireText(string? value, string message)
  {
    var trimmed = NullIfWhiteSpace(value);
    if (trimmed is null)
    {
      throw new ArgumentException(message);
    }

    return trimmed;
  }

  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private static string Truncate(string value, int maxLength)
    => value.Length <= maxLength ? value : value[..maxLength];

  private static string BuildStepTitle(int rowNumber, string? description)
  {
    var value = NullIfWhiteSpace(description) ?? $"Paso {rowNumber}";
    return Truncate(value, 80);
  }

  private static string InferLegacyPhotoPolicy(string? description)
  {
    var text = description ?? string.Empty;
    if (text.Contains("SIN FOTO", StringComparison.OrdinalIgnoreCase))
    {
      return OrdenTrabajoCodes.FotoNoPermitida;
    }

    return text.Contains("FOTO", StringComparison.OrdinalIgnoreCase)
      ? OrdenTrabajoCodes.FotoRequerida
      : OrdenTrabajoCodes.FotoNoPermitida;
  }

  private sealed class PublishedTemplateRow
  {
    public int TemplateId { get; set; }
    public int VersionId { get; set; }
    public string TemplateName { get; set; } = string.Empty;
  }

  private sealed class LegacyTemplateMatch
  {
    public int Id { get; set; }
    public string CategoriaCodigo { get; set; } = string.Empty;
  }

  private sealed class WorkOrderInsertArgs
  {
    public string Folio { get; set; } = string.Empty;
    public string Rfc { get; set; } = string.Empty;
    public int CategoryId { get; set; }
    public string Estado { get; set; } = string.Empty;
    public string Prioridad { get; set; } = string.Empty;
    public string Titulo { get; set; } = string.Empty;
    public string? Descripcion { get; set; }
    public int OwnerEmployeeId { get; set; }
    public DateTime FechaProgramada { get; set; }
    public TimeSpan? HoraInicioProgramada { get; set; }
    public TimeSpan? HoraFinProgramada { get; set; }
    public DateTime? FechaVencimiento { get; set; }
    public int? RoomId { get; set; }
    public int? RoomCalendarId { get; set; }
    public int? ReservationId { get; set; }
    public string? Ubicacion { get; set; }
    public int? PlantillaId { get; set; }
    public int? PlantillaVersionId { get; set; }
    public decimal EstimatedCost { get; set; }
    public string Actor { get; set; } = string.Empty;
  }

  private sealed class CalendarCellContext
  {
    public int RoomCalendarId { get; set; }
    public DateTime RoomDate { get; set; }
    public string RoomName { get; set; } = string.Empty;
    public int? RoomId { get; set; }
    public int? ReservationId { get; set; }
  }

  private sealed class StepRuleRow
  {
    public int Id { get; set; }
    public bool RequiereNotasEnIncidencia { get; set; }
    public bool RequiereNotasEnNoAplica { get; set; }
  }

  private sealed class LegacyTemplateHeader
  {
    public int ActividadId { get; set; }
    public string TemplateName { get; set; } = string.Empty;
    public string RoomName { get; set; } = string.Empty;
    public int StepCount { get; set; }
  }

  private sealed class LegacyTemplateStep
  {
    public int RowNumber { get; set; }
    public decimal Secuencia { get; set; }
    public string Descripcion { get; set; } = string.Empty;
    public int? ProcedimientoId { get; set; }
  }
}
