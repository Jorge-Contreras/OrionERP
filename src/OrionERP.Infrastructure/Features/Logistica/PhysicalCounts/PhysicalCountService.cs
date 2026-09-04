using System.Data;
using System.Data.Common;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.PhysicalCounts;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Infrastructure.Features.Logistica.Support;

namespace OrionERP.Infrastructure.Features.Logistica.PhysicalCounts;

public sealed class PhysicalCountService : IPhysicalCountService
{
  private readonly IDbConnectionFactory _connectionFactory;

  public PhysicalCountService(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  }

  public async Task<IReadOnlyList<PhysicalCountSessionSummaryDto>> GetSessionsAsync(CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          s.Id,
          s.SessionCode,
          s.[Status],
          s.ScopeType,
          s.LocationId,
          l.LocationName,
          room.ROOM_NAME AS RoomName,
          scope.MaterialCount,
          scope.PrimaryMaterialLabel,
          COUNT(DISTINCT line.LocationId) AS LocationCount,
          s.CreatedAt,
          s.CreatedBy,
          s.SubmittedAt,
          s.SubmittedBy,
          s.ApprovedAt,
          s.ApprovedBy,
          s.PostedAt,
          s.PostedBy,
          s.CanceledAt,
          s.CanceledBy,
          s.CancelReason,
          activePlan.RequestedAt AS RecountRequestedAt,
          activePlan.RequestedBy AS RecountRequestedBy,
          COUNT(line.Id) AS LineCount,
          SUM(
            CASE
              WHEN s.[Status] = 'Recount'
                THEN CASE WHEN activePlanLine.Id IS NOT NULL AND line.CountedQuantity IS NOT NULL THEN 1 ELSE 0 END
              WHEN line.CountedQuantity IS NOT NULL THEN 1
              ELSE 0
            END
          ) AS CountedLineCount,
          SUM(CASE WHEN line.VarianceQuantity IS NOT NULL AND line.VarianceQuantity <> 0 THEN 1 ELSE 0 END) AS VarianceLineCount,
          COUNT(activePlanLine.Id) AS RecountLineCount
      FROM logistica.PhysicalCountSession s
      LEFT JOIN logistica.Location l
        ON l.Id = s.LocationId
      LEFT JOIN dbo.ROOM room
        ON room.ID = l.RoomId
      OUTER APPLY
      (
          SELECT
              COUNT(*) AS MaterialCount,
              MIN(CONCAT(material.MaterialCode, ' · ', material.[Description])) AS PrimaryMaterialLabel
          FROM logistica.PhysicalCountSessionMaterial sessionMaterial
          JOIN logistica.Material material
            ON material.Rfc = sessionMaterial.Rfc
           AND material.Id = sessionMaterial.MaterialId
          WHERE sessionMaterial.SessionId = s.Id
      ) scope
      LEFT JOIN logistica.PhysicalCountLine line
        ON line.SessionId = s.Id
      LEFT JOIN logistica.PhysicalCountRecountPlan activePlan
        ON activePlan.SessionId = s.Id
       AND activePlan.CompletedAt IS NULL
      LEFT JOIN logistica.PhysicalCountRecountPlanLine activePlanLine
        ON activePlanLine.RecountPlanId = activePlan.Id
       AND activePlanLine.PhysicalCountLineId = line.Id
      GROUP BY
          s.Id,
          s.SessionCode,
          s.[Status],
          s.ScopeType,
          s.LocationId,
          l.LocationName,
          room.ROOM_NAME,
          scope.MaterialCount,
          scope.PrimaryMaterialLabel,
          s.CreatedAt,
          s.CreatedBy,
          s.SubmittedAt,
          s.SubmittedBy,
          s.ApprovedAt,
          s.ApprovedBy,
          s.PostedAt,
          s.PostedBy,
          s.CanceledAt,
          s.CanceledBy,
          s.CancelReason,
          activePlan.RequestedAt,
          activePlan.RequestedBy
      ORDER BY s.CreatedAt DESC, s.Id DESC;
      """;

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<PhysicalCountSessionSummaryDto>(
      new CommandDefinition(sql, cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<PhysicalCountSessionDetailDto?> GetSessionAsync(int sessionId, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          s.Id,
          s.SessionCode,
          s.[Status],
          s.ScopeType,
          s.LocationId,
          l.LocationName,
          room.ROOM_NAME AS RoomName,
          s.MaxLocationsPerMaterial,
          scope.MaterialCount,
          scope.PrimaryMaterialLabel,
          (
            SELECT COUNT(DISTINCT scopeLine.LocationId)
            FROM logistica.PhysicalCountLine scopeLine
            WHERE scopeLine.SessionId = s.Id
          ) AS LocationCount,
          s.Notes,
          s.CreatedAt,
          s.CreatedBy,
          s.SubmittedAt,
          s.SubmittedBy,
          s.ApprovedAt,
          s.ApprovedBy,
          s.PostedAt,
          s.PostedBy,
          s.CanceledAt,
          s.CanceledBy,
          s.CancelReason,
          activePlan.Id AS ActiveRecountPlanId,
          activePlan.RequestedAt AS RecountRequestedAt,
          activePlan.RequestedBy AS RecountRequestedBy
      FROM logistica.PhysicalCountSession s
      LEFT JOIN logistica.Location l
        ON l.Id = s.LocationId
      LEFT JOIN dbo.ROOM room
        ON room.ID = l.RoomId
      OUTER APPLY
      (
          SELECT
              COUNT(*) AS MaterialCount,
              MIN(CONCAT(material.MaterialCode, ' · ', material.[Description])) AS PrimaryMaterialLabel
          FROM logistica.PhysicalCountSessionMaterial sessionMaterial
          JOIN logistica.Material material
            ON material.Rfc = sessionMaterial.Rfc
           AND material.Id = sessionMaterial.MaterialId
          WHERE sessionMaterial.SessionId = s.Id
      ) scope
      LEFT JOIN logistica.PhysicalCountRecountPlan activePlan
        ON activePlan.SessionId = s.Id
       AND activePlan.CompletedAt IS NULL
      WHERE s.Id = @SessionId;

      SELECT
          line.Id,
          line.StockBalanceId,
          line.MaterialId,
          m.MaterialCode,
          m.[Description] AS MaterialDescription,
          m.Barcode,
          m.MaterialClass,
          u.UnitName AS BaseUnitName,
          line.LocationId,
          loc.LocationCode,
          loc.LocationName,
          lineRoom.ROOM_NAME AS RoomName,
          line.CountSequence,
          CAST(line.ExpectedQuantity AS decimal(18,4)) AS ExpectedQuantity,
          CAST(line.CountedQuantity AS decimal(18,4)) AS CountedQuantity,
          CAST(line.VarianceQuantity AS decimal(18,4)) AS VarianceQuantity,
          line.Notes,
          line.IsMissing,
          line.IsDamaged,
          line.CapturedAt,
          line.CapturedBy,
          activePlanLine.IssueCode AS RecountIssueCode,
          activePlanLine.Reason AS RecountReason,
          activePlan.RequestedAt AS RecountRequestedAt,
          activePlan.RequestedBy AS RecountRequestedBy,
          (
            SELECT COUNT(*)
            FROM logistica.PhysicalCountAttachment attachment
            WHERE attachment.PhysicalCountLineId = line.Id
          ) AS AttachmentCount
      FROM logistica.PhysicalCountLine line
      JOIN logistica.Material m
        ON m.Id = line.MaterialId
      JOIN logistica.Location loc
        ON loc.Id = line.LocationId
      LEFT JOIN dbo.ROOM lineRoom
        ON lineRoom.ID = loc.RoomId
      LEFT JOIN logistica.UnitOfMeasure u
        ON u.Id = m.BaseUnitId
      LEFT JOIN logistica.PhysicalCountRecountPlan activePlan
        ON activePlan.SessionId = line.SessionId
       AND activePlan.CompletedAt IS NULL
      LEFT JOIN logistica.PhysicalCountRecountPlanLine activePlanLine
        ON activePlanLine.RecountPlanId = activePlan.Id
       AND activePlanLine.PhysicalCountLineId = line.Id
      WHERE line.SessionId = @SessionId
      ORDER BY line.CountSequence, loc.LocationCode, m.[Description], m.MaterialCode, line.Id;

      SELECT
          attachment.Id,
          attachment.PhysicalCountLineId,
          attachment.FileName,
          attachment.FileExtension,
          attachment.[Description],
          DATALENGTH(attachment.Attachment) AS [Length],
          attachment.CreatedAt,
          attachment.CreatedBy
      FROM logistica.PhysicalCountAttachment attachment
      JOIN logistica.PhysicalCountLine line
        ON line.Id = attachment.PhysicalCountLineId
      WHERE line.SessionId = @SessionId
      ORDER BY attachment.CreatedAt DESC, attachment.Id DESC;

      SELECT
          audit.EventType,
          audit.OccurredAt,
          audit.PerformedBy,
          audit.MaterialId,
          audit.MaterialCode,
          audit.MaterialDescription,
          audit.LocationName,
          audit.ExpectedQuantity,
          audit.CountedQuantity,
          audit.Details
      FROM
      (
          SELECT
              CAST('SessionStarted' AS varchar(40)) AS EventType,
              sessionInfo.CreatedAt AS OccurredAt,
              sessionInfo.CreatedBy AS PerformedBy,
              CAST(NULL AS int) AS MaterialId,
              CAST(NULL AS varchar(100)) AS MaterialCode,
              CAST(NULL AS varchar(500)) AS MaterialDescription,
              CAST(NULL AS varchar(200)) AS LocationName,
              CAST(NULL AS decimal(18,4)) AS ExpectedQuantity,
              CAST(NULL AS decimal(18,4)) AS CountedQuantity,
              CAST(sessionInfo.Notes AS varchar(1000)) AS Details,
              10 AS EventSort
          FROM logistica.PhysicalCountSession sessionInfo
          WHERE sessionInfo.Id = @SessionId

          UNION ALL

          SELECT
              'LineCounted',
              countLine.CapturedAt,
              countLine.CapturedBy,
              countLine.MaterialId,
              material.MaterialCode,
              material.[Description],
              auditLocation.LocationName,
              CAST(countLine.ExpectedQuantity AS decimal(18,4)),
              CAST(countLine.CountedQuantity AS decimal(18,4)),
              CAST(countLine.Notes AS varchar(1000)),
              20
          FROM logistica.PhysicalCountLine countLine
          JOIN logistica.Material material
            ON material.Rfc = countLine.Rfc
           AND material.Id = countLine.MaterialId
          LEFT JOIN logistica.Location auditLocation
            ON auditLocation.Rfc = countLine.Rfc
           AND auditLocation.Id = countLine.LocationId
          WHERE countLine.SessionId = @SessionId
            AND countLine.CapturedAt IS NOT NULL

          UNION ALL

          SELECT
              'LineCounted',
              recountLine.PreviousCapturedAt,
              recountLine.PreviousCapturedBy,
              countLine.MaterialId,
              material.MaterialCode,
              material.[Description],
              auditLocation.LocationName,
              CAST(countLine.ExpectedQuantity AS decimal(18,4)),
              CAST(recountLine.PreviousCountedQuantity AS decimal(18,4)),
              CAST(recountLine.PreviousNotes AS varchar(1000)),
              20
          FROM logistica.PhysicalCountRecountPlanLine recountLine
          JOIN logistica.PhysicalCountRecountPlan recountPlan
            ON recountPlan.Rfc = recountLine.Rfc
           AND recountPlan.Id = recountLine.RecountPlanId
          JOIN logistica.PhysicalCountLine countLine
            ON countLine.Rfc = recountLine.Rfc
           AND countLine.Id = recountLine.PhysicalCountLineId
          JOIN logistica.Material material
            ON material.Rfc = countLine.Rfc
           AND material.Id = countLine.MaterialId
          LEFT JOIN logistica.Location auditLocation
            ON auditLocation.Rfc = countLine.Rfc
           AND auditLocation.Id = countLine.LocationId
          WHERE recountPlan.SessionId = @SessionId
            AND recountLine.PreviousCapturedAt IS NOT NULL

          UNION ALL

          SELECT
              'EvidenceAdded',
              attachment.CreatedAt,
              attachment.CreatedBy,
              countLine.MaterialId,
              material.MaterialCode,
              material.[Description],
              auditLocation.LocationName,
              CAST(NULL AS decimal(18,4)),
              CAST(NULL AS decimal(18,4)),
              CAST(attachment.FileName AS varchar(1000)),
              30
          FROM logistica.PhysicalCountAttachment attachment
          JOIN logistica.PhysicalCountLine countLine
            ON countLine.Rfc = attachment.Rfc
           AND countLine.Id = attachment.PhysicalCountLineId
          JOIN logistica.Material material
            ON material.Rfc = countLine.Rfc
           AND material.Id = countLine.MaterialId
          LEFT JOIN logistica.Location auditLocation
            ON auditLocation.Rfc = countLine.Rfc
           AND auditLocation.Id = countLine.LocationId
          WHERE countLine.SessionId = @SessionId

          UNION ALL

          SELECT
              'Submitted',
              sessionInfo.SubmittedAt,
              sessionInfo.SubmittedBy,
              NULL,
              NULL,
              NULL,
              NULL,
              NULL,
              NULL,
              NULL,
              40
          FROM logistica.PhysicalCountSession sessionInfo
          WHERE sessionInfo.Id = @SessionId
            AND sessionInfo.SubmittedAt IS NOT NULL

          UNION ALL

          SELECT
              'RecountRequested',
              recountPlan.RequestedAt,
              recountPlan.RequestedBy,
              NULL,
              NULL,
              NULL,
              NULL,
              NULL,
              NULL,
              CAST(CONCAT(
                (SELECT COUNT(*)
                 FROM logistica.PhysicalCountRecountPlanLine recountItem
                 WHERE recountItem.Rfc = recountPlan.Rfc
                   AND recountItem.RecountPlanId = recountPlan.Id),
                ' material(es) enviados a reconteo.') AS varchar(1000)),
              50
          FROM logistica.PhysicalCountRecountPlan recountPlan
          WHERE recountPlan.SessionId = @SessionId

          UNION ALL

          SELECT
              'RecountCompleted',
              recountPlan.CompletedAt,
              recountPlan.CompletedBy,
              NULL,
              NULL,
              NULL,
              NULL,
              NULL,
              NULL,
              NULL,
              60
          FROM logistica.PhysicalCountRecountPlan recountPlan
          WHERE recountPlan.SessionId = @SessionId
            AND recountPlan.CompletedAt IS NOT NULL

          UNION ALL

          SELECT
              'Approved',
              sessionInfo.ApprovedAt,
              sessionInfo.ApprovedBy,
              NULL,
              NULL,
              NULL,
              NULL,
              NULL,
              NULL,
              NULL,
              70
          FROM logistica.PhysicalCountSession sessionInfo
          WHERE sessionInfo.Id = @SessionId
            AND sessionInfo.ApprovedAt IS NOT NULL

          UNION ALL

          SELECT
              'Posted',
              sessionInfo.PostedAt,
              sessionInfo.PostedBy,
              NULL,
              NULL,
              NULL,
              NULL,
              NULL,
              NULL,
              NULL,
              80
          FROM logistica.PhysicalCountSession sessionInfo
          WHERE sessionInfo.Id = @SessionId
            AND sessionInfo.PostedAt IS NOT NULL

          UNION ALL

          SELECT
              'Canceled',
              sessionInfo.CanceledAt,
              sessionInfo.CanceledBy,
              NULL,
              NULL,
              NULL,
              NULL,
              NULL,
              NULL,
              CAST(sessionInfo.CancelReason AS varchar(1000)),
              90
          FROM logistica.PhysicalCountSession sessionInfo
          WHERE sessionInfo.Id = @SessionId
            AND sessionInfo.CanceledAt IS NOT NULL
      ) audit
      ORDER BY audit.OccurredAt DESC, audit.EventSort DESC;

      SELECT
          sessionMaterial.MaterialId,
          material.MaterialCode,
          material.[Description] AS MaterialDescription,
          COUNT(countLine.Id) AS LineCount,
          COUNT(DISTINCT countLine.LocationId) AS LocationCount
      FROM logistica.PhysicalCountSessionMaterial sessionMaterial
      JOIN logistica.Material material
        ON material.Rfc = sessionMaterial.Rfc
       AND material.Id = sessionMaterial.MaterialId
      LEFT JOIN logistica.PhysicalCountLine countLine
        ON countLine.Rfc = sessionMaterial.Rfc
       AND countLine.SessionId = sessionMaterial.SessionId
       AND countLine.MaterialId = sessionMaterial.MaterialId
      WHERE sessionMaterial.SessionId = @SessionId
      GROUP BY
          sessionMaterial.MaterialId,
          material.MaterialCode,
          material.[Description]
      ORDER BY material.[Description], material.MaterialCode;
      """;

    using var conn = CreateConnection();
    using var multi = await conn.QueryMultipleAsync(
      new CommandDefinition(sql, new { SessionId = sessionId }, cancellationToken: ct));

    var session = await multi.ReadFirstOrDefaultAsync<PhysicalCountSessionDetailDto>();
    if (session is null)
    {
      return null;
    }

    var lines = (await multi.ReadAsync<PhysicalCountLineDto>()).AsList();
    var attachments = (await multi.ReadAsync<PhysicalCountAttachmentDto>()).AsList();
    var auditEvents = (await multi.ReadAsync<PhysicalCountAuditEventDto>()).AsList();
    var scopeMaterials = (await multi.ReadAsync<PhysicalCountMaterialScopeDto>()).AsList();
    var attachmentsByLine = attachments
      .GroupBy(attachment => attachment.PhysicalCountLineId)
      .ToDictionary(group => group.Key, group => (IReadOnlyList<PhysicalCountAttachmentDto>)group.ToList());

    foreach (var line in lines)
    {
      line.Attachments = attachmentsByLine.TryGetValue(line.Id, out var lineAttachments)
        ? lineAttachments
        : Array.Empty<PhysicalCountAttachmentDto>();
    }

    session.Lines = lines;
    session.AuditEvents = auditEvents;
    session.Materials = scopeMaterials;
    return session;
  }

  /// <summary>
  /// Alcance de un conteo, en un solo lugar. <c>LocationScope</c> resuelve el subárbol de ubicaciones
  /// (o todas, cuando no se restringe); <c>ScopeCandidate</c> aplica el filtro de materiales y el de
  /// saldos en cero; <c>ScopeLine</c> aplica el tope de ubicaciones por material, priorizando lo que
  /// nunca se ha contado y lo más antiguo. Lo comparten la creación, la vista previa y la guardia de
  /// solapamiento para que las tres vean exactamente los mismos renglones.
  /// </summary>
  private const string ScopeCteSql =
    """
    WITH LocationScope AS (
        SELECT rootLocation.Id
        FROM logistica.Location rootLocation
        WHERE (@LocationId IS NOT NULL AND rootLocation.Id = @LocationId)
           OR (@LocationId IS NULL AND rootLocation.IsActive = 1 AND rootLocation.IsInventoryEnabled = 1)

        UNION ALL

        SELECT child.Id
        FROM logistica.Location child
        JOIN LocationScope parent
          ON parent.Id = child.ParentLocationId
        WHERE @LocationId IS NOT NULL
    ),
    ScopeCandidate AS (
        SELECT
            sb.Id AS StockBalanceId,
            sb.LocationId,
            sb.MaterialId,
            sb.Quantity,
            sb.LastCountedAt,
            ROW_NUMBER() OVER (
                PARTITION BY sb.MaterialId
                ORDER BY
                    CASE WHEN sb.LastCountedAt IS NULL THEN 0 ELSE 1 END,
                    sb.LastCountedAt,
                    sb.Quantity DESC,
                    sb.Id
            ) AS MaterialRank
        FROM logistica.StockBalance sb
        JOIN LocationScope scope
          ON scope.Id = sb.LocationId
        -- Sin filtro de cantidad: una ubicacion en cero es justo la que hay que ir a comprobar,
        -- y es lo que el generador por ubicacion ha hecho desde siempre.
        WHERE ISNULL(sb.IsRemoved, 0) = 0
          AND (@HasMaterialFilter = 0 OR sb.MaterialId IN @MaterialIds)
    ),
    ScopeLine AS (
        SELECT
            candidate.StockBalanceId,
            candidate.LocationId,
            candidate.MaterialId,
            candidate.Quantity,
            candidate.LastCountedAt
        FROM ScopeCandidate candidate
        WHERE @MaxLocationsPerMaterial IS NULL
           OR candidate.MaterialRank <= @MaxLocationsPerMaterial
    )
    """;

  /// <summary>
  /// Sesiones todavía abiertas que ya reclamaron alguno de esos saldos. Dos sesiones sobre el mismo
  /// saldo significan que la segunda en aplicarse pisa a la primera.
  /// </summary>
  private const string ConflictSelectSql =
    """
    SELECT TOP (50)
        openSession.Id AS SessionId,
        openSession.SessionCode,
        openSession.[Status],
        material.MaterialCode,
        material.[Description] AS MaterialDescription,
        loc.LocationName,
        COUNT(*) AS OverlappingLineCount
    FROM ScopeLine line
    JOIN logistica.PhysicalCountLine openLine
      ON openLine.StockBalanceId = line.StockBalanceId
    JOIN logistica.PhysicalCountSession openSession
      ON openSession.Id = openLine.SessionId
    JOIN logistica.Material material
      ON material.Id = line.MaterialId
    JOIN logistica.Location loc
      ON loc.Id = line.LocationId
    WHERE openSession.[Status] NOT IN ('Posted', 'Canceled')
    GROUP BY
        openSession.Id,
        openSession.SessionCode,
        openSession.[Status],
        material.MaterialCode,
        material.[Description],
        loc.LocationName
    ORDER BY openSession.SessionCode, loc.LocationName, material.MaterialCode;
    """;

  public async Task<LogisticsCommandResult> CreateSessionAsync(PhysicalCountSessionCreateRequest request, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    var scopeType = PhysicalCountSessionScopeTypes.Normalize(request.ScopeType);
    var isMaterialScope = PhysicalCountSessionScopeTypes.IsMaterialScope(scopeType);
    var materialIds = NormalizeMaterialIds(request.MaterialIds, isMaterialScope);

    if (isMaterialScope && materialIds.Length == 0)
    {
      return LogisticsCommandResult.Fail("Selecciona al menos un material para el conteo.");
    }

    if (!isMaterialScope && (request.LocationId is null || request.LocationId <= 0))
    {
      return LogisticsCommandResult.Fail("Selecciona la ubicación que se va a contar.");
    }

    var scopeParameters = BuildScopeParameters(request, materialIds);

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      if (request.LocationId is > 0)
      {
        var locationExists = await conn.ExecuteScalarAsync<bool>(
          new CommandDefinition(
            """
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1
                FROM logistica.Location
                WHERE Id = @LocationId
                  AND IsActive = 1
                  AND IsInventoryEnabled = 1
            ) THEN 1 ELSE 0 END AS bit);
            """,
            new { request.LocationId },
            tx,
            cancellationToken: ct));

        if (!locationExists)
        {
          await tx.RollbackAsync(ct);
          return LogisticsCommandResult.Fail("La ubicación seleccionada no existe o no está habilitada para inventario.");
        }
      }

      if (materialIds.Length > 0)
      {
        var knownMaterialCount = await conn.ExecuteScalarAsync<int>(
          new CommandDefinition(
            """
            SELECT COUNT(*)
            FROM logistica.Material
            WHERE Id IN @MaterialIds
              AND IsActive = 1;
            """,
            new { MaterialIds = materialIds },
            tx,
            cancellationToken: ct));

        if (knownMaterialCount != materialIds.Length)
        {
          await tx.RollbackAsync(ct);
          return LogisticsCommandResult.Fail("Alguno de los materiales seleccionados ya no existe o está inactivo.");
        }
      }

      var conflictingSessionCodes = (await conn.QueryAsync<string>(
        new CommandDefinition(
          ScopeCteSql +
          """

          SELECT DISTINCT openSession.SessionCode
          FROM ScopeLine line
          JOIN logistica.PhysicalCountLine openLine WITH (UPDLOCK, HOLDLOCK)
            ON openLine.StockBalanceId = line.StockBalanceId
          JOIN logistica.PhysicalCountSession openSession
            ON openSession.Id = openLine.SessionId
          WHERE openSession.[Status] NOT IN ('Posted', 'Canceled')
          ORDER BY openSession.SessionCode;
          """,
          scopeParameters,
          tx,
          cancellationToken: ct))).AsList();

      if (conflictingSessionCodes.Count > 0)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail(BuildOverlapMessage(conflictingSessionCodes));
      }

      var sessionId = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(
          """
          INSERT INTO logistica.PhysicalCountSession
          (
              SessionCode,
              ScopeType,
              LocationId,
              MaxLocationsPerMaterial,
              [Status],
              Notes,
              CreatedAt,
              CreatedBy
          )
          VALUES
          (
              CONCAT('TMP-', LEFT(REPLACE(CONVERT(varchar(36), NEWID()), '-', ''), 20)),
              @ScopeType,
              @LocationId,
              @MaxLocationsPerMaterial,
              'Draft',
              @Notes,
              SYSUTCDATETIME(),
              @CreatedBy
          );

          SELECT CAST(SCOPE_IDENTITY() AS int);
          """,
          new
          {
            ScopeType = scopeType,
            LocationId = NormalizeLocationId(request.LocationId),
            request.MaxLocationsPerMaterial,
            Notes = NullIfWhiteSpace(request.Notes),
            CreatedBy = NullIfWhiteSpace(request.CreatedBy) ?? "OrionERP"
          },
          tx,
          cancellationToken: ct));

      await conn.ExecuteAsync(
        new CommandDefinition(
          """
          UPDATE logistica.PhysicalCountSession
          SET SessionCode = CONCAT('PC-', RIGHT(REPLICATE('0', 6) + CAST(@SessionId AS varchar(20)), 6))
          WHERE Id = @SessionId;
          """,
          new { SessionId = sessionId },
          tx,
          cancellationToken: ct));

      if (materialIds.Length > 0)
      {
        await conn.ExecuteAsync(
          new CommandDefinition(
            """
            INSERT INTO logistica.PhysicalCountSessionMaterial (SessionId, MaterialId)
            SELECT @SessionId, material.Id
            FROM logistica.Material material
            WHERE material.Id IN @MaterialIds;
            """,
            new { SessionId = sessionId, MaterialIds = materialIds },
            tx,
            cancellationToken: ct));
      }

      await conn.ExecuteAsync(
        new CommandDefinition(
          ScopeCteSql +
          """

          INSERT INTO logistica.PhysicalCountLine
          (
              SessionId,
              StockBalanceId,
              LocationId,
              MaterialId,
              ExpectedQuantity,
              CountSequence
          )
          SELECT
              @SessionId,
              line.StockBalanceId,
              line.LocationId,
              line.MaterialId,
              line.Quantity,
              ROW_NUMBER() OVER (
                  ORDER BY room.ROOM_NAME, loc.LocationCode, material.[Description], material.MaterialCode, line.StockBalanceId
              )
          FROM ScopeLine line
          JOIN logistica.Location loc
            ON loc.Id = line.LocationId
          LEFT JOIN dbo.ROOM room
            ON room.ID = loc.RoomId
          JOIN logistica.Material material
            ON material.Id = line.MaterialId;
          """,
          BuildScopeParameters(request, materialIds, sessionId),
          tx,
          cancellationToken: ct));

      var lineCount = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(
          "SELECT COUNT(*) FROM logistica.PhysicalCountLine WHERE SessionId = @SessionId;",
          new { SessionId = sessionId },
          tx,
          cancellationToken: ct));

      if (lineCount == 0)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail(BuildEmptyScopeMessage(isMaterialScope, request.LocationId));
      }

      await tx.CommitAsync(ct);
      return LogisticsCommandResult.Ok("Sesión de conteo creada correctamente.", sessionId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<PhysicalCountScopePreviewDto> PreviewScopeAsync(PhysicalCountScopePreviewRequest request, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    var scopeType = PhysicalCountSessionScopeTypes.Normalize(request.ScopeType);
    var isMaterialScope = PhysicalCountSessionScopeTypes.IsMaterialScope(scopeType);
    var materialIds = NormalizeMaterialIds(request.MaterialIds, isMaterialScope);

    if (isMaterialScope && materialIds.Length == 0)
    {
      return new PhysicalCountScopePreviewDto();
    }

    if (!isMaterialScope && (request.LocationId is null || request.LocationId <= 0))
    {
      return new PhysicalCountScopePreviewDto();
    }

    var sql =
      ScopeCteSql +
      """

      SELECT
          COUNT(*) AS LineCount,
          COUNT(DISTINCT line.LocationId) AS LocationCount,
          COUNT(DISTINCT line.MaterialId) AS MaterialCount
      FROM ScopeLine line;
      """ +
      ScopeCteSql +
      """

      SELECT
          line.MaterialId,
          material.MaterialCode,
          material.[Description] AS MaterialDescription,
          COUNT(DISTINCT line.LocationId) AS LocationCount,
          CAST(SUM(line.Quantity) AS decimal(18,4)) AS TotalQuantity,
          MAX(line.LastCountedAt) AS LastCountedAt
      FROM ScopeLine line
      JOIN logistica.Material material
        ON material.Id = line.MaterialId
      GROUP BY line.MaterialId, material.MaterialCode, material.[Description]
      ORDER BY material.[Description], material.MaterialCode;
      """ +
      ScopeCteSql +
      "\n\n" + ConflictSelectSql;

    using var conn = CreateConnection();
    using var multi = await conn.QueryMultipleAsync(
      new CommandDefinition(sql, BuildScopeParameters(request, materialIds), cancellationToken: ct));

    var totals = await multi.ReadFirstOrDefaultAsync<ScopeTotalsRow>();
    var materials = (await multi.ReadAsync<PhysicalCountScopeMaterialPreviewDto>()).AsList();
    var conflicts = (await multi.ReadAsync<PhysicalCountScopeConflictDto>()).AsList();

    return new PhysicalCountScopePreviewDto
    {
      LineCount = totals?.LineCount ?? 0,
      LocationCount = totals?.LocationCount ?? 0,
      MaterialCount = totals?.MaterialCount ?? 0,
      Materials = materials,
      Conflicts = conflicts
    };
  }

  private static int[] NormalizeMaterialIds(IReadOnlyList<int>? materialIds, bool isMaterialScope)
    => isMaterialScope && materialIds is not null
      ? materialIds.Where(materialId => materialId > 0).Distinct().OrderBy(materialId => materialId).ToArray()
      : Array.Empty<int>();

  private static int? NormalizeLocationId(int? locationId)
    => locationId is > 0 ? locationId : null;

  private static object BuildScopeParameters(
    PhysicalCountScopeRequest request,
    int[] materialIds,
    int sessionId = 0)
    => new
    {
      SessionId = sessionId,
      LocationId = NormalizeLocationId(request.LocationId),
      MaterialIds = materialIds.Length > 0 ? materialIds : new[] { 0 },
      HasMaterialFilter = materialIds.Length > 0,
      request.MaxLocationsPerMaterial
    };

  private static string BuildOverlapMessage(IReadOnlyList<string> sessionCodes)
  {
    var codes = string.Join(", ", sessionCodes);
    return sessionCodes.Count == 1
      ? $"El conteo {codes} ya está abierto sobre esos materiales y ubicaciones. Termínalo o cancélalo antes de crear otro."
      : $"Los conteos {codes} ya están abiertos sobre esos materiales y ubicaciones. Termínalos o cancélalos antes de crear otro.";
  }

  private static string BuildEmptyScopeMessage(bool isMaterialScope, int? locationId)
  {
    if (!isMaterialScope)
    {
      return "La ubicación no tiene existencias para generar un conteo físico.";
    }

    return locationId is > 0
      ? "Los materiales seleccionados no tienen existencias registradas en esa ubicación."
      : "Los materiales seleccionados no tienen existencias registradas en ninguna ubicación.";
  }

  private sealed class ScopeTotalsRow
  {
    public int LineCount { get; set; }
    public int LocationCount { get; set; }
    public int MaterialCount { get; set; }
  }

  public async Task<LogisticsCommandResult> CaptureLineAsync(PhysicalCountLineCaptureRequest request, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      var status = await conn.ExecuteScalarAsync<string?>(
        new CommandDefinition(
          "SELECT [Status] FROM logistica.PhysicalCountSession WHERE Id = @SessionId;",
          new { request.SessionId },
          tx,
          cancellationToken: ct));

      if (!IsDraftStatus(status) && !IsRecountStatus(status))
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Solo las sesiones en borrador o reconteo permiten capturar conteos.");
      }

      var currentLine = await conn.QueryFirstOrDefaultAsync<LineCaptureStateRow>(
        new CommandDefinition(
          """
          SELECT Id, CapturedAt
          FROM logistica.PhysicalCountLine WITH (UPDLOCK, HOLDLOCK)
          WHERE Id = @LineId
            AND SessionId = @SessionId;
          """,
          new { request.LineId, request.SessionId },
          tx,
          cancellationToken: ct));

      if (currentLine is null)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("La línea no pertenece a la sesión de conteo seleccionada.");
      }

      if (currentLine.CapturedAt != request.ExpectedCapturedAt)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Otro empleado actualizó este material. Se recargó el conteo para proteger su captura.");
      }

      var affectedLine = await conn.ExecuteAsync(
        new CommandDefinition(
          """
          UPDATE logistica.PhysicalCountLine
          SET CountedQuantity = @CountedQuantity,
              VarianceQuantity = @CountedQuantity - ExpectedQuantity,
              Notes = @Notes,
              IsMissing = @IsMissing,
              IsDamaged = @IsDamaged,
              CapturedAt = SYSUTCDATETIME(),
              CapturedBy = @CapturedBy
          WHERE Id = @LineId
            AND SessionId = @SessionId;
          """,
          new
          {
            request.CountedQuantity,
            Notes = NullIfWhiteSpace(request.Notes),
            request.IsMissing,
            request.IsDamaged,
            CapturedBy = NullIfWhiteSpace(request.CapturedBy) ?? "OrionERP",
            request.LineId,
            request.SessionId
          },
          tx,
          cancellationToken: ct));

      if (affectedLine != 1)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("La línea no pertenece a la sesión de conteo seleccionada.");
      }

      if (request.AttachmentBytes is { Length: > 0 })
      {
        await conn.ExecuteAsync(
          new CommandDefinition(
            """
            INSERT INTO logistica.PhysicalCountAttachment
            (
                PhysicalCountLineId,
                FileName,
                FileExtension,
                ContentType,
                [Description],
                Attachment,
                CreatedAt,
                CreatedBy
            )
            VALUES
            (
                @PhysicalCountLineId,
                @FileName,
                @FileExtension,
                @ContentType,
                @Description,
                @Attachment,
                SYSUTCDATETIME(),
                @CreatedBy
            );
            """,
            new
            {
              PhysicalCountLineId = request.LineId,
              FileName = string.IsNullOrWhiteSpace(request.AttachmentFileName)
                ? $"conteo-{request.SessionId}-{request.LineId}"
                : request.AttachmentFileName.Trim(),
              FileExtension = string.IsNullOrWhiteSpace(request.AttachmentExtension)
                ? "bin"
                : request.AttachmentExtension.Trim().TrimStart('.'),
              ContentType = LogisticsContentTypes.Normalize(
                request.AttachmentContentType,
                request.AttachmentFileName,
                request.AttachmentBytes),
              Description = NullIfWhiteSpace(request.AttachmentDescription),
              Attachment = request.AttachmentBytes,
              CreatedBy = NullIfWhiteSpace(request.CapturedBy) ?? "OrionERP"
            },
            tx,
            cancellationToken: ct));
      }

      await tx.CommitAsync(ct);
      return LogisticsCommandResult.Ok("Línea de conteo actualizada correctamente.", request.LineId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<LogisticsCommandResult> DeleteDraftSessionAsync(int sessionId, CancellationToken ct = default)
  {
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      var status = await conn.ExecuteScalarAsync<string?>(
        new CommandDefinition(
          "SELECT [Status] FROM logistica.PhysicalCountSession WHERE Id = @SessionId;",
          new { SessionId = sessionId },
          tx,
          cancellationToken: ct));

      if (status is null)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("La sesión de conteo no existe.");
      }

      if (!string.Equals(status, "Draft", StringComparison.OrdinalIgnoreCase))
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Solo las sesiones en borrador se pueden cancelar o eliminar.");
      }

      await conn.ExecuteAsync(
        new CommandDefinition(
          """
          DELETE attachment
          FROM logistica.PhysicalCountAttachment attachment
          JOIN logistica.PhysicalCountLine line
            ON line.Id = attachment.PhysicalCountLineId
          WHERE line.SessionId = @SessionId;
          """,
          new { SessionId = sessionId },
          tx,
          cancellationToken: ct));

      // Los conteos ya no se capturan por lote; el DELETE solo purga renglones heredados.
      await conn.ExecuteAsync(
        new CommandDefinition(
          """
          DELETE lotLine
          FROM logistica.PhysicalCountLotLine lotLine
          JOIN logistica.PhysicalCountLine countLine ON countLine.Rfc=lotLine.Rfc AND countLine.Id=lotLine.PhysicalCountLineId
          WHERE countLine.SessionId=@SessionId;
          DELETE FROM logistica.PhysicalCountLine WHERE SessionId = @SessionId;
          """,
          new { SessionId = sessionId },
          tx,
          cancellationToken: ct));

      // Los materiales del alcance apuntan a la sesión, así que se purgan antes que ella.
      await conn.ExecuteAsync(
        new CommandDefinition(
          "DELETE FROM logistica.PhysicalCountSessionMaterial WHERE SessionId = @SessionId;",
          new { SessionId = sessionId },
          tx,
          cancellationToken: ct));

      var affected = await conn.ExecuteAsync(
        new CommandDefinition(
          "DELETE FROM logistica.PhysicalCountSession WHERE Id = @SessionId;",
          new { SessionId = sessionId },
          tx,
          cancellationToken: ct));

      if (affected == 0)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("La sesión de conteo no existe.");
      }

      await tx.CommitAsync(ct);
      return LogisticsCommandResult.Ok("Sesión en borrador eliminada correctamente.", sessionId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<LogisticsCommandResult> SubmitSessionAsync(int sessionId, string submittedBy, CancellationToken ct = default)
  {
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      var status = await conn.ExecuteScalarAsync<string?>(
        new CommandDefinition(
          "SELECT [Status] FROM logistica.PhysicalCountSession WHERE Id = @SessionId;",
          new { SessionId = sessionId },
          tx,
          cancellationToken: ct));

      if (!IsDraftStatus(status) && !IsRecountStatus(status))
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Solo las sesiones en borrador o reconteo pueden enviarse a aprobación.");
      }

      var missingLines = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(
          """
          SELECT COUNT(*)
          FROM logistica.PhysicalCountLine
          WHERE SessionId = @SessionId
            AND CountedQuantity IS NULL;
          """,
          new { SessionId = sessionId },
          tx,
          cancellationToken: ct));

      if (missingLines > 0)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Todas las líneas deben capturar cantidad contada antes de enviar el conteo.");
      }

      var safeSubmittedBy = string.IsNullOrWhiteSpace(submittedBy) ? "OrionERP" : submittedBy.Trim();

      if (IsRecountStatus(status))
      {
        await conn.ExecuteAsync(
          new CommandDefinition(
            """
            UPDATE logistica.PhysicalCountRecountPlan
            SET CompletedAt = SYSUTCDATETIME(),
                CompletedBy = @SubmittedBy
            WHERE SessionId = @SessionId
              AND CompletedAt IS NULL;
            """,
            new
            {
              SessionId = sessionId,
              SubmittedBy = safeSubmittedBy
            },
            tx,
            cancellationToken: ct));
      }

      await conn.ExecuteAsync(
        new CommandDefinition(
          """
          UPDATE logistica.PhysicalCountSession
          SET [Status] = 'Submitted',
              SubmittedAt = SYSUTCDATETIME(),
              SubmittedBy = @SubmittedBy
          WHERE Id = @SessionId;
          """,
          new
          {
            SessionId = sessionId,
            SubmittedBy = safeSubmittedBy
          },
          tx,
          cancellationToken: ct));

      await tx.CommitAsync(ct);
      return LogisticsCommandResult.Ok("Sesión enviada a aprobación correctamente.", sessionId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<LogisticsCommandResult> ApproveSessionAsync(int sessionId, string approvedBy, CancellationToken ct = default)
  {
    using var conn = CreateConnection();
    var affected = await conn.ExecuteAsync(
      new CommandDefinition(
        """
        UPDATE logistica.PhysicalCountSession
        SET [Status] = 'Approved',
            ApprovedAt = SYSUTCDATETIME(),
            ApprovedBy = @ApprovedBy
        WHERE Id = @SessionId
          AND [Status] = 'Submitted';
        """,
        new
        {
          SessionId = sessionId,
          ApprovedBy = string.IsNullOrWhiteSpace(approvedBy) ? "OrionERP" : approvedBy.Trim()
        },
        cancellationToken: ct));

    return affected == 0
      ? LogisticsCommandResult.Fail("La sesión debe estar enviada para poder aprobarse.")
      : LogisticsCommandResult.Ok("Sesión aprobada correctamente.", sessionId);
  }

  public async Task<LogisticsCommandResult> RequestRecountAsync(PhysicalCountRecountRequest request, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    var lineRequests = request.Lines
      .Where(line => line.LineId > 0)
      .ToList();
    if (lineRequests.Count == 0)
    {
      return LogisticsCommandResult.Fail("Selecciona al menos una línea para enviar a reconteo.");
    }

    var duplicateLineId = lineRequests
      .GroupBy(line => line.LineId)
      .FirstOrDefault(group => group.Count() > 1)
      ?.Key;
    if (duplicateLineId.HasValue)
    {
      return LogisticsCommandResult.Fail("No repitas líneas en el plan de reconteo.");
    }

    foreach (var line in lineRequests)
    {
      if (string.IsNullOrWhiteSpace(line.IssueCode) || !PhysicalCountRecountIssueCodes.All.Contains(line.IssueCode))
      {
        return LogisticsCommandResult.Fail("Selecciona un tipo de incidencia válido para cada línea de reconteo.");
      }

      if (string.IsNullOrWhiteSpace(line.Reason))
      {
        return LogisticsCommandResult.Fail("Captura una razón para cada línea enviada a reconteo.");
      }
    }

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      var status = await conn.ExecuteScalarAsync<string?>(
        new CommandDefinition(
          "SELECT [Status] FROM logistica.PhysicalCountSession WHERE Id = @SessionId;",
          new { request.SessionId },
          tx,
          cancellationToken: ct));

      if (status is null)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("La sesión de conteo no existe.");
      }

      if (!IsSubmittedStatus(status) && !IsApprovedStatus(status))
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Solo las sesiones enviadas o aprobadas pueden enviarse a reconteo.");
      }

      var activeRecountPlans = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(
          """
          SELECT COUNT(*)
          FROM logistica.PhysicalCountRecountPlan
          WHERE SessionId = @SessionId
            AND CompletedAt IS NULL;
          """,
          new { request.SessionId },
          tx,
          cancellationToken: ct));

      if (activeRecountPlans > 0)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("La sesión ya tiene un plan de reconteo pendiente.");
      }

      var lineIds = lineRequests.Select(line => line.LineId).ToArray();
      var matchingLineCount = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(
          """
          SELECT COUNT(*)
          FROM logistica.PhysicalCountLine
          WHERE SessionId = @SessionId
            AND Id IN @LineIds;
          """,
          new
          {
            request.SessionId,
            LineIds = lineIds
          },
          tx,
          cancellationToken: ct));

      if (matchingLineCount != lineRequests.Count)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Una o más líneas seleccionadas no pertenecen a la sesión.");
      }

      var requestedBy = NullIfWhiteSpace(request.RequestedBy) ?? "OrionERP";
      var planId = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(
          """
          INSERT INTO logistica.PhysicalCountRecountPlan
          (
              SessionId,
              RequestedAt,
              RequestedBy
          )
          VALUES
          (
              @SessionId,
              SYSUTCDATETIME(),
              @RequestedBy
          );

          SELECT CAST(SCOPE_IDENTITY() AS int);
          """,
          new
          {
            request.SessionId,
            RequestedBy = requestedBy
          },
          tx,
          cancellationToken: ct));

      foreach (var line in lineRequests)
      {
        await conn.ExecuteAsync(
          new CommandDefinition(
            """
            INSERT INTO logistica.PhysicalCountRecountPlanLine
            (
                RecountPlanId,
                PhysicalCountLineId,
                IssueCode,
                Reason,
                PreviousCountedQuantity,
                PreviousVarianceQuantity,
                PreviousNotes,
                PreviousIsMissing,
                PreviousIsDamaged,
                PreviousCapturedAt,
                PreviousCapturedBy
            )
            SELECT
                @RecountPlanId,
                countLine.Id,
                @IssueCode,
                @Reason,
                countLine.CountedQuantity,
                countLine.VarianceQuantity,
                countLine.Notes,
                countLine.IsMissing,
                countLine.IsDamaged,
                countLine.CapturedAt,
                countLine.CapturedBy
            FROM logistica.PhysicalCountLine countLine
            WHERE countLine.Id = @LineId
              AND countLine.SessionId = @SessionId;
            """,
            new
            {
              RecountPlanId = planId,
              line.LineId,
              request.SessionId,
              IssueCode = line.IssueCode.Trim(),
              Reason = line.Reason.Trim()
            },
            tx,
            cancellationToken: ct));

        await conn.ExecuteAsync(
          new CommandDefinition(
            """
            UPDATE logistica.PhysicalCountLine
            SET CountedQuantity = NULL,
                VarianceQuantity = NULL,
                CapturedAt = NULL,
                CapturedBy = NULL
            WHERE Id = @LineId
              AND SessionId = @SessionId;
            """,
            new
            {
              line.LineId,
              request.SessionId
            },
            tx,
            cancellationToken: ct));
      }

      await conn.ExecuteAsync(
        new CommandDefinition(
          """
          UPDATE logistica.PhysicalCountSession
          SET [Status] = 'Recount',
              ApprovedAt = NULL,
              ApprovedBy = NULL
          WHERE Id = @SessionId;
          """,
          new { request.SessionId },
          tx,
          cancellationToken: ct));

      await tx.CommitAsync(ct);
      return LogisticsCommandResult.Ok("Sesión enviada a reconteo correctamente.", request.SessionId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<LogisticsCommandResult> CancelSessionAsync(PhysicalCountCancelRequest request, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    var reason = NullIfWhiteSpace(request.Reason);
    if (reason is null)
    {
      return LogisticsCommandResult.Fail("Captura una razón para cancelar el conteo.");
    }

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      var status = await conn.ExecuteScalarAsync<string?>(
        new CommandDefinition(
          "SELECT [Status] FROM logistica.PhysicalCountSession WHERE Id = @SessionId;",
          new { request.SessionId },
          tx,
          cancellationToken: ct));

      if (status is null)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("La sesión de conteo no existe.");
      }

      if (IsPostedStatus(status))
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Las sesiones contabilizadas no se pueden cancelar.");
      }

      if (IsCanceledStatus(status))
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("La sesión ya está cancelada.");
      }

      if (!IsCancelableStatus(status))
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("La sesión no está en un estado cancelable.");
      }

      var canceledBy = NullIfWhiteSpace(request.CanceledBy) ?? "OrionERP";
      await conn.ExecuteAsync(
        new CommandDefinition(
          """
          UPDATE logistica.PhysicalCountRecountPlan
          SET CompletedAt = SYSUTCDATETIME(),
              CompletedBy = @CanceledBy
          WHERE SessionId = @SessionId
            AND CompletedAt IS NULL;
          """,
          new
          {
            request.SessionId,
            CanceledBy = canceledBy
          },
          tx,
          cancellationToken: ct));

      var affected = await conn.ExecuteAsync(
        new CommandDefinition(
          """
          UPDATE logistica.PhysicalCountSession
          SET [Status] = 'Canceled',
              CanceledAt = SYSUTCDATETIME(),
              CanceledBy = @CanceledBy,
              CancelReason = @CancelReason
          WHERE Id = @SessionId;
          """,
          new
          {
            request.SessionId,
            CanceledBy = canceledBy,
            CancelReason = reason
          },
          tx,
          cancellationToken: ct));

      if (affected == 0)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("La sesión de conteo no existe.");
      }

      await tx.CommitAsync(ct);
      return LogisticsCommandResult.Ok("Sesión cancelada correctamente.", request.SessionId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<LogisticsCommandResult> PostSessionAsync(int sessionId, string postedBy, CancellationToken ct = default)
  {
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);

    try
    {
      var session = await conn.QueryFirstOrDefaultAsync<SessionPostingRow>(
        new CommandDefinition(
          """
          SELECT Id, [Status]
          FROM logistica.PhysicalCountSession
          WHERE Id = @SessionId;
          """,
          new { SessionId = sessionId },
          tx,
          cancellationToken: ct));

      if (session is null)
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("La sesión de conteo no existe.");
      }

      if (!string.Equals(session.Status, "Approved", StringComparison.OrdinalIgnoreCase))
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Solo las sesiones aprobadas pueden contabilizarse.");
      }

      var lines = (await conn.QueryAsync<LinePostingRow>(
        new CommandDefinition(
          """
          SELECT
              line.Id,
              line.StockBalanceId,
              line.MaterialId,
              line.LocationId,
              CAST(line.ExpectedQuantity AS decimal(18,4)) AS ExpectedQuantity,
              CAST(line.CountedQuantity AS decimal(18,4)) AS CountedQuantity,
              CAST(stockBalance.Quantity AS decimal(18,4)) AS SystemQuantity,
              CAST(stockBalance.ReservedQuantity AS decimal(18,4)) AS ReservedQuantity
          FROM logistica.PhysicalCountLine line
          JOIN logistica.StockBalance stockBalance ON stockBalance.Rfc=line.Rfc AND stockBalance.Id=line.StockBalanceId
          WHERE line.SessionId = @SessionId
          ORDER BY line.Id;
          """,
          new { SessionId = sessionId },
          tx,
          cancellationToken: ct))).AsList();

      if (lines.Any(line => line.CountedQuantity is null))
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Todas las líneas deben tener cantidad contada antes de contabilizar.");
      }
      if (lines.Any(line => line.CountedQuantity < line.ReservedQuantity))
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("No se puede contabilizar una cantidad menor que la existencia reservada.");
      }

      var safePostedBy = string.IsNullOrWhiteSpace(postedBy) ? "OrionERP" : postedBy.Trim();

      // Si la existencia del sistema ya no coincide con la que se fotografió al abrir el conteo,
      // hubo movimientos (compras, consumos, traspasos) mientras se contaba. El post sigue
      // sobrescribiendo con lo contado, pero el delta del kardex debe medirse contra la
      // existencia real y hay que avisar para que se revise si esos movimientos ya estaban
      // reflejados en el conteo físico.
      var driftedLines = lines.Count(line => PhysicalCountVarianceMath.MovedDuringCount(line.ExpectedQuantity, line.SystemQuantity));

      foreach (var line in lines)
      {
        var countedQuantity = line.CountedQuantity ?? 0m;
        var lineDrifted = PhysicalCountVarianceMath.MovedDuringCount(line.ExpectedQuantity, line.SystemQuantity);
        // El conteo se captura como total de la línea, sin desglose por lote. Si el material
        // arrastra saldos de lote de etapas anteriores, la diferencia se reparte por FEFO para
        // que la suma de los lotes siga cuadrando con la existencia contabilizada.
        var lotBalances = (await conn.QueryAsync<LotPostingRow>(new CommandDefinition(
          """
          SELECT lotBalance.MaterialLotId,
                 CAST(lotBalance.Quantity AS decimal(18,4)) AS Quantity,
                 CAST(lotBalance.ReservedQuantity AS decimal(18,4)) AS ReservedQuantity
          FROM logistica.LotBalance lotBalance WITH (UPDLOCK,HOLDLOCK)
          JOIN logistica.MaterialLot materialLot
            ON materialLot.Rfc=lotBalance.Rfc AND materialLot.Id=lotBalance.MaterialLotId
          WHERE lotBalance.MaterialId=@MaterialId AND lotBalance.LocationId=@LocationId
          ORDER BY CASE WHEN materialLot.ExpiresAt IS NULL THEN 1 ELSE 0 END,materialLot.ExpiresAt,materialLot.Id;
          """, new { line.MaterialId, line.LocationId }, tx, cancellationToken: ct))).AsList();
        if (lotBalances.Count > 0)
        {
          var pendingLotDelta = countedQuantity - lotBalances.Sum(lot => lot.Quantity);
          if (pendingLotDelta < 0m)
          {
            foreach (var lot in lotBalances)
            {
              if (pendingLotDelta >= 0m) break;
              var reducible = Math.Min(lot.Quantity - lot.ReservedQuantity, -pendingLotDelta);
              if (reducible <= 0m) continue;
              lot.Quantity -= reducible;
              pendingLotDelta += reducible;
            }
            if (pendingLotDelta < -0.0001m)
            {
              await tx.RollbackAsync(ct);
              return LogisticsCommandResult.Fail("No se puede contabilizar una cantidad menor que la existencia reservada.");
            }
          }
          else if (pendingLotDelta > 0m)
          {
            lotBalances[^1].Quantity += pendingLotDelta;
          }

          foreach (var lot in lotBalances)
            await conn.ExecuteAsync(new CommandDefinition(
              """
              UPDATE logistica.LotBalance SET Quantity=@Quantity,UpdatedAt=SYSUTCDATETIME()
              WHERE MaterialLotId=@MaterialLotId AND LocationId=@LocationId;
              """, new { lot.Quantity, lot.MaterialLotId, line.LocationId }, tx, cancellationToken: ct));
        }

        await conn.ExecuteAsync(
          new CommandDefinition(
            """
            UPDATE logistica.StockBalance
            SET Quantity = @CountedQuantity,
                LastCountedAt = SYSUTCDATETIME()
            WHERE Id = @StockBalanceId;
            """,
            new
            {
              CountedQuantity = countedQuantity,
              line.StockBalanceId
            },
            tx,
            cancellationToken: ct));

        await conn.ExecuteAsync(
          new CommandDefinition(
            """
            INSERT INTO logistica.StockTransaction
            (
                StockBalanceId,
                LocationId,
                MaterialId,
                TransactionType,
                QuantityDelta,
                QuantityAfter,
                ReferenceType,
                ReferenceId,
                Notes,
                PerformedBy,
                OccurredAt
            )
            VALUES
            (
                @StockBalanceId,
                @LocationId,
                @MaterialId,
                'CountAdjustment',
                @QuantityDelta,
                @QuantityAfter,
                'PhysicalCountSession',
                @ReferenceId,
                @Notes,
                @PerformedBy,
                SYSUTCDATETIME()
            );
            """,
            new
            {
              line.StockBalanceId,
              line.LocationId,
              line.MaterialId,
              QuantityDelta = PhysicalCountVarianceMath.PostingDelta(countedQuantity, line.SystemQuantity),
              QuantityAfter = countedQuantity,
              ReferenceId = sessionId,
              Notes = lineDrifted
                ? $"Conteo físico contabilizado desde sesión {sessionId}. Existencia esperada al abrir el conteo: {line.ExpectedQuantity:0.####}; existencia del sistema al contabilizar: {line.SystemQuantity:0.####} (hubo movimientos durante el conteo)."
                : $"Conteo físico contabilizado desde sesión {sessionId}.",
              PerformedBy = safePostedBy
            },
            tx,
            cancellationToken: ct));
      }

      await conn.ExecuteAsync(
        new CommandDefinition(
          """
          UPDATE logistica.PhysicalCountSession
          SET [Status] = 'Posted',
              PostedAt = SYSUTCDATETIME(),
              PostedBy = @PostedBy
          WHERE Id = @SessionId;
          """,
          new
          {
            SessionId = sessionId,
            PostedBy = safePostedBy
          },
          tx,
          cancellationToken: ct));

      await tx.CommitAsync(ct);
      return driftedLines == 0
        ? LogisticsCommandResult.Ok("Sesión contabilizada correctamente.", sessionId)
        : LogisticsCommandResult.Ok(
            $"Sesión contabilizada. Atención: {driftedLines} línea(s) tuvieron movimientos de inventario (compras, consumos o traspasos) mientras se contaba. Verifica que esos movimientos ya estuvieran reflejados en el conteo físico; si no, solicita un reconteo de esos materiales.",
            sessionId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<IReadOnlyList<PhysicalCountPendingRecountDto>> GetPendingRecountsAsync(CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          s.Id,
          s.SessionCode,
          s.ScopeType,
          l.LocationName,
          room.ROOM_NAME AS RoomName,
          scope.MaterialCount,
          scope.PrimaryMaterialLabel,
          COUNT(DISTINCT line.LocationId) AS LocationCount,
          activePlan.RequestedAt AS RecountRequestedAt,
          activePlan.RequestedBy AS RecountRequestedBy,
          COUNT(line.Id) AS LineCount,
          COUNT(activePlanLine.Id) AS RecountLineCount,
          COALESCE(STRING_AGG(CONVERT(varchar(max), activePlanLine.IssueCode), ', '), '') AS IssueSummary
      FROM logistica.PhysicalCountSession s
      LEFT JOIN logistica.Location l
        ON l.Id = s.LocationId
      LEFT JOIN dbo.ROOM room
        ON room.ID = l.RoomId
      OUTER APPLY
      (
          SELECT
              COUNT(*) AS MaterialCount,
              MIN(CONCAT(material.MaterialCode, ' · ', material.[Description])) AS PrimaryMaterialLabel
          FROM logistica.PhysicalCountSessionMaterial sessionMaterial
          JOIN logistica.Material material
            ON material.Rfc = sessionMaterial.Rfc
           AND material.Id = sessionMaterial.MaterialId
          WHERE sessionMaterial.SessionId = s.Id
      ) scope
      JOIN logistica.PhysicalCountRecountPlan activePlan
        ON activePlan.SessionId = s.Id
       AND activePlan.CompletedAt IS NULL
      LEFT JOIN logistica.PhysicalCountLine line
        ON line.SessionId = s.Id
      LEFT JOIN logistica.PhysicalCountRecountPlanLine activePlanLine
        ON activePlanLine.RecountPlanId = activePlan.Id
       AND activePlanLine.PhysicalCountLineId = line.Id
      WHERE s.[Status] = 'Recount'
      GROUP BY
          s.Id,
          s.SessionCode,
          s.ScopeType,
          l.LocationName,
          room.ROOM_NAME,
          scope.MaterialCount,
          scope.PrimaryMaterialLabel,
          activePlan.RequestedAt,
          activePlan.RequestedBy
      ORDER BY activePlan.RequestedAt DESC, s.Id DESC;
      """;

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<PhysicalCountPendingRecountDto>(
      new CommandDefinition(sql, cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<LogisticsBinaryContent?> GetAttachmentContentAsync(int attachmentId, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          attachment.Id,
          attachment.FileName,
          attachment.ContentType,
          attachment.Attachment AS Bytes
      FROM logistica.PhysicalCountAttachment attachment
      WHERE attachment.Id = @AttachmentId;
      """;

    using var conn = CreateConnection();
    var row = await conn.QueryFirstOrDefaultAsync<LogisticsBinaryContent>(
      new CommandDefinition(sql, new { AttachmentId = attachmentId }, cancellationToken: ct));

    if (row is null)
    {
      return null;
    }

    row.ContentType = LogisticsContentTypes.Normalize(row.ContentType, row.FileName, row.Bytes);
    return row;
  }

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica de conexiones no devolvió una DbConnection.");

  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private static string NormalizeStatus(string? status)
    => string.IsNullOrWhiteSpace(status) ? string.Empty : status.Trim();

  private static bool IsDraftStatus(string? status)
    => string.Equals(NormalizeStatus(status), PhysicalCountSessionStatuses.Draft, StringComparison.OrdinalIgnoreCase);

  private static bool IsSubmittedStatus(string? status)
    => string.Equals(NormalizeStatus(status), PhysicalCountSessionStatuses.Submitted, StringComparison.OrdinalIgnoreCase);

  private static bool IsApprovedStatus(string? status)
    => string.Equals(NormalizeStatus(status), PhysicalCountSessionStatuses.Approved, StringComparison.OrdinalIgnoreCase);

  private static bool IsRecountStatus(string? status)
    => string.Equals(NormalizeStatus(status), PhysicalCountSessionStatuses.Recount, StringComparison.OrdinalIgnoreCase);

  private static bool IsPostedStatus(string? status)
    => string.Equals(NormalizeStatus(status), PhysicalCountSessionStatuses.Posted, StringComparison.OrdinalIgnoreCase);

  private static bool IsCanceledStatus(string? status)
    => string.Equals(NormalizeStatus(status), PhysicalCountSessionStatuses.Canceled, StringComparison.OrdinalIgnoreCase);

  private static bool IsCancelableStatus(string? status)
    => IsDraftStatus(status)
      || IsSubmittedStatus(status)
      || IsApprovedStatus(status)
      || IsRecountStatus(status);

  private sealed class SessionPostingRow
  {
    public int Id { get; set; }
    public string Status { get; set; } = string.Empty;
  }

  private sealed class LineCaptureStateRow
  {
    public int Id { get; set; }
    public DateTime? CapturedAt { get; set; }
  }

  private sealed class LinePostingRow
  {
    public int Id { get; set; }
    public int StockBalanceId { get; set; }
    public int MaterialId { get; set; }
    public int LocationId { get; set; }
    public decimal ExpectedQuantity { get; set; }
    public decimal? CountedQuantity { get; set; }
    /// <summary>Existencia real en el momento de contabilizar (puede diferir de <see cref="ExpectedQuantity"/> si hubo movimientos durante el conteo).</summary>
    public decimal SystemQuantity { get; set; }
    public decimal ReservedQuantity { get; set; }
  }

  private sealed class LotPostingRow
  {
    public long MaterialLotId { get; set; }
    public decimal Quantity { get; set; }
    public decimal ReservedQuantity { get; set; }
  }
}
