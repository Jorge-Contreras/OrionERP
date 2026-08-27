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
          s.LocationId,
          l.LocationName,
          room.ROOM_NAME AS RoomName,
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
      JOIN logistica.Location l
        ON l.Id = s.LocationId
      LEFT JOIN dbo.ROOM room
        ON room.ID = l.RoomId
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
          s.LocationId,
          l.LocationName,
          room.ROOM_NAME,
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
          s.LocationId,
          l.LocationName,
          room.ROOM_NAME AS RoomName,
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
      JOIN logistica.Location l
        ON l.Id = s.LocationId
      LEFT JOIN dbo.ROOM room
        ON room.ID = l.RoomId
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
      LEFT JOIN logistica.UnitOfMeasure u
        ON u.Id = m.BaseUnitId
      LEFT JOIN logistica.PhysicalCountRecountPlan activePlan
        ON activePlan.SessionId = line.SessionId
       AND activePlan.CompletedAt IS NULL
      LEFT JOIN logistica.PhysicalCountRecountPlanLine activePlanLine
        ON activePlanLine.RecountPlanId = activePlan.Id
       AND activePlanLine.PhysicalCountLineId = line.Id
      WHERE line.SessionId = @SessionId
      ORDER BY m.MaterialCode, m.[Description], line.Id;

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

      SELECT lotLine.Id,lotLine.PhysicalCountLineId,lotLine.MaterialLotId,materialLot.LotCode,materialLot.ExpiresAt,
             CAST(lotLine.ExpectedQuantity AS decimal(18,4)) AS ExpectedQuantity,
             CAST(lotLine.CountedQuantity AS decimal(18,4)) AS CountedQuantity,
             CAST(lotLine.VarianceQuantity AS decimal(18,4)) AS VarianceQuantity
      FROM logistica.PhysicalCountLotLine lotLine
      JOIN logistica.PhysicalCountLine line ON line.Rfc=lotLine.Rfc AND line.Id=lotLine.PhysicalCountLineId
      JOIN logistica.MaterialLot materialLot ON materialLot.Rfc=lotLine.Rfc AND materialLot.Id=lotLine.MaterialLotId
      WHERE line.SessionId=@SessionId
      ORDER BY materialLot.ExpiresAt,materialLot.LotCode;
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
    var lots = (await multi.ReadAsync<PhysicalCountLotLineDto>()).AsList();
    var attachmentsByLine = attachments
      .GroupBy(attachment => attachment.PhysicalCountLineId)
      .ToDictionary(group => group.Key, group => (IReadOnlyList<PhysicalCountAttachmentDto>)group.ToList());

    foreach (var line in lines)
    {
      line.Attachments = attachmentsByLine.TryGetValue(line.Id, out var lineAttachments)
        ? lineAttachments
        : Array.Empty<PhysicalCountAttachmentDto>();
      line.Lots = lots.Where(lot => lot.PhysicalCountLineId == line.Id).ToList();
    }

    session.Lines = lines;
    return session;
  }

  public async Task<LogisticsCommandResult> CreateSessionAsync(PhysicalCountSessionCreateRequest request, CancellationToken ct = default)
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

      var sessionId = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(
          """
          INSERT INTO logistica.PhysicalCountSession
          (
              SessionCode,
              LocationId,
              [Status],
              Notes,
              CreatedAt,
              CreatedBy
          )
          VALUES
          (
              CONCAT('TMP-', LEFT(REPLACE(CONVERT(varchar(36), NEWID()), '-', ''), 20)),
              @LocationId,
              'Draft',
              @Notes,
              SYSUTCDATETIME(),
              @CreatedBy
          );

          SELECT CAST(SCOPE_IDENTITY() AS int);
          """,
          new
          {
            request.LocationId,
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

      await conn.ExecuteAsync(
        new CommandDefinition(
          """
          ;WITH LocationScope AS (
              SELECT Id
              FROM logistica.Location
              WHERE Id = @LocationId

              UNION ALL

              SELECT child.Id
              FROM logistica.Location child
              JOIN LocationScope parent
                ON parent.Id = child.ParentLocationId
          )
          INSERT INTO logistica.PhysicalCountLine
          (
              SessionId,
              StockBalanceId,
              LocationId,
              MaterialId,
              ExpectedQuantity
          )
          SELECT
              @SessionId,
              sb.Id,
              sb.LocationId,
              sb.MaterialId,
              sb.Quantity
          FROM logistica.StockBalance sb
          JOIN LocationScope scope
            ON scope.Id = sb.LocationId
          WHERE ISNULL(sb.IsRemoved, 0) = 0;
          """,
          new { SessionId = sessionId, request.LocationId },
          tx,
          cancellationToken: ct));

      await conn.ExecuteAsync(
        new CommandDefinition(
          """
          INSERT INTO logistica.PhysicalCountLotLine
            (Rfc,PhysicalCountLineId,MaterialLotId,ExpectedQuantity)
          SELECT countLine.Rfc,countLine.Id,lotBalance.MaterialLotId,lotBalance.Quantity
          FROM logistica.PhysicalCountLine countLine
          JOIN logistica.LotBalance lotBalance
            ON lotBalance.Rfc=countLine.Rfc AND lotBalance.MaterialId=countLine.MaterialId AND lotBalance.LocationId=countLine.LocationId
          WHERE countLine.SessionId=@SessionId AND lotBalance.Quantity<>0;
          """,
          new { SessionId = sessionId },
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
        return LogisticsCommandResult.Fail("La ubicación no tiene existencias para generar un conteo físico.");
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

      var expectedLots = (await conn.QueryAsync<PhysicalCountLotLineDto>(new CommandDefinition(
        """
        SELECT lotLine.MaterialLotId,lotLine.ExpectedQuantity,lotLine.CountedQuantity
        FROM logistica.PhysicalCountLotLine lotLine WITH (UPDLOCK,HOLDLOCK)
        JOIN logistica.PhysicalCountLine countLine ON countLine.Rfc=lotLine.Rfc AND countLine.Id=lotLine.PhysicalCountLineId
        WHERE lotLine.PhysicalCountLineId=@LineId AND countLine.SessionId=@SessionId;
        """, new { request.LineId, request.SessionId }, tx, cancellationToken: ct))).AsList();
      if (expectedLots.Count > 0)
      {
        var capturedLots = request.Lots.GroupBy(lot => lot.MaterialLotId).ToDictionary(group => group.Key, group => group.Single().CountedQuantity);
        if (capturedLots.Count != expectedLots.Count || expectedLots.Any(lot => !capturedLots.TryGetValue(lot.MaterialLotId, out var quantity) || !quantity.HasValue || quantity.Value < 0))
        {
          await tx.RollbackAsync(ct);
          return LogisticsCommandResult.Fail("Captura una cantidad no negativa para cada lote del material.");
        }
        var lotTotal = capturedLots.Values.Sum(quantity => quantity ?? 0);
        if (Math.Abs(lotTotal - request.CountedQuantity) > 0.0001m)
        {
          await tx.RollbackAsync(ct);
          return LogisticsCommandResult.Fail("La suma contada por lote debe coincidir con el total de la línea.");
        }
        foreach (var lot in expectedLots)
        {
          var counted = capturedLots[lot.MaterialLotId]!.Value;
          await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE logistica.PhysicalCountLotLine
            SET CountedQuantity=@CountedQuantity
            WHERE PhysicalCountLineId=@LineId AND MaterialLotId=@MaterialLotId;
            """, new { request.LineId, lot.MaterialLotId, CountedQuantity = counted }, tx, cancellationToken: ct));
        }
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
            UPDATE logistica.PhysicalCountLotLine SET CountedQuantity=NULL WHERE PhysicalCountLineId=@LineId;
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

      foreach (var line in lines)
      {
        var countedQuantity = line.CountedQuantity ?? 0m;
        var lotLines = (await conn.QueryAsync<LotPostingRow>(new CommandDefinition(
          """
          SELECT countLot.MaterialLotId,countLot.CountedQuantity,lotBalance.ReservedQuantity
          FROM logistica.PhysicalCountLotLine countLot
          JOIN logistica.LotBalance lotBalance
            ON lotBalance.Rfc=countLot.Rfc AND lotBalance.MaterialLotId=countLot.MaterialLotId AND lotBalance.LocationId=@LocationId
          WHERE countLot.PhysicalCountLineId=@LineId;
          """, new { LineId = line.Id, line.LocationId }, tx, cancellationToken: ct))).AsList();
        if (lotLines.Any(lot => !lot.CountedQuantity.HasValue || lot.CountedQuantity < lot.ReservedQuantity))
        {
          await tx.RollbackAsync(ct);
          return LogisticsCommandResult.Fail("Todos los lotes deben estar contados y conservar su cantidad reservada.");
        }
        foreach (var lot in lotLines)
          await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE logistica.LotBalance SET Quantity=@CountedQuantity,UpdatedAt=SYSUTCDATETIME()
            WHERE MaterialLotId=@MaterialLotId AND LocationId=@LocationId;
            """, new { CountedQuantity = lot.CountedQuantity!.Value, lot.MaterialLotId, line.LocationId }, tx, cancellationToken: ct));
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
              QuantityDelta = countedQuantity - line.ExpectedQuantity,
              QuantityAfter = countedQuantity,
              ReferenceId = sessionId,
              Notes = $"Conteo físico contabilizado desde sesión {sessionId}.",
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
      return LogisticsCommandResult.Ok("Sesión contabilizada correctamente.", sessionId);
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
          l.LocationName,
          room.ROOM_NAME AS RoomName,
          activePlan.RequestedAt AS RecountRequestedAt,
          activePlan.RequestedBy AS RecountRequestedBy,
          COUNT(line.Id) AS LineCount,
          COUNT(activePlanLine.Id) AS RecountLineCount,
          COALESCE(STRING_AGG(CONVERT(varchar(max), activePlanLine.IssueCode), ', '), '') AS IssueSummary
      FROM logistica.PhysicalCountSession s
      JOIN logistica.Location l
        ON l.Id = s.LocationId
      LEFT JOIN dbo.ROOM room
        ON room.ID = l.RoomId
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
          l.LocationName,
          room.ROOM_NAME,
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
    public decimal ReservedQuantity { get; set; }
  }

  private sealed class LotPostingRow
  {
    public long MaterialLotId { get; set; }
    public decimal? CountedQuantity { get; set; }
    public decimal ReservedQuantity { get; set; }
  }
}
