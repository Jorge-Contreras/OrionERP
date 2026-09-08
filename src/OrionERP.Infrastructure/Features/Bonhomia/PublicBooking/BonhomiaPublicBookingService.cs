using System.Data;
using System.Net.Mail;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Bonhomia.PublicBooking;
using OrionERP.Application.Features.Reservaciones;
using OrionERP.Application.Features.Reservaciones.Experiencias;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.Infrastructure.Features.Bonhomia.PublicBooking;

public sealed class BonhomiaPublicBookingService : IBonhomiaPublicBookingService
{
  private const string TaxIncluded = "TaxIncluded";

  private readonly string _connectionString;
  private readonly IBonhomiaScopedPublicDataReader _dataReader;
  private readonly IHospitalityWebsiteScopeAccessor _scopeAccessor;
  private readonly HospitalityWebsiteDefinition _website;
  private readonly BonhomiaCheckoutOptions _options;
  private readonly ILogger<BonhomiaPublicBookingService> _logger;

  public BonhomiaPublicBookingService(
    IConfiguration configuration,
    IBonhomiaScopedPublicDataReader dataReader,
    IHospitalityWebsiteScopeAccessor scopeAccessor,
    HospitalityWebsiteDefinition website,
    IOptions<BonhomiaCheckoutOptions> options,
    ILogger<BonhomiaPublicBookingService> logger)
  {
    _connectionString = configuration.GetConnectionString("OrionDb")
      ?? throw new InvalidOperationException("Missing ConnectionStrings:OrionDb.");
    _dataReader = dataReader ?? throw new ArgumentNullException(nameof(dataReader));
    _scopeAccessor = scopeAccessor ?? throw new ArgumentNullException(nameof(scopeAccessor));
    _website = website ?? throw new ArgumentNullException(nameof(website));
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

    var timeline = await _dataReader.GetCalendarTimelineAsync(startDate, endDateExclusive, ct);
    var extras = await _dataReader.GetExtraOptionsAsync(ct);
    var experiences = await _dataReader.GetExperienceCatalogAsync(startDate, endDateExclusive, ct);
    var cellsByRoom = timeline.DayCells
      .GroupBy(cell => cell.RoomId)
      .ToDictionary(group => group.Key, group => group.ToList());

    var rooms = timeline.Resources
      .Where(resource => resource.CalendarEnabled)
      .OrderBy(resource => resource.DisplayOrder)
      .ThenBy(resource => resource.RoomName)
      .Select(resource =>
      {
        var metadata = _website.FindRoom(resource.RoomName)
          ?? throw new InvalidOperationException(
            $"HospitalityWebsite has no presentation for scoped room '{resource.RoomName}'.");
        var cells = cellsByRoom.TryGetValue(resource.RoomId, out var roomCells)
          ? roomCells
          : new List<RoomCalendarDayCellDto>();

        return new BonhomiaRoomAvailabilityDto
        {
          RoomId = resource.RoomId,
          RoomName = resource.RoomName,
          Tag = metadata.Tag,
          Ideal = metadata.Ideal,
          Image = metadata.PrimaryImage,
          Capacity = metadata.Capacity,
          Bedrooms = metadata.Bedrooms,
          Bathrooms = metadata.Bathrooms,
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
      Extras = extras,
      Experiences = experiences
    };
  }

  public async Task<BonhomiaQuoteDto> CreateQuoteAsync(
    BonhomiaQuoteRequest request,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);

    var scope = await _scopeAccessor.ResolveRequiredAsync(ct);

    var nowUtc = DateTimeOffset.UtcNow;
    BonhomiaBookingCutoffPolicy.EnsureCheckInIsAllowed(request.CheckIn, nowUtc, _options.TimeZone);

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
      availability.Experiences,
      nowUtc.AddMinutes(Math.Max(_options.QuoteTokenLifetimeMinutes, 5)),
      _options.Currency,
      Math.Max(_options.MaxStayNights, 1));

    quote.PublicSiteKey = scope.PublicSiteKey;
    quote.RoomCalendarIds = await _dataReader.GetRoomCalendarIdsAsync(
      quote.RoomName,
      quote.CheckIn,
      quote.CheckOut,
      ct);
    quote.Fingerprint = BonhomiaQuoteCalculator.CreateFingerprint(quote);
    return quote;
  }

  public async Task ValidateQuoteAvailabilityAsync(
    BonhomiaQuoteDto quote,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(quote);
    var scope = await _scopeAccessor.ResolveRequiredAsync(ct);
    HospitalityWebsiteScopePolicy.EnsureQuoteBelongsToScope(quote, scope);
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
    BonhomiaLegalAcceptance legalAcceptance,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(quote);
    ArgumentNullException.ThrowIfNull(customer);
    ArgumentNullException.ThrowIfNull(payment);
    ArgumentNullException.ThrowIfNull(legalAcceptance);

    var acceptedPrivacyVersion = RequireLegalVersion(
      legalAcceptance.PrivacyVersion,
      "La version aceptada del aviso de privacidad no es valida.");
    var acceptedTermsVersion = RequireLegalVersion(
      legalAcceptance.TermsVersion,
      "La version aceptada de los terminos no es valida.");
    if (legalAcceptance.AcceptedAtUtc == default)
    {
      throw new BonhomiaPublicBookingException(
        "invalid_legal_acceptance",
        "La aceptacion legal no tiene un sello de tiempo valido.");
    }
    var legalAcceptedAtUtc = legalAcceptance.AcceptedAtUtc.UtcDateTime;

    var scope = await _scopeAccessor.ResolveRequiredAsync(ct);
    HospitalityWebsiteScopePolicy.EnsureQuoteBelongsToScope(quote, scope);
    BonhomiaPayPalOrderPolicy.EnsureCaptureBelongsToQuote(payment, quote);

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
    var phone = customer.Phone?.Trim() ?? string.Empty;
    if (!IsValidEmail(email))
    {
      throw new BonhomiaPublicBookingException("invalid_customer", "El correo no tiene un formato valido.");
    }

    await using var conn = new SqlConnection(_connectionString);
    await conn.OpenAsync(ct);
    await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);

    try
    {
      var existingReservation = await FindExistingPaidReservationAsync(conn, tx, scope, payment, ct);
      if (existingReservation is not null)
      {
        await tx.CommitAsync(ct);
        return existingReservation;
      }

      var room = await ResolveRoomAsync(conn, tx, scope, quote.RoomName, requireSuiteType: true, ct);
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
WHERE rc.OrionCompanyId = @ScopeCompanyId
  AND rc.OrionSiteId = @ScopeSiteId
  AND rc.RoomId = @RoomId
  AND rc.ROOM_DATE >= @CheckIn
  AND rc.ROOM_DATE < @CheckOut
ORDER BY rc.ROOM_DATE;
""",
          new
          {
            ScopeCompanyId = scope.CompanyId,
            ScopeSiteId = scope.SiteId,
            room.RoomId,
            CheckIn = quote.CheckIn.ToDateTime(TimeOnly.MinValue),
            CheckOut = quote.CheckOut.ToDateTime(TimeOnly.MinValue)
          },
          tx,
          cancellationToken: ct))).AsList();

      ValidateLockedCalendarRows(quote, calendarRows);

      var extras = await ResolveSelectedExtrasAsync(conn, tx, scope, quote.Request.Extras, ct);
      var experiences = await ResolveSelectedExperiencesAsync(quote, ct);
      var suiteLineTotals = calendarRows.Select(row => row.Precio > 0m ? row.Precio : room.BasePrice).ToArray();
      var extraLineTotals = extras
        .Select(extra => new ReservationChargeLine(extra.UnitPrice * extra.Quantity, ReservationChargeTaxMode.TaxIncluded))
        .ToArray();
      var experienceLineTotals = experiences.SelectMany(experience => BuildExperienceChargeLines(experience.Pricing)).ToArray();
      var totals = ReservacionTotalsCalculator.Calculate(
        quote.CheckIn.ToDateTime(TimeOnly.MinValue),
        quote.CheckOut.ToDateTime(TimeOnly.MinValue),
        suiteLineTotals,
        extraLineTotals,
        experienceLineTotals,
        totalPagado: 0m);

      if (decimal.Abs(totals.TotalReservacion - quote.Total) > 0.01m)
      {
        throw new BonhomiaPublicBookingException("quote_changed", "La cotizacion cambio antes de confirmar el pago.");
      }

      var cliente = await ResolveOrCreateCustomerAsync(conn, tx, scope, fullName, email, phone, ct);
      var reservationId = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(
          """
INSERT INTO dbo.RESERVATION
(CLIENTE_ID, CHECKIN, CHECKOUT, STATUS, RECOMMENED_BY, NOTES, TAXABLE, TOTAL_PRICE,
 OrionCompanyId, OrionSiteId, PrivacyVersionAccepted, TermsVersionAccepted, LegalAcceptedAtUtc)
VALUES
(@ClienteId, @CheckIn, @CheckOut, @Status, @RecommendedBy, @Notes, @RequiresCfdi, @TotalPrice,
 @ScopeCompanyId, @ScopeSiteId, @PrivacyVersionAccepted, @TermsVersionAccepted, @LegalAcceptedAtUtc);
SELECT CAST(SCOPE_IDENTITY() AS int);
""",
          new
          {
            ClienteId = cliente.Id,
            CheckIn = quote.CheckIn.ToDateTime(TimeOnly.MinValue),
            CheckOut = quote.CheckOut.ToDateTime(TimeOnly.MinValue),
            Status = ReservationStatuses.Pagada,
          RecommendedBy = _options.ReservationSourceLabel,
            Notes = BuildReservationNotes(quote, customer, payment),
            RequiresCfdi = true,
            TotalPrice = totals.TotalReservacion,
            ScopeCompanyId = scope.CompanyId,
            ScopeSiteId = scope.SiteId,
            PrivacyVersionAccepted = acceptedPrivacyVersion,
            TermsVersionAccepted = acceptedTermsVersion,
            LegalAcceptedAtUtc = legalAcceptedAtUtc
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
    LOCK_DESCRIPTION = @ReservationIdText,
    ReservationId = @ReservationId,
    STATUS = @Status
WHERE ID IN @Ids
  AND OrionCompanyId = @ScopeCompanyId
  AND OrionSiteId = @ScopeSiteId
  AND ISNULL(IS_LOCKED, 0) = 0;
""",
          new
          {
            LockedBy = cliente.Nombre,
            ReservationId = reservationId,
            ReservationIdText = reservationId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Status = ReservationStatuses.Pagada,
            Ids = calendarRows.Select(row => row.Id).ToArray(),
            ScopeCompanyId = scope.CompanyId,
            ScopeSiteId = scope.SiteId
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
INSERT INTO dbo.Reservation_Extra
(ReservationID, ExtraID, ExtraNameSnapshot, ExtraDescriptionSnapshot, UnitPriceSnapshot, Quantity, TaxMode, Notes,
 OrionCompanyId, OrionSiteId)
VALUES
(@ReservationId, @ExtraId, @ExtraNameSnapshot, @ExtraDescriptionSnapshot, @UnitPriceSnapshot, @Quantity, @TaxMode, @Notes,
 @ScopeCompanyId, @ScopeSiteId);
""",
            extras.Select(extra => new
            {
              ReservationId = reservationId,
              ExtraId = extra.ExtraId,
              ExtraNameSnapshot = extra.DisplayName,
              ExtraDescriptionSnapshot = extra.Description,
              UnitPriceSnapshot = extra.UnitPrice,
              extra.Quantity,
              TaxMode = TaxIncluded,
              Notes = BuildExtraNotes(extra),
              ScopeCompanyId = scope.CompanyId,
              ScopeSiteId = scope.SiteId
            }).ToArray(),
            tx,
            cancellationToken: ct));
      }

      if (experiences.Count > 0)
      {
        foreach (var experience in experiences)
        {
          var reservationExperienceId = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(
              """
INSERT INTO dbo.Reservation_Experience
(
    ReservationID,
    ExperienceID,
    ExperiencePackageID,
    ExperienceDate,
    ExperienceNameSnapshot,
    PackageNameSnapshot,
    ProviderNameSnapshot,
    PackageIncludesSnapshot,
    PayingParticipants,
    NonPayingParticipants,
    UnitPriceSnapshot,
    PackageSubtotalSnapshot,
    AddOnsTotalSnapshot,
    TotalSnapshot,
    TaxMode,
    Notes,
    OrionCompanyId,
    OrionSiteId
)
VALUES
(
    @ReservationId,
    @ExperienceId,
    @ExperiencePackageId,
    @ExperienceDate,
    @ExperienceName,
    @PackageName,
    @ProviderName,
    @PackageIncludes,
    @AdultParticipants,
    @ChildParticipants,
    @UnitPrice,
    @PackageSubtotal,
    @AddOnsTotal,
    @Total,
    @TaxMode,
    @Notes,
    @ScopeCompanyId,
    @ScopeSiteId
);
SELECT CAST(SCOPE_IDENTITY() AS int);
""",
              new
              {
                ReservationId = reservationId,
                experience.Experience.ExperienceId,
                experience.Package.ExperiencePackageId,
                ExperienceDate = experience.Request.ExperienceDate.ToDateTime(TimeOnly.MinValue),
                ExperienceName = experience.Experience.Name,
                PackageName = experience.Package.Name,
                ProviderName = experience.Experience.ProviderName,
                PackageIncludes = experience.Package.Includes,
                AdultParticipants = experience.Request.AdultParticipants,
                ChildParticipants = experience.Request.ChildParticipants,
                UnitPrice = experience.Pricing.UnitPrice,
                PackageSubtotal = experience.Pricing.PackageSubtotal,
                AddOnsTotal = experience.Pricing.AddOnsTotal,
                Total = experience.Pricing.Total,
                TaxMode = experience.Pricing.TaxMode,
                Notes = BuildExperienceNotes(experience),
                ScopeCompanyId = scope.CompanyId,
                ScopeSiteId = scope.SiteId
              },
              tx,
              cancellationToken: ct));

          if (experience.Pricing.AddOns.Count > 0)
          {
            await conn.ExecuteAsync(
              new CommandDefinition(
                """
INSERT INTO dbo.Reservation_ExperienceAddOn
(ReservationExperienceID, ExperienceAddOnID, AddOnNameSnapshot, Quantity, UnitPriceSnapshot, TotalSnapshot, TaxMode,
 OrionCompanyId, OrionSiteId)
VALUES
(@ReservationExperienceId, @ExperienceAddOnId, @AddOnName, @Quantity, @UnitPrice, @Total, @TaxMode,
 @ScopeCompanyId, @ScopeSiteId);
""",
                experience.Pricing.AddOns.Select(addOn => new
                {
                  ReservationExperienceId = reservationExperienceId,
                  addOn.AddOn.ExperienceAddOnId,
                  AddOnName = addOn.AddOn.Name,
                  addOn.Quantity,
                  addOn.UnitPrice,
                  addOn.Total,
                  addOn.TaxMode,
                  ScopeCompanyId = scope.CompanyId,
                  ScopeSiteId = scope.SiteId
                }).ToArray(),
                tx,
                cancellationToken: ct));
          }
        }
      }

      var transaccionId = await CreatePaymentTransactionAsync(conn, tx, scope, reservationId, cliente.Nombre, totals.TotalReservacion, payment, ct);

      await tx.CommitAsync(ct);

      return new BonhomiaPaidReservationResult
      {
        ReservationId = reservationId,
        TransaccionId = transaccionId,
        ClientName = cliente.Nombre,
        Total = totals.TotalReservacion,
        CreatedNewReservation = true
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
    => _dataReader.GetReservationDetailAsync(reservationId, ct);

  private async Task<RoomCatalogRow> ResolveRoomAsync(
    SqlConnection conn,
    SqlTransaction tx,
    HospitalityWebsiteScope scope,
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
FROM dbo.ROOM r
WHERE r.OrionCompanyId = @ScopeCompanyId
  AND r.OrionSiteId = @ScopeSiteId;
""",
        new
        {
          ScopeCompanyId = scope.CompanyId,
          ScopeSiteId = scope.SiteId
        },
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
    HospitalityWebsiteScope scope,
    IReadOnlyList<BonhomiaSelectedExtraRequest>? selectedExtras,
    CancellationToken ct)
  {
    if (selectedExtras is null || selectedExtras.Count == 0)
    {
      return Array.Empty<ResolvedExtraLine>();
    }

    var publicExtras = await _dataReader.GetExtraOptionsAsync(ct);
    var optionsByCode = publicExtras.ToDictionary(extra => extra.Code, StringComparer.OrdinalIgnoreCase);
    var rows = (await conn.QueryAsync<ExtraCatalogRow>(
      new CommandDefinition(
        """
SELECT
    e.ExtraID,
    e.[Name],
    e.[Description],
    CAST(ISNULL(e.Price, 0) AS decimal(18,2)) AS Price
FROM dbo.Extra e
WHERE e.OrionCompanyId = @ScopeCompanyId
  AND e.OrionSiteId = @ScopeSiteId
  AND e.IsActive = 1;
""",
        new
        {
          ScopeCompanyId = scope.CompanyId,
          ScopeSiteId = scope.SiteId
        },
        transaction: tx,
        cancellationToken: ct))).AsList();

    var resolved = new List<ResolvedExtraLine>();
    foreach (var selected in selectedExtras.Where(item => item.Quantity > 0))
    {
      if (!optionsByCode.TryGetValue(selected.Code, out var option))
      {
        throw new BonhomiaPublicBookingException("unknown_extra", "Uno de los extras seleccionados ya no esta disponible.");
      }

      var extra = rows.FirstOrDefault(row => NamesMatch(row.Name, option.CatalogName));
      if (extra is null)
      {
        throw new BonhomiaPublicBookingException("extra_not_configured", $"{option.Name} no esta configurado en el catalogo de OrionERP.");
      }

      resolved.Add(new ResolvedExtraLine(
        extra.ExtraID,
        option.Name,
        extra.Description,
        option.UnitPrice,
        selected.Quantity));
    }

    return resolved;
  }

  private async Task<IReadOnlyList<ResolvedExperienceLine>> ResolveSelectedExperiencesAsync(
    BonhomiaQuoteDto quote,
    CancellationToken ct)
  {
    if (quote.Request.Experiences.Count == 0)
    {
      return Array.Empty<ResolvedExperienceLine>();
    }

    var catalog = await _dataReader.GetExperienceCatalogAsync(quote.CheckIn, quote.CheckOut, ct);
    var experiencesByCode = catalog.ToDictionary(item => item.Code, StringComparer.OrdinalIgnoreCase);
    var resolved = new List<ResolvedExperienceLine>();

    foreach (var selected in quote.Request.Experiences.Where(item => item.AdultParticipants + item.ChildParticipants > 0))
    {
      if (!experiencesByCode.TryGetValue(selected.Code, out var experience))
      {
        throw new BonhomiaPublicBookingException("unknown_experience", "Una de las experiencias seleccionadas ya no esta disponible.");
      }

      var package = experience.Packages.FirstOrDefault(item => string.Equals(item.Code, selected.PackageCode, StringComparison.OrdinalIgnoreCase));
      if (package is null)
      {
        throw new BonhomiaPublicBookingException("unknown_experience_package", $"{experience.Name} ya no tiene disponible el paquete seleccionado.");
      }

      var addOnsByCode = experience.AddOns.ToDictionary(item => item.Code, StringComparer.OrdinalIgnoreCase);
      var addOns = new List<ExperiencePricingAddOnInput>();
      var normalizedAddOns = new List<BonhomiaSelectedExperienceAddOnRequest>();

      foreach (var addOnRequest in selected.AddOns.Where(item => item.Quantity > 0))
      {
        if (!addOnsByCode.TryGetValue(addOnRequest.Code, out var addOn))
        {
          throw new BonhomiaPublicBookingException("unknown_experience_addon", $"Un adicional de {experience.Name} ya no esta disponible.");
        }

        addOns.Add(new ExperiencePricingAddOnInput
        {
          AddOn = addOn,
          Quantity = addOnRequest.Quantity
        });
        normalizedAddOns.Add(new BonhomiaSelectedExperienceAddOnRequest
        {
          Code = addOn.Code,
          Quantity = addOnRequest.Quantity
        });
      }

      var normalizedRequest = new BonhomiaSelectedExperienceRequest
      {
        Code = experience.Code,
        PackageCode = package.Code,
        ExperienceDate = selected.ExperienceDate,
        AdultParticipants = selected.AdultParticipants,
        ChildParticipants = selected.ChildParticipants,
        AddOns = normalizedAddOns
      };

      var pricing = ExperiencePricingCalculator.Calculate(new ExperiencePricingInput
      {
        ExperienceDate = normalizedRequest.ExperienceDate,
        Experience = experience,
        Package = package,
        AdultParticipants = normalizedRequest.AdultParticipants,
        ChildParticipants = normalizedRequest.ChildParticipants,
        AddOns = addOns
      });

      resolved.Add(new ResolvedExperienceLine(experience, package, normalizedRequest, pricing));
    }

    return resolved;
  }

  private static IEnumerable<ReservationChargeLine> BuildExperienceChargeLines(ExperiencePricingResult pricing)
  {
    yield return new ReservationChargeLine(pricing.PackageSubtotal, MapTaxMode(pricing.TaxMode));

    foreach (var addOn in pricing.AddOns)
    {
      yield return new ReservationChargeLine(addOn.Total, MapTaxMode(addOn.TaxMode));
    }
  }

  private static ReservationChargeTaxMode MapTaxMode(string? value)
    => value switch
    {
      ExperienceTaxModes.TaxIncluded => ReservationChargeTaxMode.TaxIncluded,
      ExperienceTaxModes.NonTaxable => ReservationChargeTaxMode.NonTaxable,
      _ => ReservationChargeTaxMode.TaxableExclusive
    };

  private async Task<ClienteRow> ResolveOrCreateCustomerAsync(
    SqlConnection conn,
    SqlTransaction tx,
    HospitalityWebsiteScope scope,
    string fullName,
    string email,
    string phone,
    CancellationToken ct)
  {
    const string sql = """
DECLARE @ClienteId int;

SELECT TOP (1) @ClienteId = customer.ID
FROM orion.HospitalitySiteCustomer siteCustomer WITH (UPDLOCK, HOLDLOCK)
INNER JOIN dbo.Clientes customer ON customer.ID = siteCustomer.ClienteId
WHERE siteCustomer.CompanyId = @ScopeCompanyId
  AND siteCustomer.SiteId = @ScopeSiteId
  AND UPPER(LTRIM(RTRIM(ISNULL(customer.Email, '')))) = UPPER(@Email)
ORDER BY customer.ID;

IF @ClienteId IS NULL
BEGIN
    SELECT TOP (1) @ClienteId = customer.ID
    FROM orion.HospitalitySiteCustomer siteCustomer WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN dbo.Clientes customer ON customer.ID = siteCustomer.ClienteId
    WHERE siteCustomer.CompanyId = @ScopeCompanyId
      AND siteCustomer.SiteId = @ScopeSiteId
      AND UPPER(LTRIM(RTRIM(ISNULL(customer.Nombre, '')))) = UPPER(@Nombre)
    ORDER BY customer.ID;
END;

IF @ClienteId IS NULL
BEGIN
    INSERT INTO dbo.Clientes (Nombre, Email, Cel)
    VALUES (@Nombre, @Email, @Telefono);

    SET @ClienteId = CAST(SCOPE_IDENTITY() AS int);

    INSERT orion.HospitalitySiteCustomer (CompanyId, SiteId, ClienteId)
    VALUES (@ScopeCompanyId, @ScopeSiteId, @ClienteId);
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
FROM dbo.Clientes customer
WHERE customer.ID = @ClienteId
  AND EXISTS
  (
    SELECT 1 FROM orion.HospitalitySiteCustomer siteCustomer
    WHERE siteCustomer.CompanyId = @ScopeCompanyId
      AND siteCustomer.SiteId = @ScopeSiteId
      AND siteCustomer.ClienteId = customer.ID
  );
""";

    return await conn.QuerySingleAsync<ClienteRow>(
      new CommandDefinition(
        sql,
        new
        {
          Nombre = fullName,
          Email = email,
          Telefono = phone,
          ScopeCompanyId = scope.CompanyId,
          ScopeSiteId = scope.SiteId
        },
        tx,
        cancellationToken: ct));
  }

  private async Task<int> CreatePaymentTransactionAsync(
    SqlConnection conn,
    SqlTransaction tx,
    HospitalityWebsiteScope scope,
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
(RFC, Fecha, Concepto, Monto, Tipo_Poliza, Forma_Pago, Facturado, Memo, Cuenta)
VALUES
(@Rfc, @Fecha, @Concepto, @Monto, @TipoPoliza, @FormaPago, 0, @Memo, @Cuenta);
SELECT CAST(SCOPE_IDENTITY() AS int);
""",
        new
        {
          Rfc = scope.CompanyRfc,
          Fecha = DateTime.Now,
          Concepto = $"PAGO PAYPAL RESERVACION#{reservationId} - {clienteNombre}",
          Monto = amount,
          TipoPoliza = "INGRESO",
          FormaPago = _options.AccountingPaymentForm,
          Memo = $"{_options.ReservationSourceLabel} | PayPal Order: {payment.OrderId} | Capture: {payment.CaptureId} | Payer: {payment.PayerEmail}",
          Cuenta = _options.AccountingAccount
        },
        tx,
        cancellationToken: ct));

    await conn.ExecuteAsync(
      new CommandDefinition(
        """
INSERT INTO dbo.Reservation_Transacciones
(ReservationID, TransaccionID, Amount, OrionCompanyId, OrionSiteId)
VALUES (@ReservationId, @TransaccionId, @Amount, @ScopeCompanyId, @ScopeSiteId);
""",
        new
        {
          ReservationId = reservationId,
          TransaccionId = transaccionId,
          Amount = amount,
          ScopeCompanyId = scope.CompanyId,
          ScopeSiteId = scope.SiteId
        },
        tx,
        cancellationToken: ct));

    return transaccionId;
  }

  private async Task<BonhomiaPaidReservationResult?> FindExistingPaidReservationAsync(
    SqlConnection conn,
    SqlTransaction tx,
    HospitalityWebsiteScope scope,
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
INNER JOIN orion.HospitalitySiteCustomer customerScope
  ON customerScope.CompanyId = @ScopeCompanyId
 AND customerScope.SiteId = @ScopeSiteId
 AND customerScope.ClienteId = c.ID
LEFT JOIN dbo.Reservation_Transacciones rt
  ON rt.ReservationID = r.ID
 AND rt.OrionCompanyId = @ScopeCompanyId
 AND rt.OrionSiteId = @ScopeSiteId
LEFT JOIN dbo.Transacciones t
  ON t.ID = rt.TransaccionID
WHERE r.OrionCompanyId = @ScopeCompanyId
  AND r.OrionSiteId = @ScopeSiteId
  AND
  (
       ISNULL(r.NOTES, '') LIKE @OrderLike ESCAPE '\'
    OR ISNULL(t.Memo, '') LIKE @OrderLike ESCAPE '\'
    OR (@CaptureLike IS NOT NULL AND ISNULL(r.NOTES, '') LIKE @CaptureLike ESCAPE '\')
    OR (@CaptureLike IS NOT NULL AND ISNULL(t.Memo, '') LIKE @CaptureLike ESCAPE '\')
  )
ORDER BY r.ID DESC;
""",
        new
        {
          OrderLike = orderLike,
          CaptureLike = captureLike,
          ScopeCompanyId = scope.CompanyId,
          ScopeSiteId = scope.SiteId
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
      Total = row.Total,
      CreatedNewReservation = false
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
      ReservationCatalogNaming.NormalizeLookupKey(left),
      ReservationCatalogNaming.NormalizeLookupKey(right),
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

  private static string RequireLegalVersion(string? value, string message)
  {
    var normalized = value?.Trim();
    if (string.IsNullOrWhiteSpace(normalized)
        || normalized.Length > 30
        || normalized.Any(char.IsControl))
    {
      throw new BonhomiaPublicBookingException("invalid_legal_acceptance", message);
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

  private string BuildReservationNotes(
    BonhomiaQuoteDto quote,
    BonhomiaCustomerInfo customer,
    BonhomiaPayPalCaptureResult payment)
  {
    var extras = quote.Lines
      .Where(line => string.Equals(line.Type, "extra", StringComparison.OrdinalIgnoreCase))
      .Select(line => $"{line.Description} x{line.Quantity}")
      .ToArray();
    var experiences = quote.Lines
      .Where(line => string.Equals(line.Type, "experience", StringComparison.OrdinalIgnoreCase)
        || string.Equals(line.Type, "experience-addon", StringComparison.OrdinalIgnoreCase))
      .Select(line => $"{line.Description} x{line.Quantity}")
      .ToArray();

    return string.Join(
      Environment.NewLine,
      $"Reservacion creada desde {_options.ReservationSourceLabel}.",
      $"Huespedes: {quote.Guests}",
      $"Contacto: {BuildContactNote(customer)}",
      $"PayPal Order: {payment.OrderId}",
      $"PayPal Capture: {payment.CaptureId}",
      extras.Length == 0 ? "Extras: ninguno" : $"Extras: {string.Join(", ", extras)}",
      experiences.Length == 0 ? "Experiencias: ninguna" : $"Experiencias: {string.Join(", ", experiences)}");
  }

  private static string BuildContactNote(BonhomiaCustomerInfo customer)
  {
    var parts = new[] { customer.Email?.Trim() ?? string.Empty, customer.Phone?.Trim() ?? string.Empty }
      .Where(part => !string.IsNullOrWhiteSpace(part));
    return string.Join(" | ", parts);
  }

  private static string BuildExtraNotes(ResolvedExtraLine extra)
    => extra.Quantity == 1
      ? extra.DisplayName
      : $"{extra.DisplayName} x{extra.Quantity}";

  private static string BuildExperienceNotes(ResolvedExperienceLine experience)
  {
    var parts = new List<string>
    {
      $"{experience.Experience.Name} - {experience.Package.Name}",
      $"Fecha: {experience.Request.ExperienceDate:yyyy-MM-dd}",
      $"Adultos: {experience.Request.AdultParticipants}",
      $"Menores: {experience.Request.ChildParticipants}"
    };

    if (experience.Pricing.RequiresOperationalWarning)
    {
      parts.Add("Aviso operativo: revisar indicaciones del proveedor para menores.");
    }

    if (experience.Pricing.AddOns.Count > 0)
    {
      parts.Add($"Adicionales: {string.Join(", ", experience.Pricing.AddOns.Select(addOn => $"{addOn.AddOn.Name} x{addOn.Quantity}"))}");
    }

    return string.Join(" | ", parts);
  }

  private sealed record ResolvedExtraLine(int ExtraId, string DisplayName, string? Description, decimal UnitPrice, int Quantity);

  private sealed record ResolvedExperienceLine(
    ExperienceCatalogItemDto Experience,
    ExperiencePackageOptionDto Package,
    BonhomiaSelectedExperienceRequest Request,
    ExperiencePricingResult Pricing);

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
    public int ExtraID { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal Price { get; set; }
  }

}
