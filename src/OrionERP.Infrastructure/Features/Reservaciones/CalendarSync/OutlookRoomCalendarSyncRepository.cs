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
using OrionERP.Application.Features.Reservaciones;

namespace OrionERP.Infrastructure.Features.Reservaciones.CalendarSync;

public sealed class OutlookRoomCalendarSyncRepository : IOutlookRoomCalendarSyncRepository
{
  private readonly string _connectionString;
  private readonly IHospitalityScopeAccessor _scopeAccessor;
  private readonly ILogger<OutlookRoomCalendarSyncRepository> _logger;

  public OutlookRoomCalendarSyncRepository(
    IConfiguration configuration,
    ILogger<OutlookRoomCalendarSyncRepository> logger,
    IHospitalityScopeAccessor scopeAccessor)
  {
    _connectionString = configuration.GetConnectionString("OrionDb")
      ?? throw new InvalidOperationException("Missing connection string: OrionDb");
    _scopeAccessor = scopeAccessor ?? throw new ArgumentNullException(nameof(scopeAccessor));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  public async Task<IReadOnlyList<OrionRoomCalendarBlock>> GetBlockedBlocksAsync(
    HospitalityScope scope,
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
IF (SELECT COUNT_BIG(*) FROM dbo.ROOM
    WHERE OrionCompanyId=@ScopeCompanyId AND OrionSiteId=@ScopeSiteId AND ROOM_NAME IN @Rooms) <> @RoomCount
  THROW 51042, 'Configured calendars do not resolve uniquely in the active company/site.', 1;
SELECT
    rc.ROOM AS RoomName,
    rc.ROOM_DATE AS RoomDate,
    r.ID AS ReservationId,
    NULLIF(LTRIM(RTRIM(rc.LOCKED_BY)), '') AS LockedBy,
    NULLIF(LTRIM(RTRIM(rc.LOCK_DESCRIPTION)), '') AS LockDescription,
    COALESCE(NULLIF(LTRIM(RTRIM(r.STATUS)), ''), NULLIF(LTRIM(RTRIM(rc.STATUS)), '')) AS Status
FROM dbo.ROOM_CALENDAR rc
INNER JOIN dbo.ROOM room
  ON room.ID = rc.RoomId
 AND room.OrionCompanyId = @ScopeCompanyId
 AND room.OrionSiteId = @ScopeSiteId
LEFT JOIN dbo.RESERVATION r
  ON r.ID = TRY_CAST(rc.LOCK_DESCRIPTION AS int)
 AND r.OrionCompanyId = @ScopeCompanyId
 AND r.OrionSiteId = @ScopeSiteId
WHERE rc.OrionCompanyId = @ScopeCompanyId
  AND rc.OrionSiteId = @ScopeSiteId
  AND rc.ROOM = room.ROOM_NAME
  AND rc.ROOM IN @Rooms
  AND (TRY_CAST(rc.LOCK_DESCRIPTION AS int) IS NULL OR r.ID IS NOT NULL)
  AND rc.ROOM_DATE >= @StartDate
  AND rc.ROOM_DATE < @EndDateExclusive
  AND CAST(ISNULL(rc.IS_LOCKED, 0) AS bit) = 1
  AND (
      r.ID IS NULL
      OR UPPER(LTRIM(RTRIM(ISNULL(r.STATUS, '')))) COLLATE Latin1_General_100_CI_AI <> N'COTIZACION'
  )
ORDER BY rc.ROOM, rc.ROOM_DATE;
""";

    await using var connection = await OpenAsync(scope, ct);
    var rows = await connection.QueryAsync<OrionRoomCalendarLockRow>(
      new CommandDefinition(
        sql,
        new
        {
          ScopeCompanyId = scope.CompanyId,
          ScopeSiteId = scope.SiteId,
          Rooms = roomNames,
          RoomCount = roomNames.Count,
          StartDate = startDate.Date,
          EndDateExclusive = endDateExclusive.Date
        },
        cancellationToken: ct));

    return BonhomiaCalendarSyncBlockBuilder.BuildBlocks(rows);
  }

  public async Task<IReadOnlyList<OutlookRoomCalendarSyncMapping>> GetMappingsAsync(
    HospitalityScope scope,
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
INNER JOIN dbo.ROOM room
  ON room.ID=s.RoomId AND room.ROOM_NAME=s.ROOM_NAME
 AND room.OrionCompanyId=@ScopeCompanyId AND room.OrionSiteId=@ScopeSiteId
WHERE s.OrionCompanyId = @ScopeCompanyId
  AND s.OrionSiteId = @ScopeSiteId
  AND (s.RESERVATION_ID IS NULL OR EXISTS
    (SELECT 1 FROM dbo.RESERVATION r WHERE r.ID=s.RESERVATION_ID
     AND r.OrionCompanyId=@ScopeCompanyId AND r.OrionSiteId=@ScopeSiteId))
  AND s.ROOM_NAME IN @Rooms
  AND s.START_DATE < @EndDateExclusive
  AND s.END_DATE_EXCLUSIVE > @StartDate
ORDER BY s.ROOM_NAME, s.START_DATE, s.ID;
""";

    await using var connection = await OpenAsync(scope, ct);
    var rows = await connection.QueryAsync<OutlookRoomCalendarSyncMapping>(
      new CommandDefinition(
        sql,
        new
        {
          ScopeCompanyId = scope.CompanyId,
          ScopeSiteId = scope.SiteId,
          Rooms = roomNames,
          RoomCount = roomNames.Count,
          StartDate = startDate.Date,
          EndDateExclusive = endDateExclusive.Date
        },
        cancellationToken: ct));

    return rows.AsList();
  }

  public async Task UpsertMappingsAsync(
    HospitalityScope scope,
    IReadOnlyCollection<OutlookRoomCalendarSyncMappingUpsert> mappings,
    CancellationToken ct = default)
  {
    if (mappings is null || mappings.Count == 0)
    {
      return;
    }

    const string sql = """
DECLARE @RoomId int;
IF (SELECT COUNT_BIG(*) FROM dbo.ROOM
    WHERE OrionCompanyId=@ScopeCompanyId AND OrionSiteId=@ScopeSiteId AND ROOM_NAME=@RoomName) <> 1
  THROW 51040, 'Calendar mapping room does not belong uniquely to the active company/site.', 1;
SELECT @RoomId=ID FROM dbo.ROOM
WHERE OrionCompanyId=@ScopeCompanyId AND OrionSiteId=@ScopeSiteId AND ROOM_NAME=@RoomName;
IF @ReservationId IS NOT NULL AND NOT EXISTS
  (SELECT 1 FROM dbo.RESERVATION WHERE ID=@ReservationId
   AND OrionCompanyId=@ScopeCompanyId AND OrionSiteId=@ScopeSiteId)
  THROW 51041, 'Calendar mapping reservation is outside the active company/site.', 1;
MERGE dbo.ROOM_CALENDAR_OUTLOOK_SYNC AS target
USING (VALUES
  (@SourceKey, @RoomName, @ReservationId, @StartDate, @EndDateExclusive, @OutlookCalendarId, @OutlookEventId, @ContentHash)
) AS source
  (SOURCE_KEY, ROOM_NAME, RESERVATION_ID, START_DATE, END_DATE_EXCLUSIVE, OUTLOOK_CALENDAR_ID, OUTLOOK_EVENT_ID, CONTENT_HASH)
ON target.OrionCompanyId = @ScopeCompanyId
AND target.OrionSiteId = @ScopeSiteId
AND target.SOURCE_KEY = source.SOURCE_KEY
AND target.OUTLOOK_CALENDAR_ID = source.OUTLOOK_CALENDAR_ID
WHEN MATCHED THEN
  UPDATE SET
      RoomId = @RoomId,
      ROOM_NAME = source.ROOM_NAME,
      RESERVATION_ID = source.RESERVATION_ID,
      START_DATE = source.START_DATE,
      END_DATE_EXCLUSIVE = source.END_DATE_EXCLUSIVE,
      OUTLOOK_EVENT_ID = source.OUTLOOK_EVENT_ID,
      CONTENT_HASH = source.CONTENT_HASH,
      LAST_SYNCED_UTC = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
  INSERT
      (OrionCompanyId, OrionSiteId, RoomId, SOURCE_KEY, ROOM_NAME, RESERVATION_ID, START_DATE, END_DATE_EXCLUSIVE, OUTLOOK_CALENDAR_ID, OUTLOOK_EVENT_ID, CONTENT_HASH, LAST_SYNCED_UTC)
  VALUES
      (@ScopeCompanyId, @ScopeSiteId, @RoomId, source.SOURCE_KEY, source.ROOM_NAME, source.RESERVATION_ID, source.START_DATE, source.END_DATE_EXCLUSIVE, source.OUTLOOK_CALENDAR_ID, source.OUTLOOK_EVENT_ID, source.CONTENT_HASH, SYSUTCDATETIME());
""";

    await using var connection = await OpenAsync(scope, ct);
    await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct) as SqlTransaction;

    try
    {
      foreach (var mapping in mappings)
      {
        var parameters = new DynamicParameters(mapping);
        parameters.Add("ScopeCompanyId", scope.CompanyId);
        parameters.Add("ScopeSiteId", scope.SiteId);
        await connection.ExecuteAsync(
          new CommandDefinition(sql, parameters, transaction, cancellationToken: ct));
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
    HospitalityScope scope,
    IReadOnlyCollection<int> mappingIds,
    CancellationToken ct = default)
  {
    if (mappingIds is null || mappingIds.Count == 0)
    {
      return;
    }

    const string sql = "DELETE FROM dbo.ROOM_CALENDAR_OUTLOOK_SYNC WHERE OrionCompanyId = @ScopeCompanyId AND OrionSiteId = @ScopeSiteId AND ID IN @Ids;";

    await using var connection = await OpenAsync(scope, ct);
    await connection.ExecuteAsync(
      new CommandDefinition(sql, new { Ids = mappingIds, ScopeCompanyId = scope.CompanyId, ScopeSiteId = scope.SiteId }, cancellationToken: ct));
  }

  private async Task<SqlConnection> OpenAsync(HospitalityScope scope, CancellationToken ct)
  {
    ArgumentNullException.ThrowIfNull(scope);
    var authorizedScope = await _scopeAccessor.ResolveRequiredAsync(ct);
    if (scope != authorizedScope)
      throw new UnauthorizedAccessException("La empresa o sede de la operación de calendario cambió.");
    var connection = new SqlConnection(_connectionString);
    try
    {
      await connection.OpenAsync(ct);
      await HospitalityConnectionFactory.InitializeAsync(connection, scope, ct);
      return connection;
    }
    catch
    {
      await connection.DisposeAsync();
      throw;
    }
  }
}
