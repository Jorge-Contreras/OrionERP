using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Reservaciones;
using OrionERP.Infrastructure.Features.Reservaciones;

namespace OrionERP.Infrastructure.Features.Logistica.Shared;

/// <summary>
/// RFC is mandatory for logistics; Hospitality is optional for general locations.
/// A location and every ancestor must be authorized before copied names or stock
/// can leave SQL. The temporary visibility set belongs to this open connection.
/// </summary>
public static class LogisticsLocationScope
{
  public const string CurrentRfcSql = "CONVERT(varchar(50), SESSION_CONTEXT(N'OrionRfc'))";

  public static async Task<DbConnection> OpenAsync(IDbConnectionFactory factory,
    IHospitalityScopeAccessor? hospitalityScope, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(factory);
    HospitalityScope? scope = null;
    if (hospitalityScope is not null)
    {
      try { scope = await hospitalityScope.ResolveRequiredAsync(ct); }
      catch (UnauthorizedAccessException) { /* General logistics remains available. */ }
    }
    var connection = factory.Create() as DbConnection
      ?? throw new InvalidOperationException("Logística requiere una conexión de base de datos.");
    try
    {
      if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
      // OrionRfc was set from the authenticated current-company accessor by the
      // application factory. Never infer it from a room or the hospitality scope.
      await connection.ExecuteAsync(new CommandDefinition("""
        IF NULLIF(LTRIM(RTRIM(CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc')))),'') IS NULL
          OR CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'))='__UNSCOPED__'
          THROW 51930,'Selecciona una empresa autorizada antes de usar Logistica.',1;
        EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityCompanyId',@value=NULL;
        EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalitySiteId',@value=NULL;
        EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityRfc',@value=NULL;
        """, cancellationToken: ct));
      if (scope is not null)
      {
        await connection.ExecuteAsync(new CommandDefinition("""
          IF CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'))<>@CompanyRfc
            THROW 51931,'La empresa de Logistica y el alcance de Hospedaje no coinciden.',1;
          """, scope, cancellationToken: ct));
        if (connection is not SqlConnection sqlConnection)
          throw new InvalidOperationException("Hospedaje requiere SQL Server.");
        await HospitalityConnectionFactory.InitializeAsync(sqlConnection, scope, ct);
      }
      await RefreshAsync(connection, null, ct);
      return connection;
    }
    catch { await connection.DisposeAsync(); throw; }
  }

  public static string VisibilitySql(string locationAlias)
  {
    ValidateSqlReference(locationAlias);
    return ForLocationIdSql(locationAlias+".Id",locationAlias+".Rfc");
  }

  public static string ForLocationIdSql(string idExpression, string rfcExpression)
  {
    ValidateSqlReference(idExpression);
    ValidateSqlReference(rfcExpression);
    return $"EXISTS (SELECT 1 FROM #OrionVisibleLocations locationVisibility WHERE locationVisibility.LocationId={idExpression} AND locationVisibility.Rfc={rfcExpression})";
  }

  public static string RfcVisibilitySql(string rfcExpression)
  {
    ValidateSqlReference(rfcExpression);
    return $"{rfcExpression}={CurrentRfcSql}";
  }

  /// <summary>Refresh under transaction locks before mutation; never trust a stale read snapshot.</summary>
  public static async Task EnsureLocationAsync(DbConnection connection, DbTransaction? transaction,
    int locationId, CancellationToken ct = default)
  {
    if (locationId <= 0) throw new UnauthorizedAccessException("La ubicación no pertenece al alcance autorizado.");
    await RefreshAsync(connection, transaction, ct);
    await connection.ExecuteAsync(new CommandDefinition("""
      IF NOT EXISTS (SELECT 1 FROM #OrionVisibleLocations WHERE LocationId=@LocationId)
        THROW 51932,'La ubicacion no pertenece a la empresa y sede autorizadas.',1;
      """, new { LocationId=locationId }, transaction, cancellationToken:ct));
  }

  public static Task RefreshAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct = default)
    => connection.ExecuteAsync(new CommandDefinition(VisibilitySetSql, transaction:transaction, cancellationToken:ct));

  public static Task EnsureRfcAsync(DbConnection connection, DbTransaction? transaction, string rfc, CancellationToken ct = default)
    => connection.ExecuteAsync(new CommandDefinition("""
      IF NULLIF(LTRIM(RTRIM(@Rfc)),'') IS NULL
        OR NULLIF(CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc')),'') IS NULL
        OR @Rfc<>CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'))
        THROW 51935,'El RFC solicitado no coincide con la empresa autorizada de Logistica.',1;
      """,new { Rfc=rfc },transaction,cancellationToken:ct));

  private static void ValidateSqlReference(string value)
  {
    if (!Regex.IsMatch(value, "^[@A-Za-z_][A-Za-z0-9_.]*$", RegexOptions.CultureInvariant))
      throw new ArgumentException("SQL references must be trusted identifiers or parameter names.",nameof(value));
  }

  // A root must have no parent. Orphans/cycles therefore never enter this set.
  // HOLDLOCK revalidates and retains ancestor/room locks within a write transaction.
  // Excessively deep historical trees fail closed at SQL's recursion bound.
  private const string VisibilitySetSql = """
    IF OBJECT_ID('tempdb..#OrionVisibleLocations') IS NOT NULL DROP TABLE #OrionVisibleLocations;
    CREATE TABLE #OrionVisibleLocations(LocationId int NOT NULL PRIMARY KEY,Rfc varchar(50) NOT NULL);
    ;WITH Eligible AS (
      SELECT location.Id,location.Rfc,location.ParentLocationId
      FROM logistica.Location location WITH (HOLDLOCK)
      WHERE location.Rfc=CONVERT(varchar(50),SESSION_CONTEXT(N'OrionRfc'))
        AND (location.RoomId IS NULL OR EXISTS (
          SELECT 1 FROM dbo.ROOM room WITH (HOLDLOCK)
          WHERE room.ID=location.RoomId
            AND room.OrionCompanyId=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.HospitalityCompanyId'))
            AND room.OrionSiteId=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.HospitalitySiteId'))))
        AND (location.LegacyRoomId IS NULL OR EXISTS (
          SELECT 1 FROM dbo.ROOM legacyRoom WITH (HOLDLOCK)
          WHERE legacyRoom.ID=location.LegacyRoomId
            AND legacyRoom.OrionCompanyId=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.HospitalityCompanyId'))
            AND legacyRoom.OrionSiteId=TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.HospitalitySiteId'))))
    ),LocationTree AS (
      SELECT Id,Rfc FROM Eligible WHERE ParentLocationId IS NULL
      UNION ALL
      SELECT child.Id,child.Rfc FROM Eligible child
      JOIN LocationTree parent ON parent.Id=child.ParentLocationId AND parent.Rfc=child.Rfc
    )
    INSERT #OrionVisibleLocations(LocationId,Rfc)
    SELECT Id,Rfc FROM LocationTree OPTION(MAXRECURSION 100);
    """;
}
