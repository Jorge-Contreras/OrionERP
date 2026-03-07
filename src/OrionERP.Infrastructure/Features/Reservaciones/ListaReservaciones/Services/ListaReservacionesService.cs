using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.Infrastructure.Features.Reservaciones.ListaReservaciones.Services;

public sealed class ListaReservacionesService : IListaReservacionesService
{
  private readonly string _cs;
  private readonly ILogger<ListaReservacionesService> _logger;

  public ListaReservacionesService(IConfiguration cfg, ILogger<ListaReservacionesService> logger)
  {
    _cs = cfg.GetConnectionString("OrionDb")
      ?? throw new InvalidOperationException("Missing connection string: OrionDb");
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  public async Task<IReadOnlyList<ListaReservacionItemDto>> GetListaAsync(
      ListaReservacionFilter filter,
      CancellationToken ct = default)
  {
    filter ??= new ListaReservacionFilter();

    var sql = new StringBuilder(@"
SELECT
    r.ID AS Id,
    ISNULL(c.Nombre, '(Sin cliente)') AS Cliente,
    r.CHECKIN AS CheckIn,
    r.CHECKOUT AS CheckOut,
    r.STATUS AS Status,
    CAST(ISNULL(r.TOTAL_PRICE, 0) AS decimal(18,2)) AS TotalPrice,
    CAST(ISNULL(SUM(t.Monto), 0) AS decimal(18,2)) AS Pagado,
    CAST(ISNULL(r.TOTAL_PRICE, 0) - ISNULL(SUM(t.Monto), 0) AS decimal(18,2)) AS PorPagar,
    r.NOTES AS Notes
FROM dbo.RESERVATION r
LEFT JOIN dbo.Transacciones t
  ON TRY_CAST(t.Referencia AS int) = r.ID
LEFT JOIN dbo.Clientes c
  ON c.ID = r.CLIENTE_ID
WHERE 1=1");

    var p = new DynamicParameters();

    if (!filter.IncluirCanceladas)
    {
      sql.Append(" AND (r.STATUS <> 'Cancelada' OR r.STATUS IS NULL)");
    }

    if (filter.Id.HasValue)
    {
      sql.Append(" AND r.ID = @Id");
      p.Add("@Id", filter.Id.Value, DbType.Int32);
    }

    if (!string.IsNullOrWhiteSpace(filter.Cliente))
    {
      sql.Append(" AND c.Nombre LIKE @Cliente");
      p.Add("@Cliente", $"%{filter.Cliente.Trim()}%", DbType.String);
    }

    if (!string.IsNullOrWhiteSpace(filter.Status))
    {
      sql.Append(" AND r.STATUS LIKE @Status");
      p.Add("@Status", $"%{filter.Status.Trim()}%", DbType.String);
    }

    if (filter.CheckInFrom.HasValue)
    {
      sql.Append(" AND r.CHECKIN >= @CheckInFrom");
      p.Add("@CheckInFrom", filter.CheckInFrom.Value.Date, DbType.Date);
    }

    if (filter.CheckInTo.HasValue)
    {
      sql.Append(" AND r.CHECKIN < @CheckInTo");
      p.Add("@CheckInTo", filter.CheckInTo.Value.Date.AddDays(1), DbType.Date);
    }

    sql.Append(@"
GROUP BY
    r.ID,
    c.Nombre,
    r.CHECKIN,
    r.CHECKOUT,
    r.STATUS,
    r.TOTAL_PRICE,
    r.NOTES
ORDER BY r.CHECKIN DESC, r.ID DESC;");

    await using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<ListaReservacionItemDto>(
      new CommandDefinition(sql.ToString(), p, cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<int> CreateReservationAsync(ListaReservacionCreateRequest request, CancellationToken ct = default)
  {
    if (request is null)
      throw new ArgumentNullException(nameof(request));

    if (request.ClienteId <= 0)
      throw new ArgumentException("ClienteId must be greater than zero.", nameof(request));

    const string sql = @"
INSERT INTO dbo.RESERVATION (CLIENTE_ID, NOTES)
VALUES (@ClienteId, @Notes);
SELECT CAST(SCOPE_IDENTITY() AS int);";

    await using var conn = new SqlConnection(_cs);
    return await conn.ExecuteScalarAsync<int>(
      new CommandDefinition(
        sql,
        new
        {
          request.ClienteId,
          Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim()
        },
        cancellationToken: ct));
  }

  public async Task<ReservacionCommandResult> UpdateNotesAsync(int reservationId, string? notes, CancellationToken ct = default)
  {
    const string sql = @"
UPDATE dbo.RESERVATION
SET NOTES = @Notes
WHERE ID = @ReservationId;";

    await using var conn = new SqlConnection(_cs);
    var affected = await conn.ExecuteAsync(
      new CommandDefinition(
        sql,
        new
        {
          ReservationId = reservationId,
          Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()
        },
        cancellationToken: ct));

    return affected > 0
      ? ReservacionCommandResult.Ok("Notas actualizadas.")
      : ReservacionCommandResult.Fail("No se encontró la reservación.");
  }

  public async Task<ReservacionCommandResult> DeleteEmptyReservationsAsync(CancellationToken ct = default)
  {
    const string sql = @"
DELETE r
FROM dbo.RESERVATION AS r
WHERE NOT EXISTS (
        SELECT 1
        FROM dbo.ROOM_CALENDAR AS rc
        WHERE TRY_CAST(rc.LOCK_DESCRIPTION AS int) = r.ID
      )
  AND NOT EXISTS (
        SELECT 1
        FROM dbo.RESERVATION_DETAIL AS rd
        WHERE rd.RESERVATION_ID = r.ID
      )
  AND NOT EXISTS (
        SELECT 1
        FROM dbo.Reservation_Transacciones AS rt
        WHERE rt.ReservationID = r.ID
      );
SELECT @@ROWCOUNT;";

    try
    {
      await using var conn = new SqlConnection(_cs);
      var deleted = await conn.ExecuteScalarAsync<int>(new CommandDefinition(sql, cancellationToken: ct));
      return ReservacionCommandResult.Ok($"Se eliminaron {deleted} reservaciones vacías.");
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error deleting empty reservations.");
      return ReservacionCommandResult.Fail("No se pudieron eliminar las reservaciones vacías.");
    }
  }

  public async Task<ReservacionDetailDto?> GetReservacionDetailAsync(int reservationId, CancellationToken ct = default)
  {
    const string summarySql = @"
SELECT TOP (1)
    r.ID AS Id,
    r.CLIENTE_ID AS ClienteId,
    ISNULL(c.Nombre, '(Sin cliente)') AS Cliente,
    r.CHECKIN AS CheckIn,
    r.CHECKOUT AS CheckOut,
    r.STATUS AS Status,
    CAST(ISNULL(r.TOTAL_PRICE, 0) AS decimal(18,2)) AS TotalPrice,
    CAST(ISNULL(SUM(t.Monto), 0) AS decimal(18,2)) AS Pagado,
    CAST(ISNULL(r.TOTAL_PRICE, 0) - ISNULL(SUM(t.Monto), 0) AS decimal(18,2)) AS PorPagar,
    r.NOTES AS Notes
FROM dbo.RESERVATION r
LEFT JOIN dbo.Transacciones t
  ON TRY_CAST(t.Referencia AS int) = r.ID
LEFT JOIN dbo.Clientes c
  ON c.ID = r.CLIENTE_ID
WHERE r.ID = @ReservationId
GROUP BY
    r.ID,
    r.CLIENTE_ID,
    c.Nombre,
    r.CHECKIN,
    r.CHECKOUT,
    r.STATUS,
    r.TOTAL_PRICE,
    r.NOTES;";

    const string pagosSql = @"
SELECT
    t.ID AS TransaccionId,
    t.Fecha AS Fecha,
    ISNULL(t.Concepto, '') AS Concepto,
    CAST(ISNULL(t.Monto, 0) AS decimal(18,2)) AS Monto
FROM dbo.Reservation_Transacciones rt
INNER JOIN dbo.Transacciones t
  ON t.ID = rt.TransaccionID
WHERE rt.ReservationID = @ReservationId
ORDER BY t.Fecha DESC, t.ID DESC;";

    await using var conn = new SqlConnection(_cs);

    var detail = await conn.QueryFirstOrDefaultAsync<ReservacionDetailDto>(
      new CommandDefinition(summarySql, new { ReservationId = reservationId }, cancellationToken: ct));

    if (detail is null)
      return null;

    var pagos = await conn.QueryAsync<ReservacionPagoDto>(
      new CommandDefinition(pagosSql, new { ReservationId = reservationId }, cancellationToken: ct));

    detail.Pagos = pagos.ToList();
    return detail;
  }
}
