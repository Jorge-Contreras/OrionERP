using System.Data;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Locations;
using OrionERP.Application.Features.Logistica.Shared;

namespace OrionERP.Infrastructure.Features.Logistica.Locations;

public sealed class LocationService : ILocationService
{
  private readonly IDbConnectionFactory _connectionFactory;

  public LocationService(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  }

  public async Task<IReadOnlyList<LocationListItemDto>> GetLocationsAsync(LocationFilter filter, CancellationToken ct = default)
  {
    filter ??= new LocationFilter();

    var sql = new StringBuilder(
      """
      WITH ChildCounts AS (
          SELECT ParentLocationId, COUNT(*) AS ChildCount
          FROM logistica.Location
          WHERE ParentLocationId IS NOT NULL
          GROUP BY ParentLocationId
      ),
      MaterialCounts AS (
          SELECT LocationId, COUNT(*) AS MaterialCount
          FROM logistica.StockBalance
          GROUP BY LocationId
      )
      SELECT
          l.Id,
          l.LocationCode,
          l.LocationName,
          l.LocationType,
          l.ParentLocationId,
          parent.LocationName AS ParentLocationName,
          l.RoomId,
          room.ROOM_NAME AS RoomName,
          l.IsInventoryEnabled,
          l.IsActive,
          ISNULL(cc.ChildCount, 0) AS ChildCount,
          ISNULL(mc.MaterialCount, 0) AS MaterialCount
      FROM logistica.Location l
      LEFT JOIN logistica.Location parent
        ON parent.Id = l.ParentLocationId
      LEFT JOIN dbo.ROOM room
        ON room.ID = l.RoomId
      LEFT JOIN ChildCounts cc
        ON cc.ParentLocationId = l.Id
      LEFT JOIN MaterialCounts mc
        ON mc.LocationId = l.Id
      WHERE 1 = 1
      """);

    var parameters = new DynamicParameters();

    if (!filter.IncludeInactive)
    {
      sql.AppendLine(" AND l.IsActive = 1");
    }

    if (filter.RoomId.HasValue)
    {
      sql.AppendLine(" AND l.RoomId = @RoomId");
      parameters.Add("@RoomId", filter.RoomId.Value, DbType.Int32);
    }

    if (!string.IsNullOrWhiteSpace(filter.SearchText))
    {
      sql.AppendLine(" AND (l.LocationCode LIKE @Search OR l.LocationName LIKE @Search OR l.LocationType LIKE @Search OR room.ROOM_NAME LIKE @Search)");
      parameters.Add("@Search", $"%{filter.SearchText.Trim()}%", DbType.String);
    }

    sql.AppendLine("ORDER BY COALESCE(room.ROOM_NAME, l.LocationName), l.ParentLocationId, l.LocationName, l.Id;");

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<LocationListItemDto>(
      new CommandDefinition(sql.ToString(), parameters, cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<LocationDetailDto?> GetLocationAsync(int locationId, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          l.Id,
          l.LocationCode,
          l.LocationName,
          l.LocationType,
          l.ParentLocationId,
          l.RoomId,
          l.[Description],
          l.IsInventoryEnabled,
          l.IsActive,
          l.LegacyEspacioId,
          l.LegacyRoomId
      FROM logistica.Location l
      WHERE l.Id = @LocationId;
      """;

    using var conn = CreateConnection();
    return await conn.QueryFirstOrDefaultAsync<LocationDetailDto>(
      new CommandDefinition(sql, new { LocationId = locationId }, cancellationToken: ct));
  }

  public async Task<IReadOnlyList<LocationTreeNodeDto>> GetLocationTreeAsync(CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          l.Id,
          l.LocationCode,
          l.LocationName,
          l.LocationType,
          l.ParentLocationId,
          l.RoomId,
          l.IsInventoryEnabled
      FROM logistica.Location l
      WHERE l.IsActive = 1
      ORDER BY COALESCE(l.ParentLocationId, l.Id), l.LocationName, l.Id;
      """;

    using var conn = CreateConnection();
    var rows = (await conn.QueryAsync<LocationTreeRow>(
      new CommandDefinition(sql, cancellationToken: ct))).AsList();

    var byParent = rows.ToLookup(row => row.ParentLocationId);

    List<LocationTreeNodeDto> BuildNodes(int? parentId)
    {
      var children = byParent[parentId].ToList();
      if (children.Count == 0)
      {
        return [];
      }

      return children
        .OrderBy(child => child.LocationName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(child => child.Id)
        .Select(child => new LocationTreeNodeDto
        {
          Id = child.Id,
          LocationCode = child.LocationCode,
          LocationName = child.LocationName,
          LocationType = child.LocationType,
          RoomId = child.RoomId,
          IsInventoryEnabled = child.IsInventoryEnabled,
          Children = BuildNodes(child.Id)
        })
        .ToList();
    }

    return BuildNodes(null);
  }

  public async Task<IReadOnlyList<LookupOptionDto>> GetLocationLookupAsync(bool inventoryOnly = false, CancellationToken ct = default)
  {
    var sql = new StringBuilder(
      """
      SELECT
          l.Id,
          l.LocationName AS Name,
          l.LocationCode AS Code
      FROM logistica.Location l
      WHERE l.IsActive = 1
      """);

    if (inventoryOnly)
    {
      sql.AppendLine(" AND l.IsInventoryEnabled = 1");
    }

    sql.AppendLine("ORDER BY l.LocationName, l.Id;");

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<LookupOptionDto>(
      new CommandDefinition(sql.ToString(), cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<IReadOnlyList<LookupOptionDto>> GetRoomLookupAsync(string? roomType = null, CancellationToken ct = default)
  {
    var sql = new StringBuilder(
      """
      SELECT
          r.ID AS Id,
          r.ROOM_NAME AS Name,
          r.ROOM_TYPE AS Code
      FROM dbo.ROOM r
      WHERE 1 = 1
      """);

    var parameters = new DynamicParameters();
    if (!string.IsNullOrWhiteSpace(roomType))
    {
      sql.AppendLine(" AND r.ROOM_TYPE = @RoomType");
      parameters.Add("@RoomType", roomType.Trim(), DbType.String);
    }

    sql.AppendLine("ORDER BY r.ROOM_NAME, r.ID;");

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<LookupOptionDto>(
      new CommandDefinition(sql.ToString(), parameters, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<LogisticsCommandResult> SaveLocationAsync(LocationUpsertRequest request, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    var name = request.LocationName?.Trim();
    if (string.IsNullOrWhiteSpace(name))
    {
      return LogisticsCommandResult.Fail("El nombre de la ubicación es obligatorio.");
    }

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

    try
    {
      var locationId = request.Id ?? 0;

      if (request.Id.HasValue && request.Id.Value > 0)
      {
        const string updateSql =
          """
          UPDATE logistica.Location
          SET LocationCode = COALESCE(@LocationCode, LocationCode),
              LocationName = @LocationName,
              LocationType = @LocationType,
              ParentLocationId = @ParentLocationId,
              RoomId = @RoomId,
              [Description] = @Description,
              IsInventoryEnabled = @IsInventoryEnabled,
              IsActive = @IsActive,
              UpdatedAt = SYSUTCDATETIME()
          WHERE Id = @Id;
          """;

        await conn.ExecuteAsync(
          new CommandDefinition(
            updateSql,
            new
            {
              Id = request.Id.Value,
              LocationCode = NullIfWhiteSpace(request.LocationCode)?.ToUpperInvariant(),
              LocationName = name,
              request.LocationType,
              request.ParentLocationId,
              request.RoomId,
              Description = NullIfWhiteSpace(request.Description),
              request.IsInventoryEnabled,
              request.IsActive
            },
            tx,
            cancellationToken: ct));

        locationId = request.Id.Value;
      }
      else
      {
        const string insertSql =
          """
          INSERT INTO logistica.Location
          (
              LocationCode,
              LocationName,
              LocationType,
              ParentLocationId,
              RoomId,
              [Description],
              IsInventoryEnabled,
              IsActive
          )
          VALUES
          (
              CONCAT('TMP-', REPLACE(CONVERT(varchar(36), NEWID()), '-', '')),
              @LocationName,
              @LocationType,
              @ParentLocationId,
              @RoomId,
              @Description,
              @IsInventoryEnabled,
              @IsActive
          );

          SELECT CAST(SCOPE_IDENTITY() AS int);
          """;

        locationId = await conn.ExecuteScalarAsync<int>(
          new CommandDefinition(
            insertSql,
            new
            {
              LocationName = name,
              request.LocationType,
              request.ParentLocationId,
              request.RoomId,
              Description = NullIfWhiteSpace(request.Description),
              request.IsInventoryEnabled,
              request.IsActive
            },
            tx,
            cancellationToken: ct));

        await conn.ExecuteAsync(
          new CommandDefinition(
            """
            UPDATE logistica.Location
            SET LocationCode = @LocationCode
            WHERE Id = @LocationId;
            """,
            new
            {
              LocationId = locationId,
              LocationCode = string.IsNullOrWhiteSpace(request.LocationCode)
                ? $"LOC-{locationId:000000}"
                : request.LocationCode.Trim().ToUpperInvariant()
            },
            tx,
            cancellationToken: ct));
      }

      await tx.CommitAsync(ct);
      return LogisticsCommandResult.Ok($"Ubicación {name} guardada correctamente.", locationId);
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
      await tx.RollbackAsync(ct);
      return LogisticsCommandResult.Fail("Ya existe una ubicación con la misma clave interna.");
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  private SqlConnection CreateConnection()
    => _connectionFactory.Create() as SqlConnection
      ?? throw new InvalidOperationException("La fábrica de conexiones no devolvió una SqlConnection.");

  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private sealed class LocationTreeRow
  {
    public int Id { get; set; }
    public string LocationCode { get; set; } = string.Empty;
    public string LocationName { get; set; } = string.Empty;
    public string LocationType { get; set; } = string.Empty;
    public int? ParentLocationId { get; set; }
    public int? RoomId { get; set; }
    public bool IsInventoryEnabled { get; set; }
  }
}
