using System.Data;
using System.Data.Common;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Arrendadores;

namespace OrionERP.Infrastructure.Features.Arrendadores;

public sealed class ArrendadoresEstadoCuentaService : IArrendadoresEstadoCuentaService
{
  private readonly IDbConnectionFactory _connectionFactory;

  public ArrendadoresEstadoCuentaService(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory;
  }

  public async Task<IReadOnlyList<ArrendadorListItemDto>> GetArrendadoresAsync(
    string? searchText = null,
    int? ownerIdScope = null,
    CancellationToken ct = default)
  {
    using var connection = _connectionFactory.Create();
    await OpenConnectionAsync(connection, ct).ConfigureAwait(false);

    const string sql = """
SELECT
    p.id AS Id,
    p.RazonSocial,
    COUNT(r.ID) AS RoomCount
FROM dbo.Proveedores AS p
INNER JOIN dbo.ROOM AS r
    ON r.OWNER_ID = p.id
WHERE
    (@OwnerIdScope IS NULL OR p.id = @OwnerIdScope)
    AND (@SearchText IS NULL OR p.RazonSocial LIKE @SearchLike)
GROUP BY p.id, p.RazonSocial
ORDER BY p.RazonSocial;
""";

    var normalizedSearch = string.IsNullOrWhiteSpace(searchText) ? null : searchText.Trim();
    var rows = await connection.QueryAsync<ArrendadorListItemDto>(
      new CommandDefinition(
        sql,
        new
        {
          SearchText = normalizedSearch,
          SearchLike = $"%{normalizedSearch}%",
          OwnerIdScope = ownerIdScope
        },
        cancellationToken: ct)).ConfigureAwait(false);

    return rows.AsList();
  }

  public async Task<IReadOnlyList<ArrendadorRoomListItemDto>> GetRoomsAsync(
    int ownerId,
    int? ownerIdScope = null,
    CancellationToken ct = default)
  {
    using var connection = _connectionFactory.Create();
    await OpenConnectionAsync(connection, ct).ConfigureAwait(false);

    const string sql = """
SELECT
    r.ID AS RoomId,
    r.ROOM_NAME AS RoomName,
    r.ROOM_TYPE AS RoomType,
    CAST(r.BASE_PRICE AS decimal(18, 2)) AS BasePrice
FROM dbo.ROOM AS r
WHERE r.OWNER_ID = @OwnerId
  AND (@OwnerIdScope IS NULL OR r.OWNER_ID = @OwnerIdScope)
ORDER BY r.ROOM_NAME;
""";

    var rows = await connection.QueryAsync<ArrendadorRoomListItemDto>(
      new CommandDefinition(sql, new { OwnerId = ownerId, OwnerIdScope = ownerIdScope }, cancellationToken: ct)).ConfigureAwait(false);

    return rows.AsList();
  }

  public async Task<ArrendadorEstadoCuentaDto> GetEstadoCuentaAsync(
    int ownerId,
    int roomId,
    int year,
    int month,
    int? ownerIdScope = null,
    CancellationToken ct = default)
  {
    if (year < 2000 || year > 2100)
    {
      throw new ArgumentOutOfRangeException(nameof(year), "El anio debe estar entre 2000 y 2100.");
    }

    if (month < 1 || month > 12)
    {
      throw new ArgumentOutOfRangeException(nameof(month), "El mes debe estar entre 1 y 12.");
    }

    using var connection = _connectionFactory.Create();
    await OpenConnectionAsync(connection, ct).ConfigureAwait(false);

    var startDate = new DateTime(year, month, 1);
    var endDate = startDate.AddMonths(1);

    using var multi = await connection.QueryMultipleAsync(
      new CommandDefinition(
        EstadoCuentaSql,
        new
        {
          OwnerId = ownerId,
          RoomId = roomId,
          OwnerIdScope = ownerIdScope,
          Year = year,
          Month = month,
          StartDate = startDate,
          EndDate = endDate
        },
        cancellationToken: ct,
        commandTimeout: 60)).ConfigureAwait(false);

    var context = await multi.ReadSingleOrDefaultAsync<ArrendadorEstadoCuentaContextDto>().ConfigureAwait(false);
    var summary = await multi.ReadSingleOrDefaultAsync<ArrendadorEstadoCuentaResumenDto>().ConfigureAwait(false);
    var details = (await multi.ReadAsync<ArrendadorEstadoCuentaDetalleDto>().ConfigureAwait(false)).AsList();
    var exclusions = (await multi.ReadAsync<ArrendadorEstadoCuentaExclusionDto>().ConfigureAwait(false)).AsList();

    return new ArrendadorEstadoCuentaDto
    {
      Context = context,
      Summary = summary,
      Details = details,
      Exclusions = exclusions
    };
  }

  private static async Task OpenConnectionAsync(IDbConnection connection, CancellationToken ct)
  {
    if (connection is DbConnection dbConnection)
    {
      await dbConnection.OpenAsync(ct).ConfigureAwait(false);
      return;
    }

    connection.Open();
  }

  private const string EstadoCuentaSql = """
SELECT
    p.id AS OwnerId,
    p.RazonSocial,
    r.ID AS RoomId,
    r.ROOM_NAME AS RoomName,
    r.ROOM_TYPE AS RoomType,
    @Year AS [Year],
    @Month AS [Month]
FROM dbo.Proveedores AS p
INNER JOIN dbo.ROOM AS r
    ON r.OWNER_ID = p.id
WHERE p.id = @OwnerId
  AND r.ID = @RoomId
  AND (@OwnerIdScope IS NULL OR p.id = @OwnerIdScope);

WITH SelectedRoom AS (
    SELECT
        r.ID AS RoomId,
        r.ROOM_NAME AS RoomName
    FROM dbo.ROOM AS r
    WHERE r.ID = @RoomId
      AND r.OWNER_ID = @OwnerId
      AND (@OwnerIdScope IS NULL OR r.OWNER_ID = @OwnerIdScope)
),
NochesBase AS (
    SELECT
        rc.id AS RoomCalendarId,
        rc.ROOM_DATE AS Noche,
        rc.ROOM AS Casa,
        rc.IS_LOCKED AS IsLocked,
        rc.STATUS AS RoomCalendarStatus,
        rc.LOCKED_BY AS HuespedOBloqueo,
        rc.PRECIO AS Precio,
        rc.PORCENTAJE_ARRENDAMIENTO AS PorcentajeArrendamiento,
        TRY_CONVERT(int, NULLIF(LTRIM(RTRIM(rc.LOCK_DESCRIPTION)), '')) AS ReservationId
    FROM dbo.ROOM_CALENDAR AS rc
    INNER JOIN SelectedRoom AS sr
        ON sr.RoomName = rc.ROOM
    WHERE rc.ROOM_DATE >= @StartDate
      AND rc.ROOM_DATE < @EndDate
),
PagosContabilizados AS (
    SELECT
        rt.ReservationID,
        CAST(SUM(CAST(rt.Amount AS decimal(18, 2))) AS decimal(18, 2)) AS TotalPagadoContabilizado,
        COUNT(DISTINCT rt.TransaccionID) AS TransaccionesPago
    FROM dbo.Reservation_Transacciones AS rt
    INNER JOIN dbo.Transacciones AS t
        ON t.ID = rt.TransaccionID
    WHERE rt.Amount > 0
      AND EXISTS (
          SELECT 1
          FROM dbo.Registro_Contable AS rcnt
          WHERE rcnt.TransaccionID = rt.TransaccionID
      )
    GROUP BY rt.ReservationID
),
NochesPagadas AS (
    SELECT nb.*
    FROM NochesBase AS nb
    INNER JOIN dbo.RESERVATION AS r
        ON r.ID = nb.ReservationId
    INNER JOIN PagosContabilizados AS pc
        ON pc.ReservationID = r.ID
    WHERE nb.IsLocked = 1
      AND nb.Precio > 0
      AND pc.TotalPagadoContabilizado >= CAST(r.TOTAL_PRICE AS decimal(18, 2))
)
SELECT
    CONVERT(char(7), @StartDate, 120) AS Mes,
    COUNT(*) AS NochesOcupadas,
    CAST(COALESCE(SUM(CAST(np.Precio AS decimal(18, 4))), 0) AS decimal(18, 2)) AS Cobrado,
    CAST(COALESCE(SUM(CAST(np.Precio AS decimal(18, 4)) * np.PorcentajeArrendamiento), 0) AS decimal(18, 2)) AS Arrendador30,
    CAST(COALESCE(SUM(CAST(np.Precio AS decimal(18, 4)) * np.PorcentajeArrendamiento * 0.10), 0) AS decimal(18, 2)) AS Isr10,
    CAST(COALESCE(SUM(CAST(np.Precio AS decimal(18, 4)) * np.PorcentajeArrendamiento * 0.90), 0) AS decimal(18, 2)) AS PagoFinalArrendador
FROM NochesPagadas AS np;

WITH SelectedRoom AS (
    SELECT
        r.ID AS RoomId,
        r.ROOM_NAME AS RoomName
    FROM dbo.ROOM AS r
    WHERE r.ID = @RoomId
      AND r.OWNER_ID = @OwnerId
      AND (@OwnerIdScope IS NULL OR r.OWNER_ID = @OwnerIdScope)
),
NochesBase AS (
    SELECT
        rc.id AS RoomCalendarId,
        rc.ROOM_DATE AS Noche,
        rc.ROOM AS Casa,
        rc.IS_LOCKED AS IsLocked,
        rc.STATUS AS RoomCalendarStatus,
        rc.LOCKED_BY AS HuespedOBloqueo,
        rc.PRECIO AS Precio,
        rc.PORCENTAJE_ARRENDAMIENTO AS PorcentajeArrendamiento,
        TRY_CONVERT(int, NULLIF(LTRIM(RTRIM(rc.LOCK_DESCRIPTION)), '')) AS ReservationId
    FROM dbo.ROOM_CALENDAR AS rc
    INNER JOIN SelectedRoom AS sr
        ON sr.RoomName = rc.ROOM
    WHERE rc.ROOM_DATE >= @StartDate
      AND rc.ROOM_DATE < @EndDate
),
PagosContabilizados AS (
    SELECT
        rt.ReservationID,
        CAST(SUM(CAST(rt.Amount AS decimal(18, 2))) AS decimal(18, 2)) AS TotalPagadoContabilizado,
        COUNT(DISTINCT rt.TransaccionID) AS TransaccionesPago,
        MAX(t.Fecha) AS FechaUltimoPago
    FROM dbo.Reservation_Transacciones AS rt
    INNER JOIN dbo.Transacciones AS t
        ON t.ID = rt.TransaccionID
    WHERE rt.Amount > 0
      AND EXISTS (
          SELECT 1
          FROM dbo.Registro_Contable AS rcnt
          WHERE rcnt.TransaccionID = rt.TransaccionID
      )
    GROUP BY rt.ReservationID
)
SELECT
    nb.RoomCalendarId,
    nb.Noche,
    nb.Casa,
    nb.HuespedOBloqueo,
    nb.ReservationId,
    r.STATUS AS ReservationStatus,
    r.CHECKIN AS CheckIn,
    r.CHECKOUT AS CheckOut,
    CAST(r.TOTAL_PRICE AS decimal(18, 2)) AS ReservationTotal,
    pc.TotalPagadoContabilizado,
    pc.TransaccionesPago,
    pc.FechaUltimoPago,
    CAST(nb.Precio AS decimal(18, 2)) AS CobradoNoche,
    CAST(CAST(nb.Precio AS decimal(18, 4)) * nb.PorcentajeArrendamiento AS decimal(18, 2)) AS Arrendador30,
    CAST(CAST(nb.Precio AS decimal(18, 4)) * nb.PorcentajeArrendamiento * 0.10 AS decimal(18, 2)) AS Isr10,
    CAST(CAST(nb.Precio AS decimal(18, 4)) * nb.PorcentajeArrendamiento * 0.90 AS decimal(18, 2)) AS PagoFinalArrendador,
    LEFT(COALESCE(r.NOTES, ''), 300) AS ReservationNotes
FROM NochesBase AS nb
INNER JOIN dbo.RESERVATION AS r
    ON r.ID = nb.ReservationId
INNER JOIN PagosContabilizados AS pc
    ON pc.ReservationID = r.ID
WHERE nb.IsLocked = 1
  AND nb.Precio > 0
  AND pc.TotalPagadoContabilizado >= CAST(r.TOTAL_PRICE AS decimal(18, 2))
ORDER BY nb.Noche, nb.RoomCalendarId;

WITH SelectedRoom AS (
    SELECT
        r.ID AS RoomId,
        r.ROOM_NAME AS RoomName
    FROM dbo.ROOM AS r
    WHERE r.ID = @RoomId
      AND r.OWNER_ID = @OwnerId
      AND (@OwnerIdScope IS NULL OR r.OWNER_ID = @OwnerIdScope)
),
NochesBase AS (
    SELECT
        rc.id AS RoomCalendarId,
        rc.ROOM_DATE AS Noche,
        rc.ROOM AS Casa,
        rc.IS_LOCKED AS IsLocked,
        rc.STATUS AS RoomCalendarStatus,
        rc.LOCKED_BY AS HuespedOBloqueo,
        rc.PRECIO AS Precio,
        TRY_CONVERT(int, NULLIF(LTRIM(RTRIM(rc.LOCK_DESCRIPTION)), '')) AS ReservationId
    FROM dbo.ROOM_CALENDAR AS rc
    INNER JOIN SelectedRoom AS sr
        ON sr.RoomName = rc.ROOM
    WHERE rc.ROOM_DATE >= @StartDate
      AND rc.ROOM_DATE < @EndDate
      AND rc.IS_LOCKED = 1
),
PagosContabilizados AS (
    SELECT
        rt.ReservationID,
        CAST(SUM(CAST(rt.Amount AS decimal(18, 2))) AS decimal(18, 2)) AS TotalPagadoContabilizado,
        COUNT(DISTINCT rt.TransaccionID) AS TransaccionesPago
    FROM dbo.Reservation_Transacciones AS rt
    INNER JOIN dbo.Transacciones AS t
        ON t.ID = rt.TransaccionID
    WHERE rt.Amount > 0
      AND EXISTS (
          SELECT 1
          FROM dbo.Registro_Contable AS rcnt
          WHERE rcnt.TransaccionID = rt.TransaccionID
      )
    GROUP BY rt.ReservationID
)
SELECT
    nb.RoomCalendarId,
    nb.Noche,
    nb.Casa,
    nb.HuespedOBloqueo,
    nb.ReservationId,
    CAST(r.TOTAL_PRICE AS decimal(18, 2)) AS ReservationTotal,
    pc.TotalPagadoContabilizado,
    pc.TransaccionesPago,
    CAST(nb.Precio AS decimal(18, 2)) AS CobradoNoche,
    CASE
        WHEN nb.Precio <= 0 THEN 'PRECIO_CERO'
        WHEN nb.ReservationId IS NULL THEN 'SIN_RESERVATION_ID_EN_LOCK_DESCRIPTION'
        WHEN r.ID IS NULL THEN 'RESERVACION_NO_ENCONTRADA'
        WHEN pc.ReservationID IS NULL THEN 'SIN_PAGO_CONTABILIZADO'
        WHEN pc.TotalPagadoContabilizado < CAST(r.TOTAL_PRICE AS decimal(18, 2)) THEN 'PAGO_PARCIAL'
        ELSE 'INCLUIBLE'
    END AS MotivoExclusion
FROM NochesBase AS nb
LEFT JOIN dbo.RESERVATION AS r
    ON r.ID = nb.ReservationId
LEFT JOIN PagosContabilizados AS pc
    ON pc.ReservationID = nb.ReservationId
WHERE nb.Precio <= 0
   OR nb.ReservationId IS NULL
   OR r.ID IS NULL
   OR pc.ReservationID IS NULL
   OR pc.TotalPagadoContabilizado < CAST(r.TOTAL_PRICE AS decimal(18, 2))
ORDER BY nb.Noche, nb.RoomCalendarId;
""";
}
