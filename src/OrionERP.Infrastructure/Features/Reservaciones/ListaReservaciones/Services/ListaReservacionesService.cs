using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OrionERP.Application.Features.Reservaciones.Cfdi;
using OrionERP.Application.Features.Reservaciones.Extras;
using OrionERP.Application.Features.Reservaciones.OpenClaw;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.Infrastructure.Features.Reservaciones.ListaReservaciones.Services;

public sealed class ListaReservacionesService : IListaReservacionesService, IOpenClawReservationsService
{
  private const decimal IvaFactor = 1.16m;

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
    var skip = Math.Max(filter.Skip, 0);
    var take = Math.Max(filter.Take, 0);

    var p = new DynamicParameters();
    var sql = new StringBuilder(@"
SELECT
    r.ID AS Id,
    ISNULL(c.Nombre, '(Sin cliente)') AS Cliente,
    r.CHECKIN AS CheckIn,
    r.CHECKOUT AS CheckOut,
    r.STATUS AS Status,
    CAST(ISNULL(r.TOTAL_PRICE, 0) AS decimal(18,2)) AS TotalPrice,
    CAST(ISNULL(r.SUITE_DISCOUNT_PERCENT, 0) AS decimal(18,2)) AS SuiteDiscountPercent,
    CAST(0 AS decimal(18,2)) AS Pagado,
    CAST(ISNULL(r.TOTAL_PRICE, 0) AS decimal(18,2)) AS PorPagar,
    r.NOTES AS Notes
FROM dbo.RESERVATION r
LEFT JOIN dbo.Clientes c
  ON c.ID = r.CLIENTE_ID
WHERE 1=1");

    if (!filter.IncluirCanceladas)
    {
      sql.Append(" AND (r.STATUS <> @CanceladaStatus OR r.STATUS IS NULL)");
      p.Add("@CanceladaStatus", ReservationStatuses.Cancelada, DbType.String);
    }

    AppendListaFilters(
      sql,
      p,
      filter,
      "r.ID",
      "c.Nombre",
      "r.STATUS",
      "r.CHECKIN");

    sql.Append(@"
ORDER BY r.CHECKIN DESC, r.ID DESC");

    AppendListaPagination(sql, p, skip, take);
    sql.Append(';');

    await using var conn = new SqlConnection(_cs);
    var rows = (await conn.QueryAsync<ListaReservacionListRow>(
      new CommandDefinition(sql.ToString(), p, cancellationToken: ct))).AsList();

    await ApplyCalculatedListTotalsAsync(conn, rows, ct);

    return rows
      .Select(row => new ListaReservacionItemDto
      {
        Id = row.Id,
        Cliente = row.Cliente,
        CheckIn = row.CheckIn,
        CheckOut = row.CheckOut,
        Status = row.Status,
        TotalPrice = row.TotalPrice,
        Pagado = row.Pagado,
        PorPagar = row.PorPagar,
        FacturacionStatus = row.FacturacionStatus,
        FacturacionPaymentCount = row.FacturacionPaymentCount,
        FacturacionFacturadoPaymentCount = row.FacturacionFacturadoPaymentCount,
        FacturacionRegularCfdiCount = row.FacturacionRegularCfdiCount,
        FacturacionPago20Count = row.FacturacionPago20Count,
        Notes = row.Notes
      })
      .ToList();
  }

  private static async Task ApplyCalculatedListTotalsAsync(
    SqlConnection conn,
    IReadOnlyList<ListaReservacionListRow> rows,
    CancellationToken ct)
  {
    if (rows.Count == 0)
    {
      return;
    }

    var reservationIds = rows.Select(row => row.Id).ToArray();

    const string totalsSql = @"
SELECT
    TRY_CAST(rc.LOCK_DESCRIPTION AS int) AS ReservationId,
    CAST(ISNULL(rc.PRECIO, 0) AS decimal(18,2)) AS Amount
FROM dbo.ROOM_CALENDAR rc
WHERE TRY_CAST(rc.LOCK_DESCRIPTION AS int) IN @ReservationIds;

SELECT
    re.ReservationID AS ReservationId,
    CAST(ISNULL(re.UnitPriceSnapshot, 0) * ISNULL(re.Quantity, 1) AS decimal(18,2)) AS Amount
FROM dbo.Reservation_Extra re
WHERE re.ReservationID IN @ReservationIds;

SELECT
    rt.ReservationID AS ReservationId,
    CAST(ISNULL(rt.Amount, ISNULL(t.Monto, 0)) AS decimal(18,2)) AS Amount
FROM dbo.Reservation_Transacciones rt
LEFT JOIN dbo.Transacciones t
  ON t.ID = rt.TransaccionID
WHERE rt.ReservationID IN @ReservationIds;

WITH ReservationPayments AS
(
    SELECT DISTINCT
        rt.ReservationID AS ReservationId,
        rt.TransaccionID AS TransaccionId
    FROM dbo.Reservation_Transacciones rt
    WHERE rt.ReservationID IN @ReservationIds
),
Evidence AS
(
    SELECT
        rp.ReservationId,
        rp.TransaccionId,
        CAST('CFDI' AS varchar(20)) AS EvidenceType,
        CAST(c.Comprobante_Id AS bigint) AS ComprobanteId
    FROM ReservationPayments rp
    INNER JOIN dbo.Transaccion_Comprobante tc
      ON tc.Transaccion_ID = rp.TransaccionId
    INNER JOIN cfdi.Comprobante c
      ON c.Comprobante_Id = tc.Comprobante_ID
    WHERE ISNULL(c.TipoDeComprobante, '') <> 'P'
      AND c.FechaCancelacion IS NULL
      AND ISNULL(c.Estatus, '') NOT LIKE 'Cancel%'

    UNION ALL

    SELECT
        rp.ReservationId,
        rp.TransaccionId,
        CAST('Pago20' AS varchar(20)) AS EvidenceType,
        CAST(c.Comprobante_Id AS bigint) AS ComprobanteId
    FROM ReservationPayments rp
    INNER JOIN dbo.Transaccion_Comprobante tc
      ON tc.Transaccion_ID = rp.TransaccionId
    INNER JOIN cfdi.Comprobante c
      ON c.Comprobante_Id = tc.Comprobante_ID
    INNER JOIN cfdi.Pagos20 p20
      ON p20.Comprobante_Id = c.Comprobante_Id
    WHERE c.TipoDeComprobante = 'P'
      AND c.FechaCancelacion IS NULL
      AND ISNULL(c.Estatus, '') NOT LIKE 'Cancel%'

    UNION ALL

    SELECT
        rp.ReservationId,
        rp.TransaccionId,
        CAST('Pago20' AS varchar(20)) AS EvidenceType,
        CAST(c.Comprobante_Id AS bigint) AS ComprobanteId
    FROM ReservationPayments rp
    INNER JOIN dbo.Transaccion_DoctoRelacionado td
      ON td.Transaccion_ID = rp.TransaccionId
    INNER JOIN cfdi.Pagos20_DoctoRelacionado dr
      ON dr.DoctoRelacionado_Id = td.DoctoRelacionado_Id
    INNER JOIN cfdi.Pagos20_Pago p
      ON p.Pago_Id = dr.Pago_Id
    INNER JOIN cfdi.Pagos20 p20
      ON p20.Pagos20_Id = p.Pagos20_Id
    INNER JOIN cfdi.Comprobante c
      ON c.Comprobante_Id = p20.Comprobante_Id
    WHERE c.FechaCancelacion IS NULL
      AND ISNULL(c.Estatus, '') NOT LIKE 'Cancel%'
)
SELECT
    rp.ReservationId,
    COUNT(DISTINCT rp.TransaccionId) AS PaymentCount,
    COUNT(DISTINCT CASE WHEN e.TransaccionId IS NOT NULL THEN rp.TransaccionId END) AS FacturadoPaymentCount,
    COUNT(DISTINCT CASE WHEN e.EvidenceType = 'CFDI' THEN e.ComprobanteId END) AS RegularCfdiCount,
    COUNT(DISTINCT CASE WHEN e.EvidenceType = 'Pago20' THEN e.ComprobanteId END) AS Pago20Count
FROM ReservationPayments rp
LEFT JOIN Evidence e
  ON e.ReservationId = rp.ReservationId
 AND e.TransaccionId = rp.TransaccionId
GROUP BY rp.ReservationId;";

    using var multi = await conn.QueryMultipleAsync(
      new CommandDefinition(
        totalsSql,
        new { ReservationIds = reservationIds },
        cancellationToken: ct));

    var suitesByReservation = (await multi.ReadAsync<ReservationAmountRow>())
      .ToLookup(row => row.ReservationId, row => row.Amount);
    var extrasByReservation = (await multi.ReadAsync<ReservationAmountRow>())
      .ToLookup(row => row.ReservationId, row => row.Amount);
    var pagosByReservation = (await multi.ReadAsync<ReservationAmountRow>())
      .ToLookup(row => row.ReservationId, row => row.Amount);
    var facturacionByReservation = (await multi.ReadAsync<ReservationFacturacionListRow>())
      .ToDictionary(row => row.ReservationId);

    foreach (var row in rows)
    {
      var totals = ReservacionTotalsCalculator.Calculate(
        row.CheckIn,
        row.CheckOut,
        suitesByReservation[row.Id],
        extrasByReservation[row.Id],
        pagosByReservation[row.Id].Sum(),
        row.SuiteDiscountPercent);

      row.TotalPrice = totals.TotalReservacion;
      row.Pagado = totals.TotalPagado;
      row.PorPagar = totals.PorPagar;

      if (facturacionByReservation.TryGetValue(row.Id, out var facturacion))
      {
        row.FacturacionPaymentCount = facturacion.PaymentCount;
        row.FacturacionFacturadoPaymentCount = facturacion.FacturadoPaymentCount;
        row.FacturacionRegularCfdiCount = facturacion.RegularCfdiCount;
        row.FacturacionPago20Count = facturacion.Pago20Count;
        row.FacturacionStatus = ResolveFacturacionStatus(facturacion.PaymentCount, facturacion.FacturadoPaymentCount);
      }
    }
  }

  private static string ResolveFacturacionStatus(int paymentCount, int facturadoPaymentCount)
  {
    if (paymentCount <= 0 || facturadoPaymentCount <= 0)
    {
      return ReservationFacturacionStatuses.SinFacturar;
    }

    return facturadoPaymentCount == paymentCount
      ? ReservationFacturacionStatuses.Facturada
      : ReservationFacturacionStatuses.Parcial;
  }

  private static void AppendListaFilters(
    StringBuilder sql,
    DynamicParameters parameters,
    ListaReservacionFilter filter,
    string idColumn,
    string clienteColumn,
    string statusColumn,
    string checkInColumn)
  {
    if (filter.Id.HasValue)
    {
      sql.Append($" AND {idColumn} = @Id");
      parameters.Add("@Id", filter.Id.Value, DbType.Int32);
    }

    if (!string.IsNullOrWhiteSpace(filter.Cliente))
    {
      sql.Append($" AND {clienteColumn} LIKE @Cliente");
      parameters.Add("@Cliente", $"%{filter.Cliente.Trim()}%", DbType.String);
    }

    if (!string.IsNullOrWhiteSpace(filter.Status))
    {
      sql.Append($" AND {statusColumn} LIKE @Status");
      parameters.Add("@Status", $"%{filter.Status.Trim()}%", DbType.String);
    }

    if (filter.CheckInFrom.HasValue)
    {
      sql.Append($" AND {checkInColumn} >= @CheckInFrom");
      parameters.Add("@CheckInFrom", filter.CheckInFrom.Value.Date, DbType.Date);
    }

    if (filter.CheckInTo.HasValue)
    {
      sql.Append($" AND {checkInColumn} < @CheckInTo");
      parameters.Add("@CheckInTo", filter.CheckInTo.Value.Date.AddDays(1), DbType.Date);
    }
  }

  private static void AppendListaPagination(
    StringBuilder sql,
    DynamicParameters parameters,
    int skip,
    int take)
  {
    if (take <= 0)
    {
      return;
    }

    sql.Append(@"
OFFSET @Skip ROWS
FETCH NEXT @Take ROWS ONLY");
    parameters.Add("@Skip", skip, DbType.Int32);
    parameters.Add("@Take", take, DbType.Int32);
  }

  public async Task<int> CreateReservationAsync(ListaReservacionCreateRequest request, CancellationToken ct = default)
  {
    if (request is null)
      throw new ArgumentNullException(nameof(request));

    if (request.ClienteId <= 0)
      throw new ArgumentException("ClienteId must be greater than zero.", nameof(request));

    var status = ReservationStatuses.NormalizeOrDefault(request.Status);

    const string sql = @"
INSERT INTO dbo.RESERVATION (CLIENTE_ID, STATUS, NOTES)
VALUES (@ClienteId, @Status, @Notes);
SELECT CAST(SCOPE_IDENTITY() AS int);";

    await using var conn = new SqlConnection(_cs);
    return await conn.ExecuteScalarAsync<int>(
      new CommandDefinition(
        sql,
        new
        {
          request.ClienteId,
          Status = status,
          Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim()
        },
        cancellationToken: ct));
  }

  public async Task<OpenClawReservationCreateResult> CreateReservationAsync(OpenClawReservationCreateRequest request, CancellationToken ct = default)
  {
    if (request is null)
      throw new ArgumentNullException(nameof(request));

    var clientName = RequireValue(request.ClientName, "El nombre del cliente es obligatorio.");
    var status = ReservationStatuses.NormalizeOrDefault(request.Status);
    var recommendedBy = TrimOrNull(request.RecommendedBy);
    var reservationNotes = TrimOrNull(request.ReservationNotes);
    var requiresCfdi = request.RequiresCfdi ?? request.Taxable ?? true;
    var checkIn = request.CheckIn.ToDateTime(TimeOnly.MinValue);
    var checkOut = request.CheckOut.ToDateTime(TimeOnly.MinValue);

    if (checkOut <= checkIn)
      throw new OpenClawReservationValidationException("CHECKOUT debe ser posterior a CHECKIN.");

    var requestedSuites = NormalizeRequestedSuites(request.SuiteNames);
    if (requestedSuites.Count == 0)
      throw new OpenClawReservationValidationException("Debes indicar al menos una suite para crear la reservación.");

    if (request.GeneralDiscountPercent is < 0 or > 100)
      throw new OpenClawReservationValidationException("El descuento general debe estar entre 0 y 100.");

    var requestedExtras = AggregateExtraRequests(request.Extras);

    await using var conn = new SqlConnection(_cs);
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct) as SqlTransaction;

    try
    {
      var rooms = (await conn.QueryAsync<OpenClawRoomCatalogRow>(
        new CommandDefinition(
          """
SELECT
    r.ID AS Id,
    r.ROOM_NAME AS RoomName,
    r.ROOM_TYPE AS RoomType,
    CAST(ISNULL(r.BASE_PRICE, 0) AS decimal(18,2)) AS BasePrice
FROM dbo.ROOM r;
""",
          transaction: tx,
          cancellationToken: ct))).AsList();

      var extraCatalog = (await conn.QueryAsync<OpenClawExtraCatalogRow>(
        new CommandDefinition(
          """
SELECT
    e.ExtraID AS Id,
    e.[Name],
    e.[Description],
    CAST(ISNULL(e.Price, 0) AS decimal(18,2)) AS Price,
    CAST(ISNULL(e.IsActive, 0) AS bit) AS IsActive
FROM dbo.Extra e
WHERE e.IsActive = 1;
""",
          transaction: tx,
          cancellationToken: ct))).AsList();

      var resolvedSuites = ResolveRequestedRooms(requestedSuites, rooms, requireSuiteType: true);
      var resolvedExtras = ResolveRequestedExtras(requestedExtras, extraCatalog);

      var calendarRows = (await conn.QueryAsync<OpenClawRoomCalendarRow>(
        new CommandDefinition(
          """
SELECT
    rc.ID AS Id,
    rc.ROOM AS Room,
    rc.ROOM_DATE AS RoomDate,
    CAST(ISNULL(rc.IS_LOCKED, 0) AS bit) AS IsLocked,
    ISNULL(rc.LOCKED_BY, '') AS LockedBy,
    CAST(ISNULL(rc.PRECIO, 0) AS decimal(18,2)) AS Precio
FROM dbo.ROOM_CALENDAR rc WITH (UPDLOCK, HOLDLOCK)
WHERE rc.ROOM IN @Rooms
  AND rc.ROOM_DATE >= @CheckIn
  AND rc.ROOM_DATE < @CheckOut
ORDER BY rc.ROOM, rc.ROOM_DATE;
""",
          new
          {
            Rooms = resolvedSuites.Select(item => item.RoomName).ToArray(),
            CheckIn = checkIn.Date,
            CheckOut = checkOut.Date
          },
          tx,
          cancellationToken: ct))).AsList();

      ValidateCalendarRows(calendarRows, resolvedSuites.Select(item => item.RoomName).ToArray(), checkIn, checkOut);

      var cliente = await ResolveOrCreateClienteAsync(conn, tx, clientName, ct);
      var suiteSubtotal = decimal.Round(calendarRows.Sum(row => row.Precio), 2, MidpointRounding.ToEven);

      var createdExtras = resolvedExtras
        .Select(item => OpenClawReservationLineFactory.CreateExtra(item.Name, item.Quantity, item.UnitPrice, item.Notes))
        .ToList();

      var suiteDiscountPercent = request.GeneralDiscountPercent is > 0
        ? ReservacionTotalsCalculator.NormalizeSuiteDiscountPercent(request.GeneralDiscountPercent.Value)
        : 0m;

      const string insertReservationSql = """
INSERT INTO dbo.RESERVATION
(CLIENTE_ID, CHECKIN, CHECKOUT, STATUS, RECOMMENED_BY, NOTES, TAXABLE, TOTAL_PRICE, SUITE_DISCOUNT_PERCENT)
VALUES
(@ClienteId, @CheckIn, @CheckOut, @Status, @RecommenedBy, @Notes, @RequiresCfdi, @TotalPrice, @SuiteDiscountPercent);
SELECT CAST(SCOPE_IDENTITY() AS int);
""";

      var reservationId = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(
          insertReservationSql,
          new
          {
            ClienteId = cliente.Id,
            CheckIn = checkIn.Date,
            CheckOut = checkOut.Date,
            Status = status,
            RecommenedBy = recommendedBy,
            Notes = reservationNotes,
            RequiresCfdi = requiresCfdi,
            TotalPrice = 0m,
            SuiteDiscountPercent = suiteDiscountPercent
          },
          tx,
          cancellationToken: ct));

      var lockAffected = await conn.ExecuteAsync(
        new CommandDefinition(
          """
UPDATE dbo.ROOM_CALENDAR
SET
    IS_LOCKED = 1,
    LOCKED_BY = @LockedBy,
    LOCK_DESCRIPTION = @ReservationId,
    STATUS = @Status
WHERE ID IN @Ids;
""",
          new
          {
            LockedBy = cliente.Nombre,
            ReservationId = reservationId.ToString(CultureInfo.InvariantCulture),
            Status = status,
            Ids = calendarRows.Select(row => row.Id).ToArray()
          },
          tx,
          cancellationToken: ct));

      if (lockAffected != calendarRows.Count)
        throw new OpenClawReservationConflictException("No se pudieron bloquear todas las suites solicitadas.");

      if (createdExtras.Count > 0)
      {
        var extraParameters = resolvedExtras
          .Zip(createdExtras, (resolved, created) => new
          {
            ReservationId = reservationId,
            ExtraId = resolved.ExtraId,
            ExtraNameSnapshot = resolved.Name,
            ExtraDescriptionSnapshot = resolved.Description,
            UnitPriceSnapshot = created.UnitPrice,
            Quantity = created.Quantity,
            Notes = TrimOrNull(created.Notes)
          })
          .ToArray();

        await conn.ExecuteAsync(
          new CommandDefinition(
            """
INSERT INTO dbo.Reservation_Extra
(ReservationID, ExtraID, ExtraNameSnapshot, ExtraDescriptionSnapshot, UnitPriceSnapshot, Quantity, Notes)
VALUES
(@ReservationId, @ExtraId, @ExtraNameSnapshot, @ExtraDescriptionSnapshot, @UnitPriceSnapshot, @Quantity, @Notes);
""",
            extraParameters,
            tx,
            cancellationToken: ct));
      }

      var totals = ReservacionTotalsCalculator.Calculate(
        checkIn,
        checkOut,
        calendarRows.Select(row => row.Precio),
        createdExtras.Select(item => item.LinePrice),
        0m,
        suiteDiscountPercent);

      var totalAffected = await conn.ExecuteAsync(
        new CommandDefinition(
          """
UPDATE dbo.RESERVATION
SET TOTAL_PRICE = @TotalPrice
WHERE ID = @ReservationId;
""",
          new
          {
            ReservationId = reservationId,
            TotalPrice = totals.TotalReservacion
          },
          tx,
          cancellationToken: ct));

      if (totalAffected != 1)
        throw new OpenClawReservationConflictException("No se pudo finalizar la reservación creada.");

      await tx!.CommitAsync(ct);

      return new OpenClawReservationCreateResult
      {
        ReservationId = reservationId,
        ClientName = cliente.Nombre,
        CheckIn = request.CheckIn,
        CheckOut = request.CheckOut,
        Status = status,
        RequiresCfdi = requiresCfdi,
        SuiteNames = resolvedSuites.Select(item => item.RoomName).ToArray(),
        Extras = createdExtras,
        SuiteSubtotal = totals.TotalSuites,
        ExtrasSubtotal = totals.TotalExtras,
        TotalPrice = totals.TotalReservacion
      };
    }
    catch (Exception ex) when (ex is not OpenClawReservationValidationException && ex is not OpenClawReservationConflictException)
    {
      try { await tx!.RollbackAsync(ct); } catch { /* ignore */ }
      _logger.LogError(ex, "Error creating reservation from OpenClaw request.");
      throw;
    }
    catch
    {
      try { await tx!.RollbackAsync(ct); } catch { /* ignore */ }
      throw;
    }
  }

  public async Task<ClienteOptionDto?> GetDefaultClienteForNewReservationAsync(CancellationToken ct = default)
  {
    const string sql = @"
SELECT TOP (1)
    c.ID AS Id,
    c.Nombre AS Nombre
FROM dbo.Clientes c
WHERE c.Nombre LIKE '%COTIZAC%'
ORDER BY
    CASE
      WHEN UPPER(LTRIM(RTRIM(c.Nombre))) = 'COTIZACION' THEN 0
      WHEN UPPER(LTRIM(RTRIM(c.Nombre))) = 'CLIENTE COTIZACION' THEN 1
      WHEN UPPER(c.Nombre) LIKE 'COTIZACION%' THEN 2
      ELSE 3
    END,
    LEN(LTRIM(RTRIM(c.Nombre))),
    c.ID;";

    await using var conn = new SqlConnection(_cs);
    return await conn.QueryFirstOrDefaultAsync<ClienteOptionDto>(
      new CommandDefinition(sql, cancellationToken: ct));
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
SET NOCOUNT ON;

DECLARE @EmptyReservations TABLE
(
    ReservationId int NOT NULL PRIMARY KEY
);

;WITH ReferencedReservations AS (
    SELECT DISTINCT refs.ReservationId
    FROM (
        SELECT parsed.ReservationId
        FROM (
            SELECT TRY_CONVERT(int, rc.LOCK_DESCRIPTION) AS ReservationId
            FROM dbo.ROOM_CALENDAR AS rc
            WHERE rc.LOCK_DESCRIPTION IS NOT NULL
        ) AS parsed
        WHERE parsed.ReservationId IS NOT NULL

        UNION ALL

        SELECT re.ReservationID
        FROM dbo.Reservation_Extra AS re
        WHERE re.ReservationID IS NOT NULL

        UNION ALL

        SELECT rt.ReservationID
        FROM dbo.Reservation_Transacciones AS rt
        WHERE rt.ReservationID IS NOT NULL
    ) AS refs
)
INSERT INTO @EmptyReservations (ReservationId)
SELECT r.ID
FROM dbo.RESERVATION AS r
LEFT JOIN ReferencedReservations AS refs
  ON refs.ReservationId = r.ID
WHERE refs.ReservationId IS NULL;

IF OBJECT_ID('dbo.ReservationAirbnbBreakdown', 'U') IS NOT NULL
BEGIN
    DELETE b
    FROM dbo.ReservationAirbnbBreakdown AS b
    INNER JOIN @EmptyReservations AS empty
      ON empty.ReservationId = b.ReservationID;
END;

DELETE r
FROM dbo.RESERVATION AS r
INNER JOIN @EmptyReservations AS empty
  ON empty.ReservationId = r.ID;

SELECT @@ROWCOUNT;";

    try
    {
      await using var conn = new SqlConnection(_cs);
      await conn.OpenAsync(ct);

      await using var cmd = conn.CreateCommand();
      cmd.CommandText = sql;
      cmd.CommandType = CommandType.Text;

      var result = await cmd.ExecuteScalarAsync(ct);
      var deleted = result is null || result is DBNull
        ? 0
        : Convert.ToInt32(result, CultureInfo.InvariantCulture);

      return ReservacionCommandResult.Ok($"Se eliminaron {deleted} reservaciones vacías.");
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error deleting empty reservations.");
      return ReservacionCommandResult.Fail("No se pudieron eliminar las reservaciones vacías.");
    }
  }

  public Task<ReservacionDetailDto?> GetReservationDetailAsync(int reservationId, CancellationToken ct = default)
    => GetReservacionDetailAsync(reservationId, ct);

  public async Task<ReservacionCommandResult> DeleteReservationAsync(int reservationId, CancellationToken ct = default)
  {
    if (reservationId <= 0)
      return ReservacionCommandResult.Fail("Reservación inválida.");

    const string paymentCountSql = @"
SELECT COUNT(1)
FROM dbo.Reservation_Transacciones
WHERE ReservationID = @ReservationId;";

    const string attachmentCountSql = @"
SELECT COUNT(1)
FROM dbo.RESERVATION_ATTACHMENT
WHERE ReservationID = @ReservationId;";

    const string suiteIdsSql = @"
SELECT rc.ID
FROM dbo.ROOM_CALENDAR rc
WHERE TRY_CAST(rc.LOCK_DESCRIPTION AS int) = @ReservationId;";

    const string deleteActividadSql = @"
DELETE FROM dbo.Actividad
WHERE ID IN (
  SELECT ar.Actividad_ID
  FROM dbo.Actividad_RoomCalendar ar
  WHERE ar.RoomCalendar_ID IN @Ids
);";

    const string unlockSuitesSql = @"
UPDATE dbo.ROOM_CALENDAR
SET
    IS_LOCKED = 0,
    LOCKED_BY = '',
    LOCK_DESCRIPTION = '',
    STATUS = ''
WHERE ID IN @Ids;";

    const string deleteExtrasSql = @"
DELETE FROM dbo.Reservation_Extra
WHERE ReservationID = @ReservationId;";

    const string deleteAirbnbBreakdownSql = @"
IF OBJECT_ID('dbo.ReservationAirbnbBreakdown', 'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.ReservationAirbnbBreakdown
    WHERE ReservationID = @ReservationId;
END;";

    const string deleteReservationSql = @"
DELETE FROM dbo.RESERVATION
WHERE ID = @ReservationId;";

    await using var conn = new SqlConnection(_cs);
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct) as SqlTransaction;

    try
    {
      var paymentCount = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(paymentCountSql, new { ReservationId = reservationId }, tx, cancellationToken: ct));
      if (paymentCount > 0)
      {
        await tx!.RollbackAsync(ct);
        return ReservacionCommandResult.Fail("No se puede borrar la reservación porque tiene pagos registrados.");
      }

      var attachmentCount = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(attachmentCountSql, new { ReservationId = reservationId }, tx, cancellationToken: ct));
      if (attachmentCount > 0)
      {
        await tx!.RollbackAsync(ct);
        return ReservacionCommandResult.Fail("No se puede borrar la reservación porque tiene archivos adjuntos.");
      }

      var suiteIds = (await conn.QueryAsync<int>(
        new CommandDefinition(suiteIdsSql, new { ReservationId = reservationId }, tx, cancellationToken: ct))).ToArray();

      if (suiteIds.Length > 0)
      {
        await conn.ExecuteAsync(
          new CommandDefinition(deleteActividadSql, new { Ids = suiteIds }, tx, cancellationToken: ct));

        await conn.ExecuteAsync(
          new CommandDefinition(unlockSuitesSql, new { Ids = suiteIds }, tx, cancellationToken: ct));
      }

      await conn.ExecuteAsync(
        new CommandDefinition(deleteExtrasSql, new { ReservationId = reservationId }, tx, cancellationToken: ct));

      await conn.ExecuteAsync(
        new CommandDefinition(deleteAirbnbBreakdownSql, new { ReservationId = reservationId }, tx, cancellationToken: ct));

      var deleted = await conn.ExecuteAsync(
        new CommandDefinition(deleteReservationSql, new { ReservationId = reservationId }, tx, cancellationToken: ct));

      if (deleted == 0)
      {
        await tx!.RollbackAsync(ct);
        return ReservacionCommandResult.Fail("No se encontró la reservación.");
      }

      await tx!.CommitAsync(ct);
      return ReservacionCommandResult.Ok("Reservación borrada.");
    }
    catch (Exception ex)
    {
      try { await tx!.RollbackAsync(ct); } catch { /* ignore */ }
      _logger.LogError(ex, "Error deleting reservation {ReservationId}.", reservationId);
      return ReservacionCommandResult.Fail("No se pudo borrar la reservación.");
    }
  }

  public async Task<ReservacionDetailDto?> GetReservacionDetailAsync(int reservationId, CancellationToken ct = default)
  {
    const string detailSql = @"
SELECT TOP (1)
    r.ID AS Id,
    r.CLIENTE_ID AS ClienteId,
    ISNULL(c.Nombre, '(Sin cliente)') AS Cliente,
    r.CHECKIN AS CheckIn,
    r.CHECKOUT AS CheckOut,
    r.STATUS AS Status,
    r.RECOMMENED_BY AS RecommenedBy,
    CAST(ISNULL(r.TAXABLE, 0) AS bit) AS RequiresCfdi,
    CAST(ISNULL(r.TOTAL_PRICE, 0) AS decimal(18,2)) AS TotalPrice,
    CAST(ISNULL(r.SUITE_DISCOUNT_PERCENT, 0) AS decimal(18,2)) AS SuiteDiscountPercent,
    r.NOTES AS Notes
FROM dbo.RESERVATION r
LEFT JOIN dbo.Clientes c
  ON c.ID = r.CLIENTE_ID
WHERE r.ID = @ReservationId;

SELECT
    rc.ID AS Id,
    rc.ROOM_DATE AS Fecha,
    ISNULL(rc.ROOM, '') AS Suite,
    CAST(ISNULL(rc.PRECIO, 0) AS decimal(18,2)) AS Precio,
    rc.LOCK_DESCRIPTION AS LockDescription,
    CAST(ISNULL(rc.LIMPIEZA_PROFUNDA, 0) AS bit) AS LimpiezaProfunda
FROM dbo.ROOM_CALENDAR rc
WHERE TRY_CAST(rc.LOCK_DESCRIPTION AS int) = @ReservationId
ORDER BY rc.ROOM_DATE, rc.ROOM;

SELECT
    re.ReservationExtraID AS Id,
    re.ExtraID,
    ISNULL(re.ExtraNameSnapshot, '') AS [Name],
    re.ExtraDescriptionSnapshot AS [Description],
    CAST(ISNULL(re.UnitPriceSnapshot, 0) AS decimal(18,2)) AS UnitPrice,
    ISNULL(re.Quantity, 1) AS Quantity,
    CAST(ISNULL(re.UnitPriceSnapshot, 0) * ISNULL(re.Quantity, 1) AS decimal(18,2)) AS Price,
    re.Notes
FROM dbo.Reservation_Extra re
WHERE re.ReservationID = @ReservationId
ORDER BY re.ReservationExtraID;

SELECT
    rt.TransaccionID AS TransaccionId,
    t.Fecha AS Fecha,
    ISNULL(t.Concepto, '') AS Concepto,
    CAST(ISNULL(rt.Amount, ISNULL(t.Monto, 0)) AS decimal(18,2)) AS Monto
FROM dbo.Reservation_Transacciones rt
LEFT JOIN dbo.Transacciones t
  ON t.ID = rt.TransaccionID
WHERE rt.ReservationID = @ReservationId
ORDER BY t.Fecha DESC, rt.TransaccionID DESC;

SELECT
    ra.ID AS Id,
    ra.ReservationID AS ReservationId,
    ISNULL(ra.AttachmentName, CONCAT('Archivo ', ra.ID)) AS AttachmentName,
    ISNULL(ra.AttachmentExtension, '') AS AttachmentExtension,
    ra.AttachmentDescription AS AttachmentDescription,
    CAST(DATALENGTH(ra.Attachment) AS bigint) AS Length
FROM dbo.RESERVATION_ATTACHMENT ra
WHERE ra.ReservationID = @ReservationId
ORDER BY ra.ID DESC;

IF OBJECT_ID('dbo.ReservationAirbnbBreakdown', 'U') IS NOT NULL
BEGIN
    SELECT
        b.ReservationID AS ReservationId,
        b.PayoutAmount,
        b.TaxableBase,
        b.RoomRateAmount,
        b.CleaningFee,
        b.IvaTransferredAmount,
        b.IvaRetainedAmount,
        b.IsrRetainedAmount,
        b.HostServiceFeeBaseAmount,
        b.HostServiceFeeIvaAmount,
        b.HostServiceFeeTotalAmount,
        b.GrossCfdiTotal,
        b.IvaRate,
        b.IvaRetentionRate,
        b.IsrRetentionRate,
        b.HostServiceFeeRate,
        b.HostServiceFeeIvaRate,
        b.CreatedAtUtc,
        b.UpdatedAtUtc
    FROM dbo.ReservationAirbnbBreakdown b
    WHERE b.ReservationID = @ReservationId;
END
ELSE
BEGIN
    SELECT TOP (0)
        CAST(NULL AS int) AS ReservationId,
        CAST(NULL AS decimal(18,2)) AS PayoutAmount,
        CAST(NULL AS decimal(18,2)) AS TaxableBase,
        CAST(NULL AS decimal(18,2)) AS RoomRateAmount,
        CAST(NULL AS decimal(18,2)) AS CleaningFee,
        CAST(NULL AS decimal(18,2)) AS IvaTransferredAmount,
        CAST(NULL AS decimal(18,2)) AS IvaRetainedAmount,
        CAST(NULL AS decimal(18,2)) AS IsrRetainedAmount,
        CAST(NULL AS decimal(18,2)) AS HostServiceFeeBaseAmount,
        CAST(NULL AS decimal(18,2)) AS HostServiceFeeIvaAmount,
        CAST(NULL AS decimal(18,2)) AS HostServiceFeeTotalAmount,
        CAST(NULL AS decimal(18,2)) AS GrossCfdiTotal,
        CAST(NULL AS decimal(9,6)) AS IvaRate,
        CAST(NULL AS decimal(9,6)) AS IvaRetentionRate,
        CAST(NULL AS decimal(9,6)) AS IsrRetentionRate,
        CAST(NULL AS decimal(9,6)) AS HostServiceFeeRate,
        CAST(NULL AS decimal(9,6)) AS HostServiceFeeIvaRate,
        CAST(NULL AS datetime2) AS CreatedAtUtc,
        CAST(NULL AS datetime2) AS UpdatedAtUtc;
END;";

    await using var conn = new SqlConnection(_cs);
    await conn.OpenAsync(ct);
    using var multi = await conn.QueryMultipleAsync(
      new CommandDefinition(detailSql, new { ReservationId = reservationId }, cancellationToken: ct));

    var detail = await multi.ReadFirstOrDefaultAsync<ReservacionDetailDto>();

    if (detail is null)
      return null;

    var suites = (await multi.ReadAsync<ReservacionSuiteDto>()).AsList();
    var extras = (await multi.ReadAsync<ReservacionExtraDto>()).AsList();
    var pagos = (await multi.ReadAsync<ReservacionPagoDto>()).AsList();
    var attachments = (await multi.ReadAsync<ReservacionAttachmentDto>()).AsList();
    var airbnbBreakdown = await multi.ReadFirstOrDefaultAsync<AirbnbReservationBreakdownDto>();

    ApplyCalculatedTotals(detail, suites, extras, pagos);
    detail.Suites = suites;
    detail.Extras = extras;
    detail.Pagos = pagos;
    detail.Attachments = attachments;
    detail.AirbnbBreakdown = airbnbBreakdown;
    return detail;
  }

  public async Task<IReadOnlyList<ClienteOptionDto>> GetClientesAsync(string? searchText = null, int maxResults = 5, CancellationToken ct = default)
  {
    const string sql = @"
SELECT TOP (@MaxResults)
    c.ID AS Id,
    c.Nombre AS Nombre
FROM dbo.Clientes c
WHERE (@Search IS NULL OR c.Nombre LIKE @Search)
ORDER BY c.Nombre;";

    var take = maxResults <= 0 ? 5 : maxResults;

    await using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<ClienteOptionDto>(
      new CommandDefinition(
        sql,
        new
        {
          MaxResults = take,
          Search = string.IsNullOrWhiteSpace(searchText) ? null : $"%{searchText.Trim()}%"
        },
        cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<ClienteOptionDto?> ResolveClienteAsync(int? clienteId, string? clienteNombre, CancellationToken ct = default)
  {
    var normalizedName = NormalizeClienteNombre(clienteNombre);

    await using var conn = new SqlConnection(_cs);

    if (clienteId.HasValue && clienteId.Value > 0)
    {
      const string byIdSql = @"
SELECT TOP (1)
    c.ID AS Id,
    c.Nombre AS Nombre
FROM dbo.Clientes c
WHERE c.ID = @ClienteId;";

      var clientePorId = await conn.QueryFirstOrDefaultAsync<ClienteOptionDto>(
        new CommandDefinition(byIdSql, new { ClienteId = clienteId.Value }, cancellationToken: ct));

      if (clientePorId is not null)
      {
        return clientePorId;
      }
    }

    if (string.IsNullOrWhiteSpace(normalizedName))
    {
      return null;
    }

    const string byNameSql = @"
SELECT TOP (1)
    c.ID AS Id,
    c.Nombre AS Nombre
FROM dbo.Clientes c
WHERE UPPER(LTRIM(RTRIM(c.Nombre))) = UPPER(@Nombre)
ORDER BY c.ID;";

    return await conn.QueryFirstOrDefaultAsync<ClienteOptionDto>(
      new CommandDefinition(byNameSql, new { Nombre = normalizedName }, cancellationToken: ct));
  }

  public async Task<ClienteOptionDto> CreateClienteAsync(string clienteNombre, CancellationToken ct = default)
  {
    var normalizedName = NormalizeClienteNombre(clienteNombre);
    if (string.IsNullOrWhiteSpace(normalizedName))
      throw new ArgumentException("Cliente nombre is required.", nameof(clienteNombre));

    const string byNameSql = @"
SELECT TOP (1)
    c.ID AS Id,
    c.Nombre AS Nombre
FROM dbo.Clientes c
WHERE UPPER(LTRIM(RTRIM(c.Nombre))) = UPPER(@Nombre)
ORDER BY c.ID;";

    const string insertSql = @"
INSERT INTO dbo.Clientes (Nombre)
VALUES (@Nombre);
SELECT CAST(SCOPE_IDENTITY() AS int);";

    await using var conn = new SqlConnection(_cs);
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct) as SqlTransaction;

    try
    {
      var existing = await conn.QueryFirstOrDefaultAsync<ClienteOptionDto>(
        new CommandDefinition(byNameSql, new { Nombre = normalizedName }, tx, cancellationToken: ct));

      if (existing is not null)
      {
        await tx!.CommitAsync(ct);
        return existing;
      }

      var clienteId = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(insertSql, new { Nombre = normalizedName }, tx, cancellationToken: ct));

      await tx!.CommitAsync(ct);

      return new ClienteOptionDto
      {
        Id = clienteId,
        Nombre = normalizedName
      };
    }
    catch
    {
      try { await tx!.RollbackAsync(ct); } catch { /* ignore */ }
      throw;
    }
  }

  public async Task<IReadOnlyList<ExtraCatalogItemDto>> GetActiveExtraCatalogAsync(CancellationToken ct = default)
  {
    const string sql = @"
SELECT
    e.ExtraID AS ExtraId,
    e.[Name],
    e.[Description],
    CAST(ISNULL(e.Price, 0) AS decimal(18,2)) AS Price,
    CAST(ISNULL(e.IsActive, 0) AS bit) AS IsActive,
    e.LegacyRoomID AS LegacyRoomId,
    e.CreatedAtUtc,
    e.UpdatedAtUtc
FROM dbo.Extra e
WHERE e.IsActive = 1
ORDER BY e.[Name], e.ExtraID;";

    await using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<ExtraCatalogItemDto>(new CommandDefinition(sql, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<RoomCalendarTimelineDto> GetCalendarTimelineAsync(RoomCalendarTimelineFilter filter, CancellationToken ct = default)
  {
    if (filter is null)
      throw new ArgumentNullException(nameof(filter));

    var startDate = filter.StartDate.Date;
    var endDateExclusive = filter.EndDateExclusive.Date;
    if (endDateExclusive <= startDate)
      throw new ArgumentException("EndDateExclusive must be after StartDate.", nameof(filter));

    await using var conn = new SqlConnection(_cs);
    using var multi = await conn.QueryMultipleAsync(
      new CommandDefinition(
        "dbo.Calendar_GetRoomTimeline",
        new
        {
          StartDate = startDate,
          EndDateExclusive = endDateExclusive,
          RoomType = string.IsNullOrWhiteSpace(filter.RoomType) ? null : filter.RoomType.Trim()
        },
        commandType: CommandType.StoredProcedure,
        cancellationToken: ct));

    var resources = (await multi.ReadAsync<RoomCalendarResourceDto>()).AsList();
    var dayCells = (await multi.ReadAsync<RoomCalendarDayCellDto>()).AsList();
    var events = (await multi.ReadAsync<RoomCalendarEventDto>()).AsList();

    return new RoomCalendarTimelineDto
    {
      StartDate = startDate,
      EndDateExclusive = endDateExclusive,
      Resources = resources,
      DayCells = dayCells,
      Events = events
    };
  }

  public async Task<IReadOnlyList<ReservacionSuiteDto>> GetSuitesByReservationAsync(int reservationId, CancellationToken ct = default)
  {
    const string sql = @"
SELECT
    rc.ID AS Id,
    rc.ROOM_DATE AS Fecha,
    ISNULL(rc.ROOM, '') AS Suite,
    CAST(ISNULL(rc.PRECIO, 0) AS decimal(18,2)) AS Precio,
    rc.LOCK_DESCRIPTION AS LockDescription,
    CAST(ISNULL(rc.LIMPIEZA_PROFUNDA, 0) AS bit) AS LimpiezaProfunda
FROM dbo.ROOM_CALENDAR rc
WHERE TRY_CAST(rc.LOCK_DESCRIPTION AS int) = @ReservationId
ORDER BY rc.ROOM_DATE, rc.ROOM;";

    await using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<ReservacionSuiteDto>(
      new CommandDefinition(sql, new { ReservationId = reservationId }, cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<IReadOnlyList<SuiteDisponibleDto>> GetSuitesDisponiblesAsync(DateTime checkIn, DateTime checkOut, CancellationToken ct = default)
  {
    var checkInDate = checkIn.Date;
    var checkOutDate = checkOut.Date;
    if (checkOutDate <= checkInDate)
    {
      return Array.Empty<SuiteDisponibleDto>();
    }

    const string sql = @"
EXEC ROOMS_BY_DATE_AND_ROOM
     @INITIAL_DATE = @InitialDate,
     @FINAL_DATE = @FinalDate,
     @ROOM = @Room;";

    await using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync(
      new CommandDefinition(
        sql,
        new
        {
          InitialDate = checkInDate,
          FinalDate = checkOutDate.AddDays(-1),
          Room = "*"
        },
        cancellationToken: ct));

    var list = new List<SuiteDisponibleDto>();
    var mexicanCulture = CultureInfo.GetCultureInfo("es-MX");

    foreach (var row in rows)
    {
      if (row is not IDictionary<string, object> map)
      {
        continue;
      }

      var roomDate = ToDateTime(GetValue(map, "ROOM_DATE"));
      list.Add(new SuiteDisponibleDto
      {
        Id = ToInt(GetValue(map, "ID")),
        Dia = roomDate.HasValue ? mexicanCulture.TextInfo.ToTitleCase(roomDate.Value.ToString("dddd", mexicanCulture)) : string.Empty,
        IsLocked = ToBool(GetValue(map, "IS_LOCKED")),
        Suite = ToStringSafe(GetValue(map, "ROOM")),
        RoomDate = roomDate ?? DateTime.MinValue,
        LockedBy = ToStringSafe(GetValue(map, "LOCKED_BY")),
        LockDescription = ToStringSafe(GetValue(map, "LOCK_DESCRIPTION")),
        Precio = ToDecimal(GetValue(map, "PRECIO")),
        Status = ToStringSafe(GetValue(map, "STATUS")),
        VencimientoBloqueo = ToDateTime(GetValue(map, "VENCIMIENTO_BLOQUEO"))
      });
    }

    return list
      .OrderBy(r => r.Suite)
      .ThenBy(r => r.RoomDate)
      .ToList();
  }

  public async Task<IReadOnlyList<ReservacionExtraDto>> GetExtrasAsync(int reservationId, CancellationToken ct = default)
  {
    const string sql = @"
SELECT
    re.ReservationExtraID AS Id,
    re.ExtraID,
    ISNULL(re.ExtraNameSnapshot, '') AS [Name],
    re.ExtraDescriptionSnapshot AS [Description],
    CAST(ISNULL(re.UnitPriceSnapshot, 0) AS decimal(18,2)) AS UnitPrice,
    ISNULL(re.Quantity, 1) AS Quantity,
    CAST(ISNULL(re.UnitPriceSnapshot, 0) * ISNULL(re.Quantity, 1) AS decimal(18,2)) AS Price,
    re.Notes
FROM dbo.Reservation_Extra re
WHERE re.ReservationID = @ReservationId
ORDER BY re.ReservationExtraID;";

    await using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<ReservacionExtraDto>(
      new CommandDefinition(sql, new { ReservationId = reservationId }, cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<IReadOnlyList<ReservacionPagoDto>> GetPagosAsync(int reservationId, CancellationToken ct = default)
  {
    const string sql = @"
SELECT
    rt.TransaccionID AS TransaccionId,
    t.Fecha AS Fecha,
    ISNULL(t.Concepto, '') AS Concepto,
    CAST(ISNULL(rt.Amount, ISNULL(t.Monto, 0)) AS decimal(18,2)) AS Monto
FROM dbo.Reservation_Transacciones rt
LEFT JOIN dbo.Transacciones t
  ON t.ID = rt.TransaccionID
WHERE rt.ReservationID = @ReservationId
ORDER BY t.Fecha DESC, rt.TransaccionID DESC;";

    await using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<ReservacionPagoDto>(
      new CommandDefinition(sql, new { ReservationId = reservationId }, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<IReadOnlyList<ReservacionAttachmentDto>> GetAttachmentsAsync(int reservationId, CancellationToken ct = default)
  {
    const string sql = @"
SELECT
    ra.ID AS Id,
    ra.ReservationID AS ReservationId,
    ISNULL(ra.AttachmentName, CONCAT('Archivo ', ra.ID)) AS AttachmentName,
    ISNULL(ra.AttachmentExtension, '') AS AttachmentExtension,
    ra.AttachmentDescription AS AttachmentDescription,
    CAST(DATALENGTH(ra.Attachment) AS bigint) AS Length
FROM dbo.RESERVATION_ATTACHMENT ra
WHERE ra.ReservationID = @ReservationId
ORDER BY ra.ID DESC;";

    await using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<ReservacionAttachmentDto>(
      new CommandDefinition(sql, new { ReservationId = reservationId }, cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<ReservacionAttachmentDto> AddAttachmentAsync(ReservacionAttachmentCreateRequest request, CancellationToken ct = default)
  {
    if (request is null)
      throw new ArgumentNullException(nameof(request));

    if (request.ReservationId <= 0)
      throw new ArgumentException("ReservationId must be greater than zero.", nameof(request));

    if (request.Content is null || request.Content.Length == 0)
      throw new ArgumentException("El archivo adjunto no contiene datos.", nameof(request));

    if (request.Content.Length > ReservacionAttachmentCreateRequest.MaxFileSizeBytes)
      throw new InvalidOperationException("El archivo adjunto excede el tamaño máximo permitido (5 MB).");

    const string insertSql = @"
INSERT INTO dbo.RESERVATION_ATTACHMENT
(ReservationID, Attachment, AttachmentName, AttachmentExtension, AttachmentDescription)
VALUES
(@ReservationId, @Attachment, @AttachmentName, @AttachmentExtension, @AttachmentDescription);
SELECT CAST(SCOPE_IDENTITY() AS int);";

    const string selectSql = @"
SELECT
    ra.ID AS Id,
    ra.ReservationID AS ReservationId,
    ISNULL(ra.AttachmentName, CONCAT('Archivo ', ra.ID)) AS AttachmentName,
    ISNULL(ra.AttachmentExtension, '') AS AttachmentExtension,
    ra.AttachmentDescription AS AttachmentDescription,
    CAST(DATALENGTH(ra.Attachment) AS bigint) AS Length
FROM dbo.RESERVATION_ATTACHMENT ra
WHERE ra.ID = @AttachmentId;";

    await using var conn = new SqlConnection(_cs);
    var attachmentId = await conn.ExecuteScalarAsync<int>(
      new CommandDefinition(
        insertSql,
        new
        {
          request.ReservationId,
          Attachment = request.Content,
          AttachmentName = request.FileName,
          AttachmentExtension = string.IsNullOrWhiteSpace(request.Extension) ? string.Empty : request.Extension.Trim(),
          AttachmentDescription = string.IsNullOrWhiteSpace(request.Description) ? "Archivo adjunto" : request.Description
        },
        cancellationToken: ct));

    var dto = await conn.QueryFirstOrDefaultAsync<ReservacionAttachmentDto>(
      new CommandDefinition(selectSql, new { AttachmentId = attachmentId }, cancellationToken: ct));

    if (dto is null)
      throw new InvalidOperationException("No se pudo recuperar el adjunto creado.");

    return dto;
  }

  public async Task<ReservacionAttachmentContent?> GetAttachmentContentAsync(int attachmentId, CancellationToken ct = default)
  {
    const string sql = @"
SELECT TOP (1)
    ra.AttachmentName,
    ra.AttachmentExtension,
    ra.Attachment
FROM dbo.RESERVATION_ATTACHMENT ra
WHERE ra.ID = @AttachmentId;";

    await using var conn = new SqlConnection(_cs);
    var row = await conn.QueryFirstOrDefaultAsync<(string? AttachmentName, string? AttachmentExtension, byte[]? Attachment)>(
      new CommandDefinition(sql, new { AttachmentId = attachmentId }, cancellationToken: ct));

    if (row.Attachment is null || row.Attachment.Length == 0)
      return null;

    var fileName = string.IsNullOrWhiteSpace(row.AttachmentName) ? $"attachment-{attachmentId}" : row.AttachmentName;
    var ext = string.IsNullOrWhiteSpace(row.AttachmentExtension) ? string.Empty : row.AttachmentExtension.Trim();
    if (!string.IsNullOrWhiteSpace(ext) && !fileName.EndsWith($".{ext}", StringComparison.OrdinalIgnoreCase))
    {
      fileName = $"{fileName}.{ext}";
    }

    return new ReservacionAttachmentContent
    {
      AttachmentId = attachmentId,
      FileName = fileName,
      ContentType = ResolveContentType(ext),
      Bytes = row.Attachment
    };
  }

  public async Task DeleteAttachmentAsync(int attachmentId, CancellationToken ct = default)
  {
    const string sql = @"DELETE FROM dbo.RESERVATION_ATTACHMENT WHERE ID = @AttachmentId;";

    await using var conn = new SqlConnection(_cs);
    await conn.ExecuteAsync(new CommandDefinition(sql, new { AttachmentId = attachmentId }, cancellationToken: ct));
  }

  public async Task<ReservacionCommandResult> SaveReservationAsync(ReservacionUpdateRequest request, CancellationToken ct = default)
  {
    if (request is null)
      throw new ArgumentNullException(nameof(request));

    if (!request.ClienteId.HasValue || request.ClienteId.Value <= 0)
      return ReservacionCommandResult.Fail("Selecciona un cliente válido antes de guardar la reservación.");

    var cliente = await ResolveClienteAsync(request.ClienteId, null, ct);
    if (cliente is null)
      return ReservacionCommandResult.Fail("El cliente seleccionado ya no existe. Selecciona o crea un cliente antes de guardar.");

    if (request.SuiteDiscountPercent < 0m
        || request.SuiteDiscountPercent > 100m
        || (request.SuiteDiscountPercent > 0m && request.SuiteDiscountPercent <= 1m))
    {
      return ReservacionCommandResult.Fail("El descuento de suites debe ser 0, o mayor a 1% y menor o igual a 100%.");
    }

    var suiteDiscountPercent = ReservacionTotalsCalculator.NormalizeSuiteDiscountPercent(request.SuiteDiscountPercent);

    const string sql = @"
UPDATE dbo.RESERVATION
SET
    CLIENTE_ID = @ClienteId,
    CHECKIN = @CheckIn,
    CHECKOUT = @CheckOut,
    STATUS = @Status,
    RECOMMENED_BY = @RecommenedBy,
    NOTES = @Notes,
    TAXABLE = @RequiresCfdi,
    TOTAL_PRICE = @TotalPrice,
    SUITE_DISCOUNT_PERCENT = @SuiteDiscountPercent
WHERE ID = @Id;";

    await using var conn = new SqlConnection(_cs);
    var affected = await conn.ExecuteAsync(
      new CommandDefinition(
        sql,
        new
        {
          request.Id,
          request.ClienteId,
          request.CheckIn,
          request.CheckOut,
          Status = string.IsNullOrWhiteSpace(request.Status) ? null : request.Status.Trim(),
          RecommenedBy = string.IsNullOrWhiteSpace(request.RecommenedBy) ? null : request.RecommenedBy.Trim(),
          Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
          request.RequiresCfdi,
          request.TotalPrice,
          SuiteDiscountPercent = suiteDiscountPercent
        },
        cancellationToken: ct));

    return affected > 0
      ? ReservacionCommandResult.Ok("Reservación guardada correctamente.")
      : ReservacionCommandResult.Fail("No se pudo guardar la reservación.");
  }

  public async Task<ReservacionCommandResult> SyncSuiteStatusAsync(int reservationId, string? status, CancellationToken ct = default)
  {
    const string sql = @"
UPDATE dbo.ROOM_CALENDAR
SET STATUS = @Status
WHERE TRY_CAST(LOCK_DESCRIPTION AS int) = @ReservationId;";

    await using var conn = new SqlConnection(_cs);
    await conn.ExecuteAsync(
      new CommandDefinition(
        sql,
        new
        {
          ReservationId = reservationId,
          Status = string.IsNullOrWhiteSpace(status) ? string.Empty : status.Trim()
        },
        cancellationToken: ct));

    return ReservacionCommandResult.Ok("Estatus de suites sincronizado.");
  }

  public async Task<ReservacionCommandResult> SyncSuiteLockedByAsync(int reservationId, int? clienteId, CancellationToken ct = default)
  {
    var clienteNombre = string.Empty;
    if (clienteId.HasValue)
    {
      const string clienteSql = @"SELECT TOP (1) Nombre FROM dbo.Clientes WHERE ID = @ClienteId;";
      await using var connCliente = new SqlConnection(_cs);
      clienteNombre = (await connCliente.ExecuteScalarAsync<string?>(
        new CommandDefinition(clienteSql, new { ClienteId = clienteId.Value }, cancellationToken: ct))) ?? string.Empty;
    }

    const string sql = @"
UPDATE dbo.ROOM_CALENDAR
SET LOCKED_BY = @ClienteNombre
WHERE TRY_CAST(LOCK_DESCRIPTION AS int) = @ReservationId;";

    await using var conn = new SqlConnection(_cs);
    await conn.ExecuteAsync(
      new CommandDefinition(sql, new { ClienteNombre = clienteNombre, ReservationId = reservationId }, cancellationToken: ct));

    return ReservacionCommandResult.Ok("Cliente sincronizado en suites.");
  }

  public async Task<ReservacionCommandResult> AddSuitesToReservationAsync(int reservationId, string? status, string? clienteNombre, IReadOnlyCollection<int> roomCalendarIds, CancellationToken ct = default)
  {
    if (roomCalendarIds is null || roomCalendarIds.Count == 0)
      return ReservacionCommandResult.Fail("Selecciona al menos una fecha/suite para agregar.");

    const string validateSql = @"
SELECT COUNT(1)
FROM dbo.ROOM_CALENDAR
WHERE ID IN @Ids
  AND IS_LOCKED = 1;";

    const string updateSql = @"
UPDATE dbo.ROOM_CALENDAR
SET
    IS_LOCKED = 1,
    LOCKED_BY = @LockedBy,
    LOCK_DESCRIPTION = @ReservationId,
    STATUS = @Status
WHERE ID IN @Ids;";

    await using var conn = new SqlConnection(_cs);
    await conn.OpenAsync(ct);

    var blocked = await conn.ExecuteScalarAsync<int>(
      new CommandDefinition(validateSql, new { Ids = roomCalendarIds.ToArray() }, cancellationToken: ct));

    if (blocked > 0)
      return ReservacionCommandResult.Fail("Una o más suites seleccionadas ya están bloqueadas.");

    var affected = await conn.ExecuteAsync(
      new CommandDefinition(
        updateSql,
        new
        {
          Ids = roomCalendarIds.ToArray(),
          LockedBy = string.IsNullOrWhiteSpace(clienteNombre) ? string.Empty : clienteNombre.Trim(),
          ReservationId = reservationId.ToString(CultureInfo.InvariantCulture),
          Status = string.IsNullOrWhiteSpace(status) ? string.Empty : status.Trim()
        },
        cancellationToken: ct));

    return ReservacionCommandResult.Ok($"Se agregaron {affected} suites a la reservación.");
  }

  public async Task<ReservacionCommandResult> DeleteSuitesAsync(IReadOnlyCollection<int> roomCalendarIds, CancellationToken ct = default)
  {
    if (roomCalendarIds is null || roomCalendarIds.Count == 0)
      return ReservacionCommandResult.Fail("Selecciona al menos una suite para eliminar.");

    const string unlockSql = @"
UPDATE dbo.ROOM_CALENDAR
SET
    IS_LOCKED = 0,
    LOCKED_BY = '',
    LOCK_DESCRIPTION = '',
    STATUS = ''
WHERE ID IN @Ids;";

    const string deleteActividadSql = @"
DELETE FROM dbo.Actividad
WHERE ID IN (
  SELECT ar.Actividad_ID
  FROM dbo.Actividad_RoomCalendar ar
  WHERE ar.RoomCalendar_ID IN @Ids
);";

    await using var conn = new SqlConnection(_cs);
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct) as SqlTransaction;

    try
    {
      var affected = await conn.ExecuteAsync(
        new CommandDefinition(unlockSql, new { Ids = roomCalendarIds.ToArray() }, tx, cancellationToken: ct));

      await conn.ExecuteAsync(
        new CommandDefinition(deleteActividadSql, new { Ids = roomCalendarIds.ToArray() }, tx, cancellationToken: ct));

      await tx!.CommitAsync(ct);
      return ReservacionCommandResult.Ok($"Se quitaron {affected} suites de la reservación.");
    }
    catch (Exception ex)
    {
      try { await tx!.RollbackAsync(ct); } catch { /* ignore */ }
      _logger.LogError(ex, "Error deleting suites from reservation.");
      return ReservacionCommandResult.Fail("No se pudieron eliminar las suites seleccionadas.");
    }
  }

  public async Task<ReservacionCommandResult> SetSuitesPriceAsync(IReadOnlyCollection<int> roomCalendarIds, decimal price, CancellationToken ct = default)
  {
    if (roomCalendarIds is null || roomCalendarIds.Count == 0)
      return ReservacionCommandResult.Fail("Selecciona al menos una suite.");

    const string sql = @"UPDATE dbo.ROOM_CALENDAR SET PRECIO = @Price WHERE ID IN @Ids;";

    await using var conn = new SqlConnection(_cs);
    var affected = await conn.ExecuteAsync(
      new CommandDefinition(sql, new { Price = price, Ids = roomCalendarIds.ToArray() }, cancellationToken: ct));

    return ReservacionCommandResult.Ok($"Precio actualizado para {affected} suites.");
  }

  public async Task<ReservacionCommandResult> SetSuitesPriceWithIvaAsync(IReadOnlyCollection<int> roomCalendarIds, decimal priceWithIva, CancellationToken ct = default)
  {
    var priceWithoutIva = decimal.Round(priceWithIva / 1.16m, 2, MidpointRounding.ToEven);
    return await SetSuitesPriceAsync(roomCalendarIds, priceWithoutIva, ct);
  }

  public async Task<ReservacionCommandResult> ToggleSuitesLimpiezaAsync(IReadOnlyCollection<int> roomCalendarIds, bool nextState, CancellationToken ct = default)
  {
    if (roomCalendarIds is null || roomCalendarIds.Count == 0)
      return ReservacionCommandResult.Fail("Selecciona al menos una suite.");

    const string sql = @"UPDATE dbo.ROOM_CALENDAR SET Limpieza_Profunda = @State WHERE ID IN @Ids;";

    await using var conn = new SqlConnection(_cs);
    var affected = await conn.ExecuteAsync(
      new CommandDefinition(sql, new { State = nextState, Ids = roomCalendarIds.ToArray() }, cancellationToken: ct));

    return ReservacionCommandResult.Ok($"Limpieza profunda actualizada para {affected} suites.");
  }

  public async Task<ReservacionCommandResult> DistributeSuitesTotalWithIvaAsync(IReadOnlyCollection<int> roomCalendarIds, decimal totalWithIva, CancellationToken ct = default)
  {
    if (roomCalendarIds is null || roomCalendarIds.Count == 0)
      return ReservacionCommandResult.Fail("Selecciona al menos una suite.");

    if (totalWithIva < 0)
      return ReservacionCommandResult.Fail("El total debe ser mayor o igual a cero.");

    const string sql = @"UPDATE dbo.ROOM_CALENDAR SET PRECIO = @Precio WHERE ID = @Id;";
    var ids = roomCalendarIds.Distinct().ToArray();
    var grossAmounts = SplitCurrency(totalWithIva, ids.Length);

    await using var conn = new SqlConnection(_cs);
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct) as SqlTransaction;

    try
    {
      var affected = 0;
      for (var i = 0; i < ids.Length; i++)
      {
        var priceWithoutIva = decimal.Round(grossAmounts[i] / IvaFactor, 2, MidpointRounding.ToEven);
        affected += await conn.ExecuteAsync(
          new CommandDefinition(sql, new { Precio = priceWithoutIva, Id = ids[i] }, tx, cancellationToken: ct));
      }

      await tx!.CommitAsync(ct);
      return ReservacionCommandResult.Ok($"Total con IVA distribuido en {affected} suites.");
    }
    catch (Exception ex)
    {
      try { await tx!.RollbackAsync(ct); } catch { /* ignore */ }
      _logger.LogError(ex, "Error distributing suite total with IVA.");
      return ReservacionCommandResult.Fail("No se pudo distribuir el total en las suites seleccionadas.");
    }
  }

  public async Task<ReservacionCommandResult> ApplyAirbnbBreakdownAsync(AirbnbReservationBreakdownApplyRequest request, CancellationToken ct = default)
  {
    if (request is null)
      throw new ArgumentNullException(nameof(request));

    if (request.ReservationId <= 0)
      return ReservacionCommandResult.Fail("Selecciona una reservación válida.");

    AirbnbReservationBreakdownDto breakdown;
    try
    {
      breakdown = AirbnbReservationBreakdownCalculator.Calculate(request);
    }
    catch (Exception ex)
    {
      return ReservacionCommandResult.Fail($"No se pudo calcular el desglose Airbnb. {ex.Message}");
    }

    if (!breakdown.IsBalanced)
    {
      return ReservacionCommandResult.Fail("El desglose Airbnb no genera una póliza balanceada.");
    }

    const string tableExistsSql = @"SELECT OBJECT_ID('dbo.ReservationAirbnbBreakdown', 'U');";
    const string reservationExistsSql = @"SELECT TOP (1) 1 FROM dbo.RESERVATION WITH (UPDLOCK) WHERE ID = @ReservationId;";
    const string extrasCountSql = @"SELECT COUNT(1) FROM dbo.Reservation_Extra WHERE ReservationID = @ReservationId;";
    const string selectedSuitesSql = @"
SELECT
    rc.ID AS Id
FROM dbo.ROOM_CALENDAR rc WITH (UPDLOCK)
WHERE TRY_CAST(rc.LOCK_DESCRIPTION AS int) = @ReservationId
  AND rc.ID IN @Ids
ORDER BY rc.ROOM_DATE, rc.ROOM, rc.ID;";
    const string allSuitesSql = @"
SELECT
    rc.ID AS Id
FROM dbo.ROOM_CALENDAR rc WITH (UPDLOCK)
WHERE TRY_CAST(rc.LOCK_DESCRIPTION AS int) = @ReservationId
ORDER BY rc.ROOM_DATE, rc.ROOM, rc.ID;";
    const string updateSuiteSql = @"UPDATE dbo.ROOM_CALENDAR SET PRECIO = @Precio WHERE ID = @Id;";
    const string updateReservationSql = @"
UPDATE dbo.RESERVATION
SET TAXABLE = 1,
    TOTAL_PRICE = @TotalPrice,
    SUITE_DISCOUNT_PERCENT = 0,
    AIRBNB_UPDATED = 1
WHERE ID = @ReservationId;";
    const string upsertBreakdownSql = @"
UPDATE dbo.ReservationAirbnbBreakdown
SET PayoutAmount = @PayoutAmount,
    TaxableBase = @TaxableBase,
    RoomRateAmount = @RoomRateAmount,
    CleaningFee = @CleaningFee,
    IvaTransferredAmount = @IvaTransferredAmount,
    IvaRetainedAmount = @IvaRetainedAmount,
    IsrRetainedAmount = @IsrRetainedAmount,
    HostServiceFeeBaseAmount = @HostServiceFeeBaseAmount,
    HostServiceFeeIvaAmount = @HostServiceFeeIvaAmount,
    HostServiceFeeTotalAmount = @HostServiceFeeTotalAmount,
    GrossCfdiTotal = @GrossCfdiTotal,
    IvaRate = @IvaRate,
    IvaRetentionRate = @IvaRetentionRate,
    IsrRetentionRate = @IsrRetentionRate,
    HostServiceFeeRate = @HostServiceFeeRate,
    HostServiceFeeIvaRate = @HostServiceFeeIvaRate,
    UsedDefaultRates = @UsedDefaultRates,
    UpdatedAtUtc = SYSUTCDATETIME()
WHERE ReservationID = @ReservationId;

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO dbo.ReservationAirbnbBreakdown
    (
        ReservationID,
        PayoutAmount,
        TaxableBase,
        RoomRateAmount,
        CleaningFee,
        IvaTransferredAmount,
        IvaRetainedAmount,
        IsrRetainedAmount,
        HostServiceFeeBaseAmount,
        HostServiceFeeIvaAmount,
        HostServiceFeeTotalAmount,
        GrossCfdiTotal,
        IvaRate,
        IvaRetentionRate,
        IsrRetentionRate,
        HostServiceFeeRate,
        HostServiceFeeIvaRate,
        UsedDefaultRates
    )
    VALUES
    (
        @ReservationId,
        @PayoutAmount,
        @TaxableBase,
        @RoomRateAmount,
        @CleaningFee,
        @IvaTransferredAmount,
        @IvaRetainedAmount,
        @IsrRetainedAmount,
        @HostServiceFeeBaseAmount,
        @HostServiceFeeIvaAmount,
        @HostServiceFeeTotalAmount,
        @GrossCfdiTotal,
        @IvaRate,
        @IvaRetentionRate,
        @IsrRetentionRate,
        @HostServiceFeeRate,
        @HostServiceFeeIvaRate,
        @UsedDefaultRates
    );
END;";

    await using var conn = new SqlConnection(_cs);
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct) as SqlTransaction;

    try
    {
      var tableExists = await conn.ExecuteScalarAsync<int?>(
        new CommandDefinition(tableExistsSql, transaction: tx, cancellationToken: ct));
      if (!tableExists.HasValue)
      {
        await tx!.RollbackAsync(ct);
        return ReservacionCommandResult.Fail("Falta ejecutar el script de base de datos para dbo.ReservationAirbnbBreakdown.");
      }

      var reservationExists = await conn.ExecuteScalarAsync<int?>(
        new CommandDefinition(reservationExistsSql, new { request.ReservationId }, tx, cancellationToken: ct));
      if (!reservationExists.HasValue)
      {
        await tx!.RollbackAsync(ct);
        return ReservacionCommandResult.Fail("No se encontró la reservación seleccionada.");
      }

      var extrasCount = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(extrasCountSql, new { request.ReservationId }, tx, cancellationToken: ct));
      if (extrasCount > 0)
      {
        await tx!.RollbackAsync(ct);
        return ReservacionCommandResult.Fail("Quita los extras de la reservación antes de aplicar el desglose Airbnb.");
      }

      var requestedIds = request.RoomCalendarIds?
        .Where(id => id > 0)
        .Distinct()
        .ToArray() ?? Array.Empty<int>();

      var suiteRows = requestedIds.Length > 0
        ? (await conn.QueryAsync<int>(
            new CommandDefinition(
              selectedSuitesSql,
              new { request.ReservationId, Ids = requestedIds },
              tx,
              cancellationToken: ct))).AsList()
        : (await conn.QueryAsync<int>(
            new CommandDefinition(allSuitesSql, new { request.ReservationId }, tx, cancellationToken: ct))).AsList();

      if (suiteRows.Count == 0)
      {
        await tx!.RollbackAsync(ct);
        return ReservacionCommandResult.Fail("La reservación no tiene suites para distribuir el desglose Airbnb.");
      }

      if (requestedIds.Length > 0 && suiteRows.Count != requestedIds.Length)
      {
        await tx!.RollbackAsync(ct);
        return ReservacionCommandResult.Fail("Una o más suites seleccionadas no pertenecen a la reservación.");
      }

      var suiteAmounts = AirbnbReservationBreakdownCalculator.SplitCurrency(breakdown.TaxableBase, suiteRows.Count);
      for (var index = 0; index < suiteRows.Count; index++)
      {
        await conn.ExecuteAsync(
          new CommandDefinition(
            updateSuiteSql,
            new { Id = suiteRows[index], Precio = suiteAmounts[index] },
            tx,
            cancellationToken: ct));
      }

      await conn.ExecuteAsync(
        new CommandDefinition(
          updateReservationSql,
          new
          {
            request.ReservationId,
            TotalPrice = breakdown.GrossCfdiTotal
          },
          tx,
          cancellationToken: ct));

      await conn.ExecuteAsync(
        new CommandDefinition(
          upsertBreakdownSql,
          new
          {
            request.ReservationId,
            breakdown.PayoutAmount,
            breakdown.TaxableBase,
            breakdown.RoomRateAmount,
            breakdown.CleaningFee,
            breakdown.IvaTransferredAmount,
            breakdown.IvaRetainedAmount,
            breakdown.IsrRetainedAmount,
            breakdown.HostServiceFeeBaseAmount,
            breakdown.HostServiceFeeIvaAmount,
            breakdown.HostServiceFeeTotalAmount,
            breakdown.GrossCfdiTotal,
            breakdown.IvaRate,
            breakdown.IvaRetentionRate,
            breakdown.IsrRetentionRate,
            breakdown.HostServiceFeeRate,
            breakdown.HostServiceFeeIvaRate,
            UsedDefaultRates = AirbnbReservationBreakdownCalculator.UsesDefaultRates(request)
          },
          tx,
          cancellationToken: ct));

      await tx!.CommitAsync(ct);
      return ReservacionCommandResult.Ok(
        $"Desglose Airbnb aplicado. Total CFDI {breakdown.GrossCfdiTotal.ToString("N2", CultureInfo.InvariantCulture)}.");
    }
    catch (Exception ex)
    {
      try { await tx!.RollbackAsync(ct); } catch { /* ignore */ }
      _logger.LogError(ex, "Error applying Airbnb breakdown to reservation {ReservationId}.", request.ReservationId);
      return ReservacionCommandResult.Fail("No se pudo aplicar el desglose Airbnb.");
    }
  }

  public async Task<ReservacionCommandResult> ClearAirbnbBreakdownIfNoPolizaAsync(int reservationId, CancellationToken ct = default)
  {
    if (reservationId <= 0)
      return ReservacionCommandResult.Fail("La reservación seleccionada no es válida.");

    const string sql = """
DECLARE @Result int = 0;

IF OBJECT_ID('dbo.ReservationAirbnbBreakdown', 'U') IS NOT NULL
   AND EXISTS (
       SELECT 1
       FROM dbo.ReservationAirbnbBreakdown
       WHERE ReservationID = @ReservationId
   )
BEGIN
    IF EXISTS (
        SELECT 1
        FROM dbo.Reservation_Transacciones
        WHERE ReservationID = @ReservationId
    )
    BEGIN
        SET @Result = 2;
    END
    ELSE
    BEGIN
        DELETE FROM dbo.ReservationAirbnbBreakdown
        WHERE ReservationID = @ReservationId;

        IF COL_LENGTH('dbo.RESERVATION', 'AIRBNB_UPDATED') IS NOT NULL
        BEGIN
            UPDATE dbo.RESERVATION
            SET AIRBNB_UPDATED = 0
            WHERE ID = @ReservationId;
        END

        SET @Result = 1;
    END
END

SELECT @Result;
""";

    try
    {
      await using var conn = new SqlConnection(_cs);
      var result = await conn.ExecuteScalarAsync<int>(
          new CommandDefinition(sql, new { ReservationId = reservationId }, cancellationToken: ct));

      return result switch
      {
        1 => ReservacionCommandResult.Ok("Desglose Airbnb eliminado porque se aplicó una acción manual."),
        2 => ReservacionCommandResult.Ok("Desglose Airbnb conservado porque la reservación ya tiene una póliza ligada."),
        _ => ReservacionCommandResult.Ok("La reservación no tenía desglose Airbnb.")
      };
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error clearing Airbnb breakdown for reservation {ReservationId}.", reservationId);
      return ReservacionCommandResult.Fail($"No se pudo limpiar el desglose Airbnb: {ex.Message}");
    }
  }

  private static decimal[] SplitCurrency(decimal total, int count)
  {
    var totalCents = (long)decimal.Round(total * 100m, 0, MidpointRounding.ToEven);
    var baseCents = totalCents / count;
    var remainder = totalCents % count;
    var amounts = new decimal[count];

    for (var i = 0; i < count; i++)
    {
      var cents = baseCents + (i < remainder ? 1 : 0);
      amounts[i] = cents / 100m;
    }

    return amounts;
  }

  public async Task<ReservacionCommandResult> AddExtraAsync(ReservacionExtraCreateRequest request, CancellationToken ct = default)
  {
    if (request is null)
      throw new ArgumentNullException(nameof(request));

    if (request.ReservationId <= 0 || request.ExtraId <= 0 || request.Quantity <= 0)
      return ReservacionCommandResult.Fail("Selecciona una reservación válida para el extra.");

    if (request.UnitPrice < 0m)
      return ReservacionCommandResult.Fail("El precio del extra no puede ser negativo.");

    const string sql = @"
INSERT INTO dbo.Reservation_Extra
(
    ReservationID,
    ExtraID,
    ExtraNameSnapshot,
    ExtraDescriptionSnapshot,
    UnitPriceSnapshot,
    Quantity,
    Notes
)
SELECT
    @ReservationId,
    e.ExtraID,
    e.[Name],
    e.[Description],
    @UnitPrice,
    @Quantity,
    @Notes
FROM dbo.Extra e
WHERE e.ExtraID = @ExtraId
  AND e.IsActive = 1;";

    await using var conn = new SqlConnection(_cs);
    var affected = await conn.ExecuteAsync(
      new CommandDefinition(
        sql,
        new
        {
          request.ReservationId,
          request.ExtraId,
          request.UnitPrice,
          request.Quantity,
          Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim()
        },
        cancellationToken: ct));

    return affected > 0
      ? ReservacionCommandResult.Ok("Extra agregado.")
      : ReservacionCommandResult.Fail("No se pudo agregar el extra.");
  }

  public async Task<ReservacionCommandResult> UpdateExtraAsync(ReservacionExtraUpdateRequest request, CancellationToken ct = default)
  {
    if (request is null)
      throw new ArgumentNullException(nameof(request));

    if (request.Id <= 0 || request.ReservationId <= 0 || request.ExtraId <= 0 || request.Quantity <= 0)
      return ReservacionCommandResult.Fail("Selecciona un extra y una reservación válidos.");

    if (request.UnitPrice < 0m)
      return ReservacionCommandResult.Fail("El precio del extra no puede ser negativo.");

    const string sql = @"
UPDATE re
SET
    ExtraID = e.ExtraID,
    ExtraNameSnapshot = e.[Name],
    ExtraDescriptionSnapshot = e.[Description],
    UnitPriceSnapshot = @UnitPrice,
    Quantity = @Quantity,
    Notes = @Notes,
    UpdatedAtUtc = SYSUTCDATETIME()
FROM dbo.Reservation_Extra re
INNER JOIN dbo.Extra e
  ON e.ExtraID = @ExtraId
WHERE re.ReservationExtraID = @Id
  AND re.ReservationID = @ReservationId
  AND e.IsActive = 1;";

    await using var conn = new SqlConnection(_cs);
    var affected = await conn.ExecuteAsync(
      new CommandDefinition(
        sql,
        new
        {
          request.Id,
          request.ReservationId,
          request.ExtraId,
          request.UnitPrice,
          request.Quantity,
          Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim()
        },
        cancellationToken: ct));

    return affected > 0
      ? ReservacionCommandResult.Ok("Extra actualizado.")
      : ReservacionCommandResult.Fail("No se encontró el extra seleccionado.");
  }

  public async Task<ReservacionCommandResult> DeleteExtraAsync(int reservationExtraId, CancellationToken ct = default)
  {
    const string sql = @"DELETE FROM dbo.Reservation_Extra WHERE ReservationExtraID = @Id;";

    await using var conn = new SqlConnection(_cs);
    var affected = await conn.ExecuteAsync(new CommandDefinition(sql, new { Id = reservationExtraId }, cancellationToken: ct));

    return affected > 0
      ? ReservacionCommandResult.Ok("Extra eliminado.")
      : ReservacionCommandResult.Fail("No se encontró el extra seleccionado.");
  }

  private static void ValidateCalendarRows(
    IReadOnlyList<OpenClawRoomCalendarRow> rows,
    IReadOnlyCollection<string> requestedRooms,
    DateTime checkIn,
    DateTime checkOut)
  {
    var expectedNights = (checkOut.Date - checkIn.Date).Days;
    var expectedRows = requestedRooms.Count * expectedNights;

    if (rows.Count != expectedRows)
    {
      throw new OpenClawReservationConflictException("No se encontraron todas las noches requeridas para las suites solicitadas.");
    }

    var lockedRows = rows
      .Where(row => row.IsLocked)
      .Select(row => $"{row.Room} ({row.RoomDate:yyyy-MM-dd})")
      .Distinct()
      .ToArray();

    if (lockedRows.Length > 0)
    {
      throw new OpenClawReservationConflictException(
        $"Las suites ya no están disponibles para todas las fechas solicitadas: {string.Join(", ", lockedRows)}.");
    }
  }

  private static IReadOnlyList<string> NormalizeRequestedSuites(IReadOnlyList<string>? suiteNames)
  {
    if (suiteNames is null || suiteNames.Count == 0)
    {
      return Array.Empty<string>();
    }

    var normalized = new List<string>(suiteNames.Count);
    var seen = new HashSet<string>(StringComparer.Ordinal);

    foreach (var suiteName in suiteNames)
    {
      var value = RequireValue(suiteName, "Cada suite debe tener un nombre válido.");
      var key = OpenClawReservationNaming.NormalizeLookupKey(value);
      if (!seen.Add(key))
      {
        throw new OpenClawReservationValidationException($"La suite '{value}' está repetida en la solicitud.");
      }

      normalized.Add(value);
    }

    return normalized;
  }

  private static IReadOnlyList<OpenClawRequestedExtra> AggregateExtraRequests(IReadOnlyList<OpenClawReservationExtraRequest>? extras)
  {
    if (extras is null || extras.Count == 0)
    {
      return Array.Empty<OpenClawRequestedExtra>();
    }

    var aggregated = new Dictionary<string, OpenClawRequestedExtra>(StringComparer.Ordinal);

    foreach (var extra in extras)
    {
      var catalogName = RequireValue(extra.CatalogName, "Cada extra debe indicar un catálogo válido.");
      if (extra.Quantity <= 0)
      {
        throw new OpenClawReservationValidationException($"La cantidad del extra '{catalogName}' debe ser mayor a cero.");
      }

      var key = OpenClawReservationNaming.NormalizeLookupKey(catalogName);
      if (!aggregated.TryGetValue(key, out var current))
      {
        aggregated[key] = new OpenClawRequestedExtra(catalogName, extra.Quantity, TrimOrNull(extra.Notes));
        continue;
      }

      var mergedNotes = string.Join("; ", new[] { current.Notes, TrimOrNull(extra.Notes) }.Where(note => !string.IsNullOrWhiteSpace(note)));
      aggregated[key] = current with
      {
        Quantity = current.Quantity + extra.Quantity,
        Notes = string.IsNullOrWhiteSpace(mergedNotes) ? null : mergedNotes
      };
    }

    return aggregated.Values.ToArray();
  }

  private static IReadOnlyList<OpenClawResolvedRoom> ResolveRequestedRooms(
    IReadOnlyList<string> requestedNames,
    IReadOnlyList<OpenClawRoomCatalogRow> rooms,
    bool requireSuiteType)
  {
    return requestedNames
      .Select(name => ResolveRequestedRoom(name, rooms, requireSuiteType))
      .ToArray();
  }

  private static IReadOnlyList<OpenClawResolvedExtra> ResolveRequestedExtras(
    IReadOnlyList<OpenClawRequestedExtra> requestedExtras,
    IReadOnlyList<OpenClawExtraCatalogRow> extras)
  {
    return requestedExtras
      .Select(extra =>
      {
        var resolved = ResolveRequestedExtra(extra.CatalogName, extras);
        return new OpenClawResolvedExtra(
          resolved.Id,
          resolved.Name,
          resolved.Description,
          resolved.Price,
          extra.Quantity,
          extra.Notes);
      })
      .ToArray();
  }

  private static async Task<ClienteOptionDto> ResolveOrCreateClienteAsync(
    SqlConnection conn,
    SqlTransaction? tx,
    string clientName,
    CancellationToken ct)
  {
    const string selectSql = """
SELECT TOP (1)
    c.ID AS Id,
    c.Nombre AS Nombre
FROM dbo.Clientes c
WHERE UPPER(LTRIM(RTRIM(c.Nombre))) = UPPER(@Nombre)
ORDER BY c.ID;
""";

    const string insertSql = """
INSERT INTO dbo.Clientes (Nombre)
VALUES (@Nombre);
SELECT CAST(SCOPE_IDENTITY() AS int);
""";

    var existing = await conn.QueryFirstOrDefaultAsync<ClienteOptionDto>(
      new CommandDefinition(
        selectSql,
        new { Nombre = clientName },
        tx,
        cancellationToken: ct));

    if (existing is not null)
    {
      return existing;
    }

    var clienteId = await conn.ExecuteScalarAsync<int>(
      new CommandDefinition(
        insertSql,
        new { Nombre = clientName },
        tx,
        cancellationToken: ct));

    return new ClienteOptionDto
    {
      Id = clienteId,
      Nombre = clientName
    };
  }

  private static OpenClawResolvedRoom ResolveRequestedRoom(
    string requestedName,
    IReadOnlyList<OpenClawRoomCatalogRow> rooms,
    bool requireSuiteType)
  {
    var requestedKey = OpenClawReservationNaming.NormalizeLookupKey(requestedName);
    var matches = rooms
      .Where(room => OpenClawReservationNaming.NormalizeLookupKey(room.RoomName) == requestedKey)
      .ToArray();

    if (requireSuiteType)
    {
      matches = matches.Where(room => IsSuiteRoom(room.RoomType)).ToArray();
    }

    if (matches.Length == 0)
    {
      var kind = requireSuiteType ? "suite" : "catálogo";
      throw new OpenClawReservationValidationException($"No se encontró la {kind} '{requestedName}'.");
    }

    var resolved = matches[0];
    return new OpenClawResolvedRoom(resolved.Id, resolved.RoomName, resolved.BasePrice);
  }

  private static OpenClawExtraCatalogRow ResolveRequestedExtra(
    string requestedName,
    IReadOnlyList<OpenClawExtraCatalogRow> extras)
  {
    var requestedKey = OpenClawReservationNaming.NormalizeLookupKey(requestedName);
    var match = extras.FirstOrDefault(extra => OpenClawReservationNaming.NormalizeLookupKey(extra.Name) == requestedKey);
    if (match is null)
    {
      throw new OpenClawReservationValidationException($"No se encontró el extra '{requestedName}'.");
    }

    return match;
  }

  private static bool IsSuiteRoom(string? roomType)
    => string.Equals(OpenClawReservationNaming.NormalizeLookupKey(roomType), "SUITE", StringComparison.Ordinal);

  private static string RequireValue(string? value, string errorMessage)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      throw new OpenClawReservationValidationException(errorMessage);
    }

    return value.Trim();
  }

  private static string? TrimOrNull(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private static void ApplyCalculatedTotals(
      ReservacionDetailDto detail,
      IReadOnlyList<ReservacionSuiteDto> suites,
      IReadOnlyList<ReservacionExtraDto> extras,
      IReadOnlyList<ReservacionPagoDto> pagos)
  {
    var totals = ReservacionTotalsCalculator.Calculate(
      detail.CheckIn,
      detail.CheckOut,
      suites.Select(s => s.Precio),
      extras.Select(e => e.Price),
      pagos.Sum(p => p.Monto),
      detail.SuiteDiscountPercent);

    detail.TotalSuites = totals.TotalSuites;
    detail.SuiteDiscountPercent = totals.SuiteDiscountPercent;
    detail.SuiteDiscountAmount = totals.SuiteDiscountAmount;
    detail.TotalExtras = totals.TotalExtras;
    detail.SubTotal = totals.SubTotal;
    detail.Tax = totals.Tax;
    detail.Ish = totals.Ish;
    detail.TotalPrice = totals.TotalReservacion;
    detail.Pagado = totals.TotalPagado;
    detail.PorPagar = totals.PorPagar;
    detail.NumNoches = totals.NumNoches;
  }

  private static object? GetValue(IDictionary<string, object> row, string name)
  {
    foreach (var kvp in row)
    {
      if (string.Equals(kvp.Key, name, StringComparison.OrdinalIgnoreCase))
      {
        return kvp.Value;
      }
    }

    return null;
  }

  private static int ToInt(object? value)
  {
    if (value is null || value is DBNull)
      return 0;

    return value switch
    {
      int i => i,
      long l => (int)l,
      short s => s,
      byte b => b,
      _ => int.TryParse(value.ToString(), out var parsed) ? parsed : 0
    };
  }

  private static decimal ToDecimal(object? value)
  {
    if (value is null || value is DBNull)
      return 0m;

    return value switch
    {
      decimal d => d,
      double db => Convert.ToDecimal(db),
      float f => Convert.ToDecimal(f),
      int i => i,
      long l => l,
      _ => decimal.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
        ? parsed
        : 0m
    };
  }

  private static int? NormalizeOptionalId(int? value)
    => value is > 0 ? value.GetValueOrDefault() : null;

  private static DateTime? ToDateTime(object? value)
  {
    if (value is null || value is DBNull)
      return null;

    if (value is DateTime dt)
      return dt;

    return DateTime.TryParse(value.ToString(), out var parsed) ? parsed : null;
  }

  private static bool ToBool(object? value)
  {
    if (value is null || value is DBNull)
      return false;

    return value switch
    {
      bool b => b,
      byte bt => bt != 0,
      short s => s != 0,
      int i => i != 0,
      long l => l != 0,
      string str when string.Equals(str, "true", StringComparison.OrdinalIgnoreCase) => true,
      string str when string.Equals(str, "x", StringComparison.OrdinalIgnoreCase) => true,
      string str when str == "1" => true,
      _ => false
    };
  }

  private static string ToStringSafe(object? value)
    => value is null || value is DBNull ? string.Empty : value.ToString() ?? string.Empty;

  private static string? NormalizeClienteNombre(string? clienteNombre)
    => string.IsNullOrWhiteSpace(clienteNombre) ? null : clienteNombre.Trim();

  private static string ResolveContentType(string? extension)
  {
    if (string.IsNullOrWhiteSpace(extension))
      return "application/octet-stream";

    return extension.Trim().TrimStart('.').ToLowerInvariant() switch
    {
      "pdf" => "application/pdf",
      "xml" => "application/xml",
      "jpg" or "jpeg" => "image/jpeg",
      "png" => "image/png",
      "txt" => "text/plain",
      "csv" => "text/csv",
      "xls" => "application/vnd.ms-excel",
      "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
      _ => "application/octet-stream"
    };
  }

  private sealed record OpenClawRequestedExtra(string CatalogName, int Quantity, string? Notes);
  private sealed record OpenClawResolvedRoom(int Id, string RoomName, decimal BasePrice);
  private sealed record OpenClawResolvedExtra(int ExtraId, string Name, string? Description, decimal UnitPrice, int Quantity, string? Notes);

  private sealed class OpenClawExtraCatalogRow
  {
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public bool IsActive { get; set; }
  }

  private sealed class ListaReservacionListRow
  {
    public int Id { get; set; }
    public string Cliente { get; set; } = string.Empty;
    public DateTime? CheckIn { get; set; }
    public DateTime? CheckOut { get; set; }
    public string? Status { get; set; }
    public decimal TotalPrice { get; set; }
    public decimal SuiteDiscountPercent { get; set; }
    public decimal Pagado { get; set; }
    public decimal PorPagar { get; set; }
    public string FacturacionStatus { get; set; } = ReservationFacturacionStatuses.SinFacturar;
    public int FacturacionPaymentCount { get; set; }
    public int FacturacionFacturadoPaymentCount { get; set; }
    public int FacturacionRegularCfdiCount { get; set; }
    public int FacturacionPago20Count { get; set; }
    public string? Notes { get; set; }
  }

  private sealed class ReservationAmountRow
  {
    public int ReservationId { get; set; }
    public decimal Amount { get; set; }
  }

  private sealed class ReservationFacturacionListRow
  {
    public int ReservationId { get; set; }
    public int PaymentCount { get; set; }
    public int FacturadoPaymentCount { get; set; }
    public int RegularCfdiCount { get; set; }
    public int Pago20Count { get; set; }
  }

  private sealed class OpenClawRoomCatalogRow
  {
    public int Id { get; set; }
    public string RoomName { get; set; } = string.Empty;
    public string? RoomType { get; set; }
    public decimal BasePrice { get; set; }
  }

  private sealed class OpenClawRoomCalendarRow
  {
    public int Id { get; set; }
    public string Room { get; set; } = string.Empty;
    public DateTime RoomDate { get; set; }
    public bool IsLocked { get; set; }
    public string LockedBy { get; set; } = string.Empty;
    public decimal Precio { get; set; }
  }
}
