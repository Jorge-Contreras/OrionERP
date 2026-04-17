using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
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
          COUNT(line.Id) AS LineCount,
          SUM(CASE WHEN line.VarianceQuantity IS NOT NULL AND line.VarianceQuantity <> 0 THEN 1 ELSE 0 END) AS VarianceLineCount
      FROM logistica.PhysicalCountSession s
      JOIN logistica.Location l
        ON l.Id = s.LocationId
      LEFT JOIN dbo.ROOM room
        ON room.ID = l.RoomId
      LEFT JOIN logistica.PhysicalCountLine line
        ON line.SessionId = s.Id
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
          s.PostedBy
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
          s.PostedBy
      FROM logistica.PhysicalCountSession s
      JOIN logistica.Location l
        ON l.Id = s.LocationId
      LEFT JOIN dbo.ROOM room
        ON room.ID = l.RoomId
      WHERE s.Id = @SessionId;

      SELECT
          line.Id,
          line.StockBalanceId,
          line.MaterialId,
          m.MaterialCode,
          m.[Description] AS MaterialDescription,
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
    using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

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
    using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

    try
    {
      var status = await conn.ExecuteScalarAsync<string?>(
        new CommandDefinition(
          "SELECT [Status] FROM logistica.PhysicalCountSession WHERE Id = @SessionId;",
          new { request.SessionId },
          tx,
          cancellationToken: ct));

      if (!string.Equals(status, "Draft", StringComparison.OrdinalIgnoreCase))
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Solo las sesiones en borrador permiten capturar conteos.");
      }

      await conn.ExecuteAsync(
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

  public async Task<LogisticsCommandResult> SubmitSessionAsync(int sessionId, string submittedBy, CancellationToken ct = default)
  {
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

    try
    {
      var status = await conn.ExecuteScalarAsync<string?>(
        new CommandDefinition(
          "SELECT [Status] FROM logistica.PhysicalCountSession WHERE Id = @SessionId;",
          new { SessionId = sessionId },
          tx,
          cancellationToken: ct));

      if (!string.Equals(status, "Draft", StringComparison.OrdinalIgnoreCase))
      {
        await tx.RollbackAsync(ct);
        return LogisticsCommandResult.Fail("Solo las sesiones en borrador pueden enviarse a aprobación.");
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
            SubmittedBy = string.IsNullOrWhiteSpace(submittedBy) ? "OrionERP" : submittedBy.Trim()
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

  public async Task<LogisticsCommandResult> PostSessionAsync(int sessionId, string postedBy, CancellationToken ct = default)
  {
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);

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
              CAST(line.CountedQuantity AS decimal(18,4)) AS CountedQuantity
          FROM logistica.PhysicalCountLine line
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

      var safePostedBy = string.IsNullOrWhiteSpace(postedBy) ? "OrionERP" : postedBy.Trim();

      foreach (var line in lines)
      {
        var countedQuantity = line.CountedQuantity ?? 0m;
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

  private SqlConnection CreateConnection()
    => _connectionFactory.Create() as SqlConnection
      ?? throw new InvalidOperationException("La fábrica de conexiones no devolvió una SqlConnection.");

  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private sealed class SessionPostingRow
  {
    public int Id { get; set; }
    public string Status { get; set; } = string.Empty;
  }

  private sealed class LinePostingRow
  {
    public int Id { get; set; }
    public int StockBalanceId { get; set; }
    public int MaterialId { get; set; }
    public int LocationId { get; set; }
    public decimal ExpectedQuantity { get; set; }
    public decimal? CountedQuantity { get; set; }
  }
}
