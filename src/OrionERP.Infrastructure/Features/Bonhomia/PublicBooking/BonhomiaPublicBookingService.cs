using System.Data;
using System.Net.Mail;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Bonhomia.PublicBooking;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;
using OrionERP.Application.Features.Reservaciones.OpenClaw;

namespace OrionERP.Infrastructure.Features.Bonhomia.PublicBooking;

public sealed class BonhomiaPublicBookingService : IBonhomiaPublicBookingService
{
  private readonly string _connectionString;
  private readonly IListaReservacionesService _reservacionesService;
  private readonly BonhomiaCheckoutOptions _options;
  private readonly ILogger<BonhomiaPublicBookingService> _logger;

  public BonhomiaPublicBookingService(
    IConfiguration configuration,
    IListaReservacionesService reservacionesService,
    IOptions<BonhomiaCheckoutOptions> options,
    ILogger<BonhomiaPublicBookingService> logger)
  {
    _connectionString = configuration.GetConnectionString("OrionDb")
      ?? throw new InvalidOperationException("Missing ConnectionStrings:OrionDb.");
    _reservacionesService = reservacionesService;
    _options = options.Value;
    _logger = logger;
  }

  public async Task<BonhomiaAvailabilityDto> GetAvailabilityAsync(
    DateOnly startDate,
    DateOnly endDateExclusive,
    CancellationToken ct = default)
  {
    if (endDateExclusive <= startDate)
    {
      endDateExclusive = startDate.AddDays(Math.Max(_options.AvailabilityDays, 30));
    }

    var timeline = await _reservacionesService.GetCalendarTimelineAsync(
      new RoomCalendarTimelineFilter
      {
        StartDate = startDate.ToDateTime(TimeOnly.MinValue),
        EndDateExclusive = endDateExclusive.ToDateTime(TimeOnly.MinValue),
        RoomType = "SUITE"
      },
      ct);

    var extras = await GetPublicExtraOptionsAsync(ct);
    var cellsByRoom = timeline.DayCells
      .GroupBy(cell => cell.RoomId)
      .ToDictionary(group => group.Key, group => group.ToList());

    var rooms = timeline.Resources
      .Where(resource => resource.CalendarEnabled)
      .OrderBy(resource => resource.DisplayOrder)
      .ThenBy(resource => resource.RoomName)
      .Select(resource =>
      {
        var metadata = BonhomiaPublicRoomMetadata.Resolve(resource.RoomName);
        var cells = cellsByRoom.TryGetValue(resource.RoomId, out var roomCells)
          ? roomCells
          : new List<RoomCalendarDayCellDto>();

        return new BonhomiaRoomAvailabilityDto
        {
          RoomId = resource.RoomId,
          RoomName = resource.RoomName,
          Tag = metadata.Tag,
          Ideal = metadata.Ideal,
          Image = metadata.Image,
          Capacity = metadata.Capacity,
          Bedrooms = metadata.Bedrooms,
          BasePrice = resource.BasePrice,
          Days = cells
            .OrderBy(cell => cell.RoomDate)
            .Select(cell => new BonhomiaDayAvailabilityDto
            {
              Date = DateOnly.FromDateTime(cell.RoomDate.Date),
              IsAvailable = IsPubliclyAvailable(cell),
              StateCode = cell.StateCode,
              Price = cell.Price > 0m ? cell.Price : resource.BasePrice
            })
            .ToArray()
        };
      })
      .ToArray();

    return new BonhomiaAvailabilityDto
    {
      StartDate = startDate,
      EndDateExclusive = endDateExclusive,
      Rooms = rooms,
      Extras = extras
    };
  }

  public async Task<BonhomiaQuoteDto> CreateQuoteAsync(
    BonhomiaQuoteRequest request,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);

    var availability = await GetAvailabilityAsync(request.CheckIn, request.CheckOut, ct);
    var room = availability.Rooms.FirstOrDefault(item => NamesMatch(item.RoomName, request.RoomName));
    if (room is null)
    {
      throw new BonhomiaPublicBookingException("unknown_room", "La suite seleccionada ya no esta disponible.");
    }

    var quote = BonhomiaQuoteCalculator.BuildQuote(
      request,
      room,
      availability.Extras,
      DateTimeOffset.UtcNow.AddMinutes(Math.Max(_options.QuoteTokenLifetimeMinutes, 5)),
      _options.Currency,
      Math.Max(_options.MaxStayNights, 1));

    quote.RoomCalendarIds = await GetRoomCalendarIdsForQuoteAsync(quote, ct);
    return quote;
  }

  public async Task ValidateQuoteAvailabilityAsync(
    BonhomiaQuoteDto quote,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(quote);
    var liveQuote = await CreateQuoteAsync(quote.Request, ct);
    if (!string.Equals(liveQuote.Fingerprint, quote.Fingerprint, StringComparison.Ordinal))
    {
      throw new BonhomiaPublicBookingException("quote_changed", "La cotizacion cambio antes de confirmar el pago.");
    }
  }

  public async Task<BonhomiaPaidReservationResult> CreatePaidReservationAsync(
    BonhomiaQuoteDto quote,
    BonhomiaCustomerInfo customer,
    BonhomiaPayPalCaptureResult payment,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(quote);
    ArgumentNullException.ThrowIfNull(customer);
    ArgumentNullException.ThrowIfNull(payment);

    if (!payment.IsCompleted)
    {
      var status = string.IsNullOrWhiteSpace(payment.Status) ? "SIN_ESTATUS" : payment.Status;
      var reason = string.IsNullOrWhiteSpace(payment.StatusReason) ? string.Empty : $" ({payment.StatusReason})";
      var orderStatus = string.IsNullOrWhiteSpace(payment.OrderStatus) ? string.Empty : $" Orden: {payment.OrderStatus}.";
      throw new BonhomiaPublicBookingException(
        "payment_not_completed",
        $"PayPal devolvio el cobro en estado {status}{reason}.{orderStatus} No se creo la reservacion porque PayPal aun no acredita el pago. Si estas probando sandbox, revisa que la cuenta business acepte {payment.Currency} o acepta/convierte el pago pendiente en PayPal.");
    }

    if (!string.Equals(payment.Currency, quote.Currency, StringComparison.OrdinalIgnoreCase)
        || decimal.Abs(payment.Amount - quote.Total) > 0.01m)
    {
      throw new BonhomiaPublicBookingException("payment_amount_mismatch", "El pago confirmado no coincide con la cotizacion.");
    }

    var fullName = RequireCustomerValue(customer.FullName, "El nombre completo es obligatorio.");
    var email = RequireCustomerValue(customer.Email, "El correo es obligatorio.");
    var phone = RequireCustomerValue(customer.Phone, "El telefono es obligatorio.");
    if (!IsValidEmail(email))
    {
      throw new BonhomiaPublicBookingException("invalid_customer", "El correo no tiene un formato valido.");
    }

    await using var conn = new SqlConnection(_connectionString);
    await conn.OpenAsync(ct);
    await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);

    try
    {
      var existingReservation = await FindExistingPaidReservationAsync(conn, tx, payment, ct);
      if (existingReservation is not null)
      {
        await tx.CommitAsync(ct);
        return existingReservation;
      }

      var room = await ResolveRoomAsync(conn, tx, quote.RoomName, requireSuiteType: true, ct);
      var calendarRows = (await conn.QueryAsync<RoomCalendarLockRow>(
        new CommandDefinition(
          """
SELECT
    rc.ID AS Id,
    rc.ROOM AS RoomName,
    rc.ROOM_DATE AS RoomDate,
    CAST(ISNULL(rc.IS_LOCKED, 0) AS bit) AS IsLocked,
    ISNULL(rc.LOCKED_BY, '') AS LockedBy,
    ISNULL(rc.LOCK_DESCRIPTION, '') AS LockDescription,
    CAST(ISNULL(rc.PRECIO, 0) AS decimal(18,2)) AS Precio
FROM dbo.ROOM_CALENDAR rc WITH (UPDLOCK, HOLDLOCK)
WHERE rc.ROOM = @RoomName
  AND rc.ROOM_DATE >= @CheckIn
  AND rc.ROOM_DATE < @CheckOut
ORDER BY rc.ROOM_DATE;
""",
          new
          {
            RoomName = room.RoomName,
            CheckIn = quote.CheckIn.ToDateTime(TimeOnly.MinValue),
            CheckOut = quote.CheckOut.ToDateTime(TimeOnly.MinValue)
          },
          tx,
          cancellationToken: ct))).AsList();

      ValidateLockedCalendarRows(quote, calendarRows);

      var extras = await ResolveSelectedExtrasAsync(conn, tx, quote.Request.Extras, ct);
      var suiteLineTotals = calendarRows.Select(row => row.Precio > 0m ? row.Precio : room.BasePrice).ToArray();
      var extraLineTotals = extras.Select(extra => extra.UnitPrice * extra.Quantity).ToArray();
      var totals = ReservacionTotalsCalculator.Calculate(
        quote.CheckIn.ToDateTime(TimeOnly.MinValue),
        quote.CheckOut.ToDateTime(TimeOnly.MinValue),
        suiteLineTotals,
        extraLineTotals,
        totalPagado: 0m);

      if (decimal.Abs(totals.TotalReservacion - quote.Total) > 0.01m)
      {
        throw new BonhomiaPublicBookingException("quote_changed", "La cotizacion cambio antes de confirmar el pago.");
      }

      var cliente = await ResolveOrCreateCustomerAsync(conn, tx, fullName, email, phone, ct);
      var reservationId = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(
          """
INSERT INTO dbo.RESERVATION
(CLIENTE_ID, CHECKIN, CHECKOUT, STATUS, RECOMMENED_BY, NOTES, TAXABLE, TOTAL_PRICE)
VALUES
(@ClienteId, @CheckIn, @CheckOut, @Status, @RecommendedBy, @Notes, @RequiresCfdi, @TotalPrice);
SELECT CAST(SCOPE_IDENTITY() AS int);
""",
          new
          {
            ClienteId = cliente.Id,
            CheckIn = quote.CheckIn.ToDateTime(TimeOnly.MinValue),
            CheckOut = quote.CheckOut.ToDateTime(TimeOnly.MinValue),
            Status = ReservationStatuses.Pagada,
            RecommendedBy = "Bonhomia Web",
            Notes = BuildReservationNotes(quote, customer, payment),
            RequiresCfdi = true,
            TotalPrice = totals.TotalReservacion
          },
          tx,
          cancellationToken: ct));

      var updateCount = await conn.ExecuteAsync(
        new CommandDefinition(
          """
UPDATE dbo.ROOM_CALENDAR
SET
    IS_LOCKED = 1,
    LOCKED_BY = @LockedBy,
    LOCK_DESCRIPTION = @ReservationId,
    STATUS = @Status
WHERE ID IN @Ids
  AND ISNULL(IS_LOCKED, 0) = 0;
""",
          new
          {
            LockedBy = cliente.Nombre,
            ReservationId = reservationId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Status = ReservationStatuses.Pagada,
            Ids = calendarRows.Select(row => row.Id).ToArray()
          },
          tx,
          cancellationToken: ct));

      if (updateCount != calendarRows.Count)
      {
        throw new BonhomiaPublicBookingException("not_available", "La suite se ocupo antes de finalizar la reservacion.");
      }

      if (extras.Count > 0)
      {
        await conn.ExecuteAsync(
          new CommandDefinition(
            """
INSERT INTO dbo.RESERVATION_DETAIL
(RESERVATION_ID, ROOM_ID, PRICE, NOTES)
VALUES
(@ReservationId, @RoomId, @Price, @Notes);
""",
            extras.Select(extra => new
            {
              ReservationId = reservationId,
              RoomId = extra.RoomId,
              Price = extra.UnitPrice * extra.Quantity,
              Notes = extra.Quantity == 1
                ? extra.DisplayName
                : $"{extra.DisplayName} x{extra.Quantity}"
            }).ToArray(),
            tx,
            cancellationToken: ct));
      }

      var transaccionId = await CreatePaymentTransactionAsync(conn, tx, reservationId, cliente.Nombre, totals.TotalReservacion, payment, ct);

      await tx.CommitAsync(ct);

      return new BonhomiaPaidReservationResult
      {
        ReservationId = reservationId,
        TransaccionId = transaccionId,
        ClientName = cliente.Nombre,
        Total = totals.TotalReservacion
      };
    }
    catch (Exception ex)
    {
      try { await tx.RollbackAsync(ct); } catch { /* ignore rollback failure */ }

      if (ex is not BonhomiaPublicBookingException)
      {
        _logger.LogError(ex, "Error creating paid Bonhomia reservation for PayPal order {OrderId}.", payment.OrderId);
      }

      throw;
    }
  }

  public Task<ReservacionDetailDto?> GetReservationDetailAsync(int reservationId, CancellationToken ct = default)
    => _reservacionesService.GetReservacionDetailAsync(reservationId, ct);

  private async Task<IReadOnlyList<int>> GetRoomCalendarIdsForQuoteAsync(BonhomiaQuoteDto quote, CancellationToken ct)
  {
    const string sql = """
SELECT rc.ID
FROM dbo.ROOM_CALENDAR rc
WHERE rc.ROOM = @RoomName
  AND rc.ROOM_DATE >= @CheckIn
  AND rc.ROOM_DATE < @CheckOut
ORDER BY rc.ROOM_DATE;
""";

    await using var conn = new SqlConnection(_connectionString);
    var ids = await conn.QueryAsync<int>(
      new CommandDefinition(
        sql,
        new
        {
          quote.RoomName,
          CheckIn = quote.CheckIn.ToDateTime(TimeOnly.MinValue),
          CheckOut = quote.CheckOut.ToDateTime(TimeOnly.MinValue)
        },
        cancellationToken: ct));

    return ids.AsList();
  }

  private async Task<IReadOnlyList<BonhomiaExtraOptionDto>> GetPublicExtraOptionsAsync(CancellationToken ct)
  {
    const string sql = """
SELECT
    r.ID AS RoomId,
    r.ROOM_NAME AS RoomName,
    CAST(ISNULL(r.BASE_PRICE, 0) AS decimal(18,2)) AS BasePrice
FROM dbo.ROOM r
WHERE ISNULL(r.ROOM_TYPE, '') <> 'SUITE';
""";

    await using var conn = new SqlConnection(_connectionString);
    var dbExtras = (await conn.QueryAsync<ExtraCatalogRow>(new CommandDefinition(sql, cancellationToken: ct))).AsList();

    return BonhomiaPublicExtraCatalog.Items
      .Select(item =>
      {
        var match = dbExtras.FirstOrDefault(row => item.Aliases.Any(alias => NamesMatch(alias, row.RoomName)));
        return new BonhomiaExtraOptionDto
        {
          Code = item.Code,
          Name = item.Name,
          Detail = item.Detail,
          CatalogName = match?.RoomName ?? item.CatalogName,
          Icon = item.Icon,
          UnitPrice = match?.BasePrice > 0m ? match.BasePrice : item.UnitPrice,
          MaxQuantity = item.MaxQuantity
        };
      })
      .ToArray();
  }

  private async Task<RoomCatalogRow> ResolveRoomAsync(
    SqlConnection conn,
    SqlTransaction tx,
    string requestedRoomName,
    bool requireSuiteType,
    CancellationToken ct)
  {
    var rows = (await conn.QueryAsync<RoomCatalogRow>(
      new CommandDefinition(
        """
SELECT
    r.ID AS RoomId,
    r.ROOM_NAME AS RoomName,
    r.ROOM_TYPE AS RoomType,
    CAST(ISNULL(r.BASE_PRICE, 0) AS decimal(18,2)) AS BasePrice
FROM dbo.ROOM r;
""",
        transaction: tx,
        cancellationToken: ct))).AsList();

    var match = rows.FirstOrDefault(row => NamesMatch(row.RoomName, requestedRoomName));
    if (match is null || (requireSuiteType && !string.Equals(match.RoomType, "SUITE", StringComparison.OrdinalIgnoreCase)))
    {
      throw new BonhomiaPublicBookingException("unknown_room", "La suite seleccionada ya no esta configurada.");
    }

    return match;
  }

  private async Task<IReadOnlyList<ResolvedExtraLine>> ResolveSelectedExtrasAsync(
    SqlConnection conn,
    SqlTransaction tx,
    IReadOnlyList<BonhomiaSelectedExtraRequest>? selectedExtras,
    CancellationToken ct)
  {
    if (selectedExtras is null || selectedExtras.Count == 0)
    {
      return Array.Empty<ResolvedExtraLine>();
    }

    var publicExtras = await GetPublicExtraOptionsAsync(ct);
    var optionsByCode = publicExtras.ToDictionary(extra => extra.Code, StringComparer.OrdinalIgnoreCase);
    var rows = (await conn.QueryAsync<RoomCatalogRow>(
      new CommandDefinition(
        """
SELECT
    r.ID AS RoomId,
    r.ROOM_NAME AS RoomName,
    r.ROOM_TYPE AS RoomType,
    CAST(ISNULL(r.BASE_PRICE, 0) AS decimal(18,2)) AS BasePrice
FROM dbo.ROOM r;
""",
        transaction: tx,
        cancellationToken: ct))).AsList();

    var resolved = new List<ResolvedExtraLine>();
    foreach (var selected in selectedExtras.Where(item => item.Quantity > 0))
    {
      if (!optionsByCode.TryGetValue(selected.Code, out var option))
      {
        throw new BonhomiaPublicBookingException("unknown_extra", "Uno de los extras seleccionados ya no esta disponible.");
      }

      var room = rows.FirstOrDefault(row => NamesMatch(row.RoomName, option.CatalogName));
      if (room is null)
      {
        throw new BonhomiaPublicBookingException("extra_not_configured", $"{option.Name} no esta configurado en el catalogo de OrionERP.");
      }

      resolved.Add(new ResolvedExtraLine(
        room.RoomId,
        option.Name,
        option.UnitPrice,
        selected.Quantity));
    }

    return resolved;
  }

  private async Task<ClienteRow> ResolveOrCreateCustomerAsync(
    SqlConnection conn,
    SqlTransaction tx,
    string fullName,
    string email,
    string phone,
    CancellationToken ct)
  {
    const string sql = """
DECLARE @ClienteId int;

SELECT TOP (1) @ClienteId = ID
FROM dbo.Clientes
WHERE UPPER(LTRIM(RTRIM(ISNULL(Email, '')))) = UPPER(@Email)
ORDER BY ID;

IF @ClienteId IS NULL
BEGIN
    SELECT TOP (1) @ClienteId = ID
    FROM dbo.Clientes
    WHERE UPPER(LTRIM(RTRIM(ISNULL(Nombre, '')))) = UPPER(@Nombre)
    ORDER BY ID;
END;

IF @ClienteId IS NULL
BEGIN
    INSERT INTO dbo.Clientes (Nombre, Email, Cel)
    VALUES (@Nombre, @Email, @Telefono);

    SET @ClienteId = CAST(SCOPE_IDENTITY() AS int);
END
ELSE
BEGIN
    UPDATE dbo.Clientes
    SET
        Email = CASE WHEN Email IS NULL OR LTRIM(RTRIM(Email)) = '' THEN @Email ELSE Email END,
        Cel = CASE WHEN Cel IS NULL OR LTRIM(RTRIM(Cel)) = '' THEN @Telefono ELSE Cel END
    WHERE ID = @ClienteId;
END;

SELECT
    ID AS Id,
    ISNULL(Nombre, @Nombre) AS Nombre
FROM dbo.Clientes
WHERE ID = @ClienteId;
""";

    return await conn.QuerySingleAsync<ClienteRow>(
      new CommandDefinition(
        sql,
        new
        {
          Nombre = fullName,
          Email = email,
          Telefono = phone
        },
        tx,
        cancellationToken: ct));
  }

  private async Task<int> CreatePaymentTransactionAsync(
    SqlConnection conn,
    SqlTransaction tx,
    int reservationId,
    string clienteNombre,
    decimal amount,
    BonhomiaPayPalCaptureResult payment,
    CancellationToken ct)
  {
    var transaccionId = await conn.ExecuteScalarAsync<int>(
      new CommandDefinition(
        """
INSERT INTO dbo.Transacciones
(RFC, Fecha, Concepto, Monto, Tipo_Poliza, Forma_Pago, Categoria, Facturado, Memo, Cuenta)
VALUES
(@Rfc, @Fecha, @Concepto, @Monto, @TipoPoliza, @FormaPago, @Categoria, 0, @Memo, @Cuenta);
SELECT CAST(SCOPE_IDENTITY() AS int);
""",
        new
        {
          Rfc = _options.AccountingRfc,
          Fecha = DateTime.Now,
          Concepto = $"PAGO PAYPAL RESERVACION#{reservationId} - {clienteNombre}",
          Monto = amount,
          TipoPoliza = "INGRESO",
          FormaPago = _options.AccountingPaymentForm,
          Categoria = _options.AccountingCategoryId,
          Memo = $"Bonhomia Web | PayPal Order: {payment.OrderId} | Capture: {payment.CaptureId} | Payer: {payment.PayerEmail}",
          Cuenta = _options.AccountingAccount
        },
        tx,
        cancellationToken: ct));

    await conn.ExecuteAsync(
      new CommandDefinition(
        """
INSERT INTO dbo.Reservation_Transacciones
(ReservationID, TransaccionID, Amount)
VALUES (@ReservationId, @TransaccionId, @Amount);
""",
        new
        {
          ReservationId = reservationId,
          TransaccionId = transaccionId,
          Amount = amount
        },
        tx,
        cancellationToken: ct));

    return transaccionId;
  }

  private async Task<BonhomiaPaidReservationResult?> FindExistingPaidReservationAsync(
    SqlConnection conn,
    SqlTransaction tx,
    BonhomiaPayPalCaptureResult payment,
    CancellationToken ct)
  {
    var orderLike = BuildSqlContainsPattern($"PayPal Order: {payment.OrderId}");
    var captureLike = string.IsNullOrWhiteSpace(payment.CaptureId)
      ? null
      : BuildSqlContainsPattern($"PayPal Capture: {payment.CaptureId}");

    var row = await conn.QueryFirstOrDefaultAsync<ExistingPaidReservationRow>(
      new CommandDefinition(
        """
SELECT TOP (1)
    r.ID AS ReservationId,
    ISNULL(rt.TransaccionID, 0) AS TransaccionId,
    ISNULL(c.Nombre, '') AS ClientName,
    CAST(ISNULL(r.TOTAL_PRICE, 0) AS decimal(18,2)) AS Total
FROM dbo.RESERVATION r WITH (UPDLOCK, HOLDLOCK)
INNER JOIN dbo.Clientes c ON c.ID = r.CLIENTE_ID
LEFT JOIN dbo.Reservation_Transacciones rt ON rt.ReservationID = r.ID
LEFT JOIN dbo.Transacciones t ON t.ID = rt.TransaccionID
WHERE ISNULL(r.NOTES, '') LIKE @OrderLike ESCAPE '\'
   OR ISNULL(t.Memo, '') LIKE @OrderLike ESCAPE '\'
   OR (@CaptureLike IS NOT NULL AND ISNULL(r.NOTES, '') LIKE @CaptureLike ESCAPE '\')
   OR (@CaptureLike IS NOT NULL AND ISNULL(t.Memo, '') LIKE @CaptureLike ESCAPE '\')
ORDER BY r.ID DESC;
""",
        new
        {
          OrderLike = orderLike,
          CaptureLike = captureLike
        },
        tx,
        cancellationToken: ct));

    if (row is null)
    {
      return null;
    }

    return new BonhomiaPaidReservationResult
    {
      ReservationId = row.ReservationId,
      TransaccionId = row.TransaccionId,
      ClientName = row.ClientName,
      Total = row.Total
    };
  }

  private static string BuildSqlContainsPattern(string value)
  {
    var escaped = value
      .Replace(@"\", @"\\", StringComparison.Ordinal)
      .Replace("%", @"\%", StringComparison.Ordinal)
      .Replace("_", @"\_", StringComparison.Ordinal)
      .Replace("[", @"\[", StringComparison.Ordinal);

    return $"%{escaped}%";
  }

  private static void ValidateLockedCalendarRows(BonhomiaQuoteDto quote, IReadOnlyList<RoomCalendarLockRow> rows)
  {
    var nights = quote.CheckOut.DayNumber - quote.CheckIn.DayNumber;
    if (rows.Count != nights)
    {
      throw new BonhomiaPublicBookingException("not_available", "No existe calendario completo para las fechas seleccionadas.");
    }

    var expected = Enumerable.Range(0, nights)
      .Select(offset => quote.CheckIn.AddDays(offset))
      .ToHashSet();

    var actual = rows.Select(row => DateOnly.FromDateTime(row.RoomDate.Date)).ToHashSet();
    if (!expected.SetEquals(actual))
    {
      throw new BonhomiaPublicBookingException("not_available", "No existe calendario completo para las fechas seleccionadas.");
    }

    if (rows.Any(row => row.IsLocked || !string.IsNullOrWhiteSpace(row.LockDescription)))
    {
      throw new BonhomiaPublicBookingException("not_available", "La suite se ocupo antes de finalizar la reservacion.");
    }
  }

  private static bool IsPubliclyAvailable(RoomCalendarDayCellDto cell)
    => cell.RoomCalendarId.HasValue
      && string.Equals(cell.StateCode, "available", StringComparison.OrdinalIgnoreCase)
      && !cell.IsLocked
      && !cell.ReservationId.HasValue;

  private static bool NamesMatch(string left, string right)
    => string.Equals(
      OpenClawReservationNaming.NormalizeLookupKey(left),
      OpenClawReservationNaming.NormalizeLookupKey(right),
      StringComparison.OrdinalIgnoreCase);

  private static string RequireCustomerValue(string? value, string message)
  {
    var normalized = value?.Trim();
    if (string.IsNullOrWhiteSpace(normalized))
    {
      throw new BonhomiaPublicBookingException("invalid_customer", message);
    }

    return normalized;
  }

  private static bool IsValidEmail(string value)
  {
    try
    {
      var address = new MailAddress(value);
      return string.Equals(address.Address, value, StringComparison.OrdinalIgnoreCase);
    }
    catch
    {
      return false;
    }
  }

  private static string BuildReservationNotes(
    BonhomiaQuoteDto quote,
    BonhomiaCustomerInfo customer,
    BonhomiaPayPalCaptureResult payment)
  {
    var extras = quote.Lines
      .Where(line => string.Equals(line.Type, "extra", StringComparison.OrdinalIgnoreCase))
      .Select(line => $"{line.Description} x{line.Quantity}")
      .ToArray();

    return string.Join(
      Environment.NewLine,
      "Reservacion creada desde Bonhomia Web.",
      $"Huespedes: {quote.Guests}",
      $"Contacto: {customer.Email.Trim()} | {customer.Phone.Trim()}",
      $"PayPal Order: {payment.OrderId}",
      $"PayPal Capture: {payment.CaptureId}",
      extras.Length == 0 ? "Extras: ninguno" : $"Extras: {string.Join(", ", extras)}");
  }

  private sealed record ResolvedExtraLine(int RoomId, string DisplayName, decimal UnitPrice, int Quantity);

  private sealed class ClienteRow
  {
    public int Id { get; set; }
    public string Nombre { get; set; } = string.Empty;
  }

  private sealed class ExistingPaidReservationRow
  {
    public int ReservationId { get; set; }
    public int TransaccionId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public decimal Total { get; set; }
  }

  private sealed class RoomCatalogRow
  {
    public int RoomId { get; set; }
    public string RoomName { get; set; } = string.Empty;
    public string RoomType { get; set; } = string.Empty;
    public decimal BasePrice { get; set; }
  }

  private sealed class RoomCalendarLockRow
  {
    public int Id { get; set; }
    public string RoomName { get; set; } = string.Empty;
    public DateTime RoomDate { get; set; }
    public bool IsLocked { get; set; }
    public string LockedBy { get; set; } = string.Empty;
    public string LockDescription { get; set; } = string.Empty;
    public decimal Precio { get; set; }
  }

  private sealed class ExtraCatalogRow
  {
    public int RoomId { get; set; }
    public string RoomName { get; set; } = string.Empty;
    public decimal BasePrice { get; set; }
  }

  private sealed record PublicRoomMetadata(int Capacity, int Bedrooms, string Ideal, string Image, string Tag);

  private static class BonhomiaPublicRoomMetadata
  {
    private static readonly PublicRoomMetadata Default = new(
      2,
      1,
      "Suite amueblada para una estancia comoda y tranquila.",
      "/Images/Bonhomia/gallery-terrace-living.jpg",
      "Suite");

    private static readonly IReadOnlyDictionary<string, PublicRoomMetadata> Items =
      new Dictionary<string, PublicRoomMetadata>(StringComparer.OrdinalIgnoreCase)
      {
        ["CASA BERLIN"] = new(6, 3, "Para familias o equipos de trabajo que necesitan amplitud, privacidad y tres recamaras.", "/Images/Bonhomia/exterior-main.jpg", "Casa completa"),
        ["BERLIN"] = new(6, 3, "Para familias o equipos de trabajo que necesitan amplitud, privacidad y tres recamaras.", "/Images/Bonhomia/exterior-main.jpg", "Casa completa"),
        ["SUITE MANHATTAN"] = new(4, 2, "Dos recamaras y espacio comodo para compartir sin sacrificar privacidad.", "/Images/Bonhomia/gallery-terrace-living.jpg", "Ejecutiva"),
        ["MANHATTAN"] = new(4, 2, "Dos recamaras y espacio comodo para compartir sin sacrificar privacidad.", "/Images/Bonhomia/gallery-terrace-living.jpg", "Ejecutiva"),
        ["SUITE SEUL"] = new(4, 2, "Estancias largas con habitaciones independientes y un ambiente tranquilo.", "/Images/Bonhomia/gallery-kitchen.jpg", "Larga estancia"),
        ["SEUL"] = new(4, 2, "Estancias largas con habitaciones independientes y un ambiente tranquilo.", "/Images/Bonhomia/gallery-kitchen.jpg", "Larga estancia"),
        ["SUITE MOSCU"] = new(2, 1, "Practicidad y confort para parejas o viajeros de negocio.", "/Images/Bonhomia/gallery-wine-detail.jpg", "Compacta"),
        ["MOSCU"] = new(2, 1, "Practicidad y confort para parejas o viajeros de negocio.", "/Images/Bonhomia/gallery-wine-detail.jpg", "Compacta"),
        ["SUITE PARIS"] = new(2, 1, "Un espacio acogedor para desconectar, celebrar o hacer home office.", "/Images/Bonhomia/welcome-detail.png", "Acogedora"),
        ["PARIS"] = new(2, 1, "Un espacio acogedor para desconectar, celebrar o hacer home office.", "/Images/Bonhomia/welcome-detail.png", "Acogedora"),
        ["PENTHOUSE"] = new(2, 1, "Maxima privacidad con un toque premium y una vista mas abierta.", "/Images/Bonhomia/hero-penthouse.jpg", "Premium"),
        ["CASA GRECIA"] = new(10, 4, "Casa completa para convivir, descansar y viajar en grupo.", "/Images/Bonhomia/catalog-exterior.png", "Grupos"),
        ["GRECIA"] = new(10, 4, "Casa completa para convivir, descansar y viajar en grupo.", "/Images/Bonhomia/catalog-exterior.png", "Grupos"),
        ["CASA LONDON"] = new(6, 3, "Para familias y grupos que quieren una casa completa y funcional.", "/Images/Bonhomia/exterior-vertical.jpg", "Familiar"),
        ["LONDON"] = new(6, 3, "Para familias y grupos que quieren una casa completa y funcional.", "/Images/Bonhomia/exterior-vertical.jpg", "Familiar")
      };

    public static PublicRoomMetadata Resolve(string roomName)
    {
      var key = OpenClawReservationNaming.NormalizeLookupKey(roomName);
      return Items.TryGetValue(key, out var metadata)
        ? metadata
        : Default;
    }
  }

  private sealed record PublicExtraCatalogItem(
    string Code,
    string Name,
    string Detail,
    string CatalogName,
    decimal UnitPrice,
    int MaxQuantity,
    string Icon,
    IReadOnlyList<string> Aliases);

  private static class BonhomiaPublicExtraCatalog
  {
    public static IReadOnlyList<PublicExtraCatalogItem> Items { get; } =
    [
      new("early-checkin", "Early check-in", "Ingreso desde 13:00 hrs", "CHECK-IN ANTICIPADO", 200m, 1, "bi bi-alarm", ["CHECK-IN ANTICIPADO", "EARLY CHECK-IN", "EARLY CHECKIN"]),
      new("late-checkout", "Late check-out", "Salida de 12:00 a 14:00 hrs", "CHECK-OUT TARDIO", 200m, 1, "bi bi-clock-history", ["CHECK-OUT TARDIO", "LATE CHECK-OUT", "LATE CHECKOUT"]),
      new("pet", "Mascota", "Admision por estancia", "MASCOTA", 500m, 2, "bi bi-house-heart", ["MASCOTA", "PET"]),
      new("meals", "Alimentos", "Desayuno o cena por persona", "ALIMENTOS", 200m, 10, "bi bi-cup-hot", ["ALIMENTOS", "DESAYUNO", "CENA"]),
      new("airport-transfer", "Transporte AICM", "Sencillo hasta 3 personas", "TRANSPORTE AICM", 3000m, 2, "bi bi-car-front", ["TRANSPORTE AICM", "TRANSPORTE", "AICM"]),
      new("laundry", "Lavanderia", "Servicio por kilogramo", "LAVANDERIA", 80m, 20, "bi bi-basket", ["LAVANDERIA", "LAVANDERIA KG"])
    ];
  }
}
