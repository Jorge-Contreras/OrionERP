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

    var p = new DynamicParameters();
    string sql;

    if (filter.IncluirCanceladas)
    {
      var includeCanceladasSql = new StringBuilder(@"
SELECT
    r.ID AS Id,
    ISNULL(c.Nombre, '(Sin cliente)') AS Cliente,
    r.CHECKIN AS CheckIn,
    r.CHECKOUT AS CheckOut,
    r.STATUS AS Status,
    CAST(ISNULL(r.TOTAL_PRICE, 0) AS decimal(18,2)) AS TotalPrice,
    CAST(ISNULL(pa.PAGADO, 0) AS decimal(18,2)) AS Pagado,
    CAST(ISNULL(r.TOTAL_PRICE, 0) - ISNULL(pa.PAGADO, 0) AS decimal(18,2)) AS PorPagar,
    r.NOTES AS Notes
FROM dbo.RESERVATION r
LEFT JOIN dbo.Clientes c
  ON c.ID = r.CLIENTE_ID
OUTER APPLY (
    SELECT SUM(rt.Amount) AS PAGADO
    FROM dbo.Reservation_Transacciones rt
    WHERE rt.ReservationID = r.ID
) pa
WHERE 1=1");

      AppendListaFilters(
        includeCanceladasSql,
        p,
        filter,
        "r.ID",
        "c.Nombre",
        "r.STATUS",
        "r.CHECKIN");

      includeCanceladasSql.Append(@"
ORDER BY r.CHECKIN DESC, r.ID DESC;");

      sql = includeCanceladasSql.ToString();
    }
    else
    {
      var viewSql = new StringBuilder(@"
SELECT
    lr.ID AS Id,
    ISNULL(lr.Nombre, '(Sin cliente)') AS Cliente,
    lr.CHECKIN AS CheckIn,
    lr.CHECKOUT AS CheckOut,
    lr.STATUS AS Status,
    CAST(ISNULL(lr.TOTAL_PRICE, 0) AS decimal(18,2)) AS TotalPrice,
    CAST(ISNULL(lr.PAGADO, 0) AS decimal(18,2)) AS Pagado,
    CAST(ISNULL(lr.POR_PAGAR, 0) AS decimal(18,2)) AS PorPagar,
    lr.NOTES AS Notes
FROM dbo.LISTA_DE_RESERVACIONES lr
WHERE 1=1");

      AppendListaFilters(
        viewSql,
        p,
        filter,
        "lr.ID",
        "lr.Nombre",
        "lr.STATUS",
        "lr.CHECKIN");

      viewSql.Append(@"
ORDER BY lr.CHECKIN DESC, lr.ID DESC;");

      sql = viewSql.ToString();
    }

    await using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<ListaReservacionItemDto>(
      new CommandDefinition(sql, p, cancellationToken: ct));

    return rows.AsList();
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
    r.RECOMMENED_BY AS RecommenedBy,
    CAST(ISNULL(r.TAXABLE, 0) AS bit) AS Taxable,
    CAST(ISNULL(r.TOTAL_PRICE, 0) AS decimal(18,2)) AS TotalPrice,
    r.NOTES AS Notes
FROM dbo.RESERVATION r
LEFT JOIN dbo.Clientes c
  ON c.ID = r.CLIENTE_ID
WHERE r.ID = @ReservationId;";

    await using var conn = new SqlConnection(_cs);

    var detail = await conn.QueryFirstOrDefaultAsync<ReservacionDetailDto>(
      new CommandDefinition(summarySql, new { ReservationId = reservationId }, cancellationToken: ct));

    if (detail is null)
      return null;

    var suites = await GetSuitesByReservationAsync(reservationId, ct);
    var extras = await GetExtrasAsync(reservationId, ct);
    var pagos = await GetPagosAsync(reservationId, ct);
    var attachments = await GetAttachmentsAsync(reservationId, ct);

    ApplyCalculatedTotals(detail, suites, extras, pagos);
    detail.Suites = suites;
    detail.Extras = extras;
    detail.Pagos = pagos;
    detail.Attachments = attachments;
    return detail;
  }

  public async Task<IReadOnlyList<ClienteOptionDto>> GetClientesAsync(string? searchText = null, CancellationToken ct = default)
  {
    const string sql = @"
SELECT TOP (300)
    c.ID AS Id,
    c.Nombre AS Nombre
FROM dbo.Clientes c
WHERE (@Search IS NULL OR c.Nombre LIKE @Search)
ORDER BY c.Nombre;";

    await using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<ClienteOptionDto>(
      new CommandDefinition(
        sql,
        new
        {
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

  public async Task<IReadOnlyList<RoomOptionDto>> GetRoomsForExtrasAsync(CancellationToken ct = default)
  {
    const string sql = @"
SELECT
    r.ID AS Id,
    r.ROOM_NAME AS RoomName,
    r.ROOM_TYPE AS RoomType,
    CAST(ISNULL(r.BASE_PRICE, 0) AS decimal(18,2)) AS BasePrice
FROM dbo.ROOM r
ORDER BY r.ROOM_NAME;";

    await using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<RoomOptionDto>(new CommandDefinition(sql, cancellationToken: ct));
    return rows.AsList();
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
    rd.ID AS Id,
    rd.ROOM_ID AS RoomId,
    ISNULL(r.ROOM_NAME, '') AS RoomName,
    ISNULL(r.ROOM_DESCRIPTION, '') AS RoomDescription,
    CAST(ISNULL(rd.PRICE, 0) AS decimal(18,2)) AS Price,
    rd.NOTES AS Notes,
    CAST(ISNULL(rd.DISCOUNT, 0) AS decimal(18,2)) AS Discount,
    CAST(ISNULL(rd.DISCOUNTED_PRICE, 0) AS decimal(18,2)) AS DiscountedPrice
FROM dbo.RESERVATION_DETAIL rd
LEFT JOIN dbo.ROOM r
  ON r.ID = rd.ROOM_ID
WHERE rd.RESERVATION_ID = @ReservationId
ORDER BY rd.ID;";

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

    const string sql = @"
UPDATE dbo.RESERVATION
SET
    CLIENTE_ID = @ClienteId,
    CHECKIN = @CheckIn,
    CHECKOUT = @CheckOut,
    STATUS = @Status,
    RECOMMENED_BY = @RecommenedBy,
    NOTES = @Notes,
    TAXABLE = @Taxable,
    TOTAL_PRICE = @TotalPrice
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
          request.Taxable,
          request.TotalPrice
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

  public async Task<ReservacionCommandResult> ApplySuitesDiscountAsync(IReadOnlyCollection<int> roomCalendarIds, decimal discountPercentage, CancellationToken ct = default)
  {
    if (roomCalendarIds is null || roomCalendarIds.Count == 0)
      return ReservacionCommandResult.Fail("Selecciona al menos una suite.");

    if (discountPercentage < 0 || discountPercentage > 100)
      return ReservacionCommandResult.Fail("Porcentaje inválido. Debe estar entre 0 y 100.");

    const string sql = @"UPDATE dbo.ROOM_CALENDAR SET PRECIO = PRECIO * @Factor WHERE ID IN @Ids;";
    var factor = 1m - (discountPercentage / 100m);

    await using var conn = new SqlConnection(_cs);
    var affected = await conn.ExecuteAsync(
      new CommandDefinition(sql, new { Factor = factor, Ids = roomCalendarIds.ToArray() }, cancellationToken: ct));

    return ReservacionCommandResult.Ok($"Descuento aplicado a {affected} suites.");
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

  public async Task<ReservacionCommandResult> DistributeSuitesTotalAsync(int reservationId, decimal totalAmount, CancellationToken ct = default)
  {
    const string countSql = @"
SELECT COUNT(1)
FROM dbo.ROOM_CALENDAR
WHERE TRY_CAST(LOCK_DESCRIPTION AS int) = @ReservationId;";

    const string updateSql = @"
UPDATE dbo.ROOM_CALENDAR
SET PRECIO = @Precio
WHERE TRY_CAST(LOCK_DESCRIPTION AS int) = @ReservationId;";

    await using var conn = new SqlConnection(_cs);
    var count = await conn.ExecuteScalarAsync<int>(
      new CommandDefinition(countSql, new { ReservationId = reservationId }, cancellationToken: ct));

    if (count <= 0)
      return ReservacionCommandResult.Fail("No hay suites ligadas para distribuir el total.");

    var unitPrice = decimal.Round(totalAmount / count, 2, MidpointRounding.ToEven);
    var affected = await conn.ExecuteAsync(
      new CommandDefinition(updateSql, new { Precio = unitPrice, ReservationId = reservationId }, cancellationToken: ct));

    return ReservacionCommandResult.Ok($"Total distribuido en {affected} suites.");
  }

  public async Task<ReservacionCommandResult> AddExtraAsync(ReservacionExtraCreateRequest request, CancellationToken ct = default)
  {
    if (request is null)
      throw new ArgumentNullException(nameof(request));

    if (request.ReservationId <= 0 || request.RoomId <= 0)
      return ReservacionCommandResult.Fail("Selecciona una suite válida para el extra.");

    const string sql = @"
INSERT INTO dbo.RESERVATION_DETAIL
(RESERVATION_ID, ROOM_ID, PRICE, DISCOUNTED_PRICE, DISCOUNT, NOTES)
VALUES
(@ReservationId, @RoomId, @Price, @DiscountedPrice, @Discount, @Notes);";

    await using var conn = new SqlConnection(_cs);
    var affected = await conn.ExecuteAsync(
      new CommandDefinition(
        sql,
        new
        {
          request.ReservationId,
          request.RoomId,
          request.Price,
          request.DiscountedPrice,
          request.Discount,
          Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim()
        },
        cancellationToken: ct));

    return affected > 0
      ? ReservacionCommandResult.Ok("Extra agregado.")
      : ReservacionCommandResult.Fail("No se pudo agregar el extra.");
  }

  public async Task<ReservacionCommandResult> DeleteExtraAsync(int reservationDetailId, CancellationToken ct = default)
  {
    const string sql = @"DELETE FROM dbo.RESERVATION_DETAIL WHERE ID = @Id;";

    await using var conn = new SqlConnection(_cs);
    var affected = await conn.ExecuteAsync(new CommandDefinition(sql, new { Id = reservationDetailId }, cancellationToken: ct));

    return affected > 0
      ? ReservacionCommandResult.Ok("Extra eliminado.")
      : ReservacionCommandResult.Fail("No se encontró el extra seleccionado.");
  }

  private static void ApplyCalculatedTotals(
      ReservacionDetailDto detail,
      IReadOnlyList<ReservacionSuiteDto> suites,
      IReadOnlyList<ReservacionExtraDto> extras,
      IReadOnlyList<ReservacionPagoDto> pagos)
  {
    var totalSuites = decimal.Round(suites.Sum(s => s.Precio), 2, MidpointRounding.ToEven);
    var totalExtras = decimal.Round(extras.Sum(e => e.DiscountedPrice), 2, MidpointRounding.ToEven);
    var subtotal = decimal.Round(totalSuites + totalExtras, 2, MidpointRounding.ToEven);

    var tax = 0m;
    var ish = 0m;
    if (detail.Taxable)
    {
      tax = decimal.Round(subtotal * 0.16m, 2, MidpointRounding.ToEven);
      if (detail.CheckIn.HasValue && detail.CheckIn.Value.Year < 2025)
      {
        ish = decimal.Round(subtotal * 0.02m, 2, MidpointRounding.ToEven);
      }
    }

    var totalReservacion = decimal.Round(subtotal + tax + ish, 2, MidpointRounding.ToEven);
    var pagado = decimal.Round(pagos.Sum(p => p.Monto), 2, MidpointRounding.ToEven);
    var porPagar = decimal.Round(totalReservacion - pagado, 2, MidpointRounding.ToEven);

    var numNoches = 0;
    if (detail.CheckIn.HasValue && detail.CheckOut.HasValue)
    {
      var inDate = detail.CheckIn.Value.Date;
      var outDate = detail.CheckOut.Value.Date;
      if (outDate >= inDate)
      {
        numNoches = (int)(outDate - inDate).TotalDays;
      }
    }

    detail.TotalSuites = totalSuites;
    detail.TotalExtras = totalExtras;
    detail.SubTotal = subtotal;
    detail.Tax = tax;
    detail.Ish = ish;
    detail.TotalPrice = totalReservacion;
    detail.Pagado = pagado;
    detail.PorPagar = porPagar;
    detail.NumNoches = numNoches;
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
}
