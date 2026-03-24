using System.Data;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Logistica.Stock;
using OrionERP.Infrastructure.Features.Logistica.Support;

namespace OrionERP.Infrastructure.Features.Logistica.Stock;

public sealed class StockService : IStockService
{
  private readonly IDbConnectionFactory _connectionFactory;

  public StockService(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  }

  public async Task<IReadOnlyList<StockListItemDto>> GetStockAsync(StockFilter filter, CancellationToken ct = default)
  {
    filter ??= new StockFilter();

    var sql = new StringBuilder(
      """
      WITH AttachmentCounts AS (
          SELECT
              a.LocationId,
              a.MaterialId,
              COUNT(*) AS AttachmentCount
          FROM logistica.LocationMaterialAttachment a
          GROUP BY a.LocationId, a.MaterialId
      )
      SELECT
          sb.Id AS StockBalanceId,
          l.Id AS LocationId,
          l.LocationCode,
          l.LocationName,
          l.LocationType,
          room.ROOM_NAME AS RoomName,
          m.Id AS MaterialId,
          m.MaterialCode,
          m.[Description] AS MaterialDescription,
          m.MaterialClass,
          m.Barcode,
          u.UnitName AS BaseUnitName,
          bp.PartnerName AS VendorName,
          CAST(sb.Quantity AS decimal(18,4)) AS Quantity,
          CAST(sb.MinQuantity AS decimal(18,4)) AS MinQuantity,
          CAST(sb.MaxQuantity AS decimal(18,4)) AS MaxQuantity,
          CAST(CASE WHEN sb.MinQuantity IS NOT NULL AND sb.Quantity <= sb.MinQuantity THEN 1 ELSE 0 END AS bit) AS IsLowStock,
          CAST(CASE
              WHEN sb.CountFrequencyDays IS NULL THEN 0
              WHEN sb.LastCountedAt IS NULL THEN 1
              WHEN DATEADD(day, sb.CountFrequencyDays, sb.LastCountedAt) <= SYSUTCDATETIME() THEN 1
              ELSE 0
          END AS bit) AS IsCountDue,
          sb.LastCountedAt,
          sb.CountFrequencyDays,
          ISNULL(ac.AttachmentCount, 0) AS AttachmentCount
      FROM logistica.StockBalance sb
      JOIN logistica.Location l
        ON l.Id = sb.LocationId
      LEFT JOIN dbo.ROOM room
        ON room.ID = l.RoomId
      JOIN logistica.Material m
        ON m.Id = sb.MaterialId
      LEFT JOIN logistica.UnitOfMeasure u
        ON u.Id = m.BaseUnitId
      LEFT JOIN dbo.BusinessPartner bp
        ON bp.Id = m.BusinessPartnerId
      LEFT JOIN AttachmentCounts ac
        ON ac.LocationId = sb.LocationId
       AND ac.MaterialId = sb.MaterialId
      WHERE 1 = 1
      """);

    var parameters = new DynamicParameters();

    if (!filter.IncludeZeroBalances)
    {
      sql.AppendLine(" AND sb.Quantity <> 0");
    }

    if (filter.RoomId.HasValue)
    {
      sql.AppendLine(" AND l.RoomId = @RoomId");
      parameters.Add("@RoomId", filter.RoomId.Value, DbType.Int32);
    }

    if (filter.LocationId.HasValue)
    {
      sql.AppendLine(" AND (l.Id = @LocationId OR l.ParentLocationId = @LocationId)");
      parameters.Add("@LocationId", filter.LocationId.Value, DbType.Int32);
    }

    if (!string.IsNullOrWhiteSpace(filter.SearchText))
    {
      sql.AppendLine(
        """
         AND (
             m.MaterialCode LIKE @Search
             OR m.[Description] LIKE @Search
             OR m.Barcode LIKE @Search
             OR l.LocationCode LIKE @Search
             OR l.LocationName LIKE @Search
             OR room.ROOM_NAME LIKE @Search
         )
        """);
      parameters.Add("@Search", $"%{filter.SearchText.Trim()}%", DbType.String);
    }

    if (filter.LowStockOnly)
    {
      sql.AppendLine(" AND sb.MinQuantity IS NOT NULL AND sb.Quantity <= sb.MinQuantity");
    }

    if (filter.CountDueOnly)
    {
      sql.AppendLine(
        """
         AND sb.CountFrequencyDays IS NOT NULL
         AND (
             sb.LastCountedAt IS NULL
             OR DATEADD(day, sb.CountFrequencyDays, sb.LastCountedAt) <= SYSUTCDATETIME()
         )
        """);
    }

    sql.AppendLine("ORDER BY room.ROOM_NAME, l.LocationName, m.MaterialCode, m.[Description], sb.Id;");

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<StockListItemDto>(
      new CommandDefinition(sql.ToString(), parameters, cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<IReadOnlyList<StockTransactionDto>> GetStockTransactionsAsync(int stockBalanceId, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          st.Id,
          st.OccurredAt,
          st.TransactionType,
          CAST(st.QuantityDelta AS decimal(18,4)) AS QuantityDelta,
          CAST(st.QuantityAfter AS decimal(18,4)) AS QuantityAfter,
          st.ReferenceType,
          st.ReferenceId,
          st.Notes,
          st.PerformedBy
      FROM logistica.StockTransaction st
      WHERE st.StockBalanceId = @StockBalanceId
      ORDER BY st.OccurredAt DESC, st.Id DESC;
      """;

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<StockTransactionDto>(
      new CommandDefinition(sql, new { StockBalanceId = stockBalanceId }, cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<IReadOnlyList<LocationMaterialAttachmentDto>> GetLocationMaterialAttachmentsAsync(int locationId, int materialId, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          a.Id,
          a.FileName,
          a.FileExtension,
          a.[Description],
          DATALENGTH(a.Attachment) AS [Length],
          a.CreatedAt,
          a.CreatedBy
      FROM logistica.LocationMaterialAttachment a
      WHERE a.LocationId = @LocationId
        AND a.MaterialId = @MaterialId
      ORDER BY a.CreatedAt DESC, a.Id DESC;
      """;

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<LocationMaterialAttachmentDto>(
      new CommandDefinition(sql, new { LocationId = locationId, MaterialId = materialId }, cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<LogisticsBinaryContent?> GetLocationMaterialAttachmentContentAsync(int attachmentId, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          a.Id,
          a.FileName,
          a.ContentType,
          a.Attachment AS Bytes
      FROM logistica.LocationMaterialAttachment a
      WHERE a.Id = @AttachmentId;
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

  public async Task<LogisticsCommandResult> SaveLocationMaterialAttachmentAsync(LocationMaterialAttachmentCreateRequest request, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    if (request.Bytes.Length == 0)
    {
      return LogisticsCommandResult.Fail("Debes adjuntar un archivo para guardar evidencia.");
    }

    const string sql =
      """
      INSERT INTO logistica.LocationMaterialAttachment
      (
          LocationId,
          MaterialId,
          FileName,
          FileExtension,
          ContentType,
          [Description],
          Attachment,
          CreatedBy
      )
      VALUES
      (
          @LocationId,
          @MaterialId,
          @FileName,
          @FileExtension,
          @ContentType,
          @Description,
          @Attachment,
          @CreatedBy
      );

      SELECT CAST(SCOPE_IDENTITY() AS int);
      """;

    using var conn = CreateConnection();
    var id = await conn.ExecuteScalarAsync<int>(
      new CommandDefinition(
        sql,
        new
        {
          request.LocationId,
          request.MaterialId,
          FileName = request.FileName.Trim(),
          FileExtension = request.FileExtension.Trim().TrimStart('.'),
          ContentType = LogisticsContentTypes.Normalize(request.ContentType, request.FileName, request.Bytes),
          Description = NullIfWhiteSpace(request.Description),
          Attachment = request.Bytes,
          CreatedBy = "OrionERP"
        },
        cancellationToken: ct));

    return LogisticsCommandResult.Ok("Adjunto de inventario guardado correctamente.", id);
  }

  private SqlConnection CreateConnection()
    => _connectionFactory.Create() as SqlConnection
      ?? throw new InvalidOperationException("La fábrica de conexiones no devolvió una SqlConnection.");

  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
