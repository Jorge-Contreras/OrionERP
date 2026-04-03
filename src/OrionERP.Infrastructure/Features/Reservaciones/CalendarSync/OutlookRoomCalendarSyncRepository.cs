using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OrionERP.Application.Features.Reservaciones.CalendarSync;

namespace OrionERP.Infrastructure.Features.Reservaciones.CalendarSync;

public sealed class OutlookRoomCalendarSyncRepository : IOutlookRoomCalendarSyncRepository
{
  private readonly string _connectionString;
  private readonly ILogger<OutlookRoomCalendarSyncRepository> _logger;

  public OutlookRoomCalendarSyncRepository(
    IConfiguration configuration,
    ILogger<OutlookRoomCalendarSyncRepository> logger)
  {
    _connectionString = configuration.GetConnectionString("OrionDb")
      ?? throw new InvalidOperationException("Missing connection string: OrionDb");
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  public async Task<IReadOnlyList<OrionRoomCalendarBlock>> GetBlockedBlocksAsync(
    DateTime startDate,
    DateTime endDateExclusive,
    IReadOnlyCollection<string> roomNames,
    CancellationToken ct = default)
  {
    if (roomNames is null || roomNames.Count == 0)
    {
      return Array.Empty<OrionRoomCalendarBlock>();
    }

    const string sql = """
SELECT
    rc.ROOM AS RoomName,
    rc.ROOM_DATE AS RoomDate,
    r.ID AS ReservationId,
    NULLIF(LTRIM(RTRIM(rc.LOCKED_BY)), '') AS LockedBy,
    NULLIF(LTRIM(RTRIM(rc.LOCK_DESCRIPTION)), '') AS LockDescription,
    NULLIF(LTRIM(RTRIM(rc.STATUS)), '') AS Status
FROM dbo.ROOM_CALENDAR rc
LEFT JOIN dbo.RESERVATION r
  ON r.ID = TRY_CAST(rc.LOCK_DESCRIPTION AS int)
WHERE rc.ROOM IN @Rooms
  AND rc.ROOM_DATE >= @StartDate
  AND rc.ROOM_DATE < @EndDateExclusive
  AND CAST(ISNULL(rc.IS_LOCKED, 0) AS bit) = 1
  AND UPPER(LTRIM(RTRIM(ISNULL(rc.LOCKED_BY, '')))) COLLATE Latin1_General_100_CI_AI <> N'COTIZACION'
ORDER BY rc.ROOM, rc.ROOM_DATE;
""";

    await using var connection = new SqlConnection(_connectionString);
    var rows = await connection.QueryAsync<OrionRoomCalendarLockRow>(
      new CommandDefinition(
        sql,
        new
        {
          Rooms = roomNames,
          StartDate = startDate.Date,
          EndDateExclusive = endDateExclusive.Date
        },
        cancellationToken: ct));

    return BonhomiaCalendarSyncBlockBuilder.BuildBlocks(rows);
  }

  public async Task<IReadOnlyList<OutlookRoomCalendarSyncMapping>> GetMappingsAsync(
    DateTime startDate,
    DateTime endDateExclusive,
    IReadOnlyCollection<string> roomNames,
    CancellationToken ct = default)
  {
    if (roomNames is null || roomNames.Count == 0)
    {
      return Array.Empty<OutlookRoomCalendarSyncMapping>();
    }

    const string sql = """
SELECT
    s.ID AS Id,
    s.SOURCE_KEY AS SourceKey,
    s.ROOM_NAME AS RoomName,
    s.RESERVATION_ID AS ReservationId,
    s.START_DATE AS StartDate,
    s.END_DATE_EXCLUSIVE AS EndDateExclusive,
    s.OUTLOOK_CALENDAR_ID AS OutlookCalendarId,
    s.OUTLOOK_EVENT_ID AS OutlookEventId,
    s.CONTENT_HASH AS ContentHash,
    s.LAST_SYNCED_UTC AS LastSyncedUtc
FROM dbo.ROOM_CALENDAR_OUTLOOK_SYNC s
WHERE s.ROOM_NAME IN @Rooms
  AND s.START_DATE < @EndDateExclusive
  AND s.END_DATE_EXCLUSIVE > @StartDate
ORDER BY s.ROOM_NAME, s.START_DATE, s.ID;
""";

    await using var connection = new SqlConnection(_connectionString);
    var rows = await connection.QueryAsync<OutlookRoomCalendarSyncMapping>(
      new CommandDefinition(
        sql,
        new
        {
          Rooms = roomNames,
          StartDate = startDate.Date,
          EndDateExclusive = endDateExclusive.Date
        },
        cancellationToken: ct));

    return rows.AsList();
  }

  public async Task UpsertMappingsAsync(
    IReadOnlyCollection<OutlookRoomCalendarSyncMappingUpsert> mappings,
    CancellationToken ct = default)
  {
    if (mappings is null || mappings.Count == 0)
    {
      return;
    }

    const string sql = """
MERGE dbo.ROOM_CALENDAR_OUTLOOK_SYNC AS target
USING (VALUES
  (@SourceKey, @RoomName, @ReservationId, @StartDate, @EndDateExclusive, @OutlookCalendarId, @OutlookEventId, @ContentHash)
) AS source
  (SOURCE_KEY, ROOM_NAME, RESERVATION_ID, START_DATE, END_DATE_EXCLUSIVE, OUTLOOK_CALENDAR_ID, OUTLOOK_EVENT_ID, CONTENT_HASH)
ON target.SOURCE_KEY = source.SOURCE_KEY
AND target.OUTLOOK_CALENDAR_ID = source.OUTLOOK_CALENDAR_ID
WHEN MATCHED THEN
  UPDATE SET
      ROOM_NAME = source.ROOM_NAME,
      RESERVATION_ID = source.RESERVATION_ID,
      START_DATE = source.START_DATE,
      END_DATE_EXCLUSIVE = source.END_DATE_EXCLUSIVE,
      OUTLOOK_EVENT_ID = source.OUTLOOK_EVENT_ID,
      CONTENT_HASH = source.CONTENT_HASH,
      LAST_SYNCED_UTC = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
  INSERT
      (SOURCE_KEY, ROOM_NAME, RESERVATION_ID, START_DATE, END_DATE_EXCLUSIVE, OUTLOOK_CALENDAR_ID, OUTLOOK_EVENT_ID, CONTENT_HASH, LAST_SYNCED_UTC)
  VALUES
      (source.SOURCE_KEY, source.ROOM_NAME, source.RESERVATION_ID, source.START_DATE, source.END_DATE_EXCLUSIVE, source.OUTLOOK_CALENDAR_ID, source.OUTLOOK_EVENT_ID, source.CONTENT_HASH, SYSUTCDATETIME());
""";

    await using var connection = new SqlConnection(_connectionString);
    await connection.OpenAsync(ct);
    await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct) as SqlTransaction;

    try
    {
      foreach (var mapping in mappings)
      {
        await connection.ExecuteAsync(
          new CommandDefinition(sql, mapping, transaction, cancellationToken: ct));
      }

      await transaction!.CommitAsync(ct);
    }
    catch (Exception ex)
    {
      try { await transaction!.RollbackAsync(ct); } catch { /* ignore */ }
      _logger.LogError(ex, "Error upserting Outlook sync mappings.");
      throw;
    }
  }

  public async Task DeleteMappingsAsync(
    IReadOnlyCollection<int> mappingIds,
    CancellationToken ct = default)
  {
    if (mappingIds is null || mappingIds.Count == 0)
    {
      return;
    }

    const string sql = "DELETE FROM dbo.ROOM_CALENDAR_OUTLOOK_SYNC WHERE ID IN @Ids;";

    await using var connection = new SqlConnection(_connectionString);
    await connection.ExecuteAsync(
      new CommandDefinition(sql, new { Ids = mappingIds }, cancellationToken: ct));
  }
}
