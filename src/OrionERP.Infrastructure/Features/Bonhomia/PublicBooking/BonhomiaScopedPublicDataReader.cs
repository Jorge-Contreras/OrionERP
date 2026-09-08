using OrionERP.Application.Features.Reservaciones;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Features.Bonhomia.PublicBooking;
using OrionERP.Application.Features.Reservaciones.Experiencias;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;
using OrionERP.Infrastructure.Features.Reservaciones;

namespace OrionERP.Infrastructure.Features.Bonhomia.PublicBooking;

/// <summary>
/// Public hospitality reader whose SQL always contains the verified composite
/// company/site scope. It intentionally does not call the global reservation
/// calendar or experience catalog readers used by the management console.
/// </summary>
public sealed class BonhomiaScopedPublicDataReader : IBonhomiaScopedPublicDataReader
{
  private readonly string _connectionString;
  private readonly IHospitalityWebsiteScopeAccessor _scopeAccessor;
  private readonly HospitalityWebsiteDefinition _hospitalityWebsite;

  public BonhomiaScopedPublicDataReader(
    IConfiguration configuration,
    IHospitalityWebsiteScopeAccessor scopeAccessor,
    HospitalityWebsiteDefinition hospitalityWebsite)
  {
    _connectionString = configuration.GetConnectionString("OrionDb")
      ?? throw new InvalidOperationException("Missing ConnectionStrings:OrionDb.");
    _scopeAccessor = scopeAccessor ?? throw new ArgumentNullException(nameof(scopeAccessor));
    _hospitalityWebsite = hospitalityWebsite ?? throw new ArgumentNullException(nameof(hospitalityWebsite));
  }

  public async Task<RoomCalendarTimelineDto> GetCalendarTimelineAsync(
    DateOnly startDate,
    DateOnly endDateExclusive,
    CancellationToken ct = default)
  {
    if (endDateExclusive <= startDate)
      throw new ArgumentException("EndDateExclusive must be after StartDate.", nameof(endDateExclusive));

    var scope = await _scopeAccessor.ResolveRequiredAsync(ct);
    await using var conn = new SqlConnection(_connectionString);
    await HospitalityConnectionFactory.InitializeAsync(conn, new HospitalityScope(scope.CompanyId, scope.SiteId, scope.CompanyRfc), ct);
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(
      CalendarTimelineSql,
      new
      {
        ScopeCompanyId = scope.CompanyId,
        ScopeSiteId = scope.SiteId,
        StartDate = startDate.ToDateTime(TimeOnly.MinValue),
        EndDateExclusive = endDateExclusive.ToDateTime(TimeOnly.MinValue),
        RoomType = "SUITE"
      },
      cancellationToken: ct));

    var resources = (await multi.ReadAsync<RoomCalendarResourceDto>()).AsList();
    var cells = (await multi.ReadAsync<RoomCalendarDayCellDto>()).AsList();
    return new RoomCalendarTimelineDto
    {
      StartDate = startDate.ToDateTime(TimeOnly.MinValue),
      EndDateExclusive = endDateExclusive.ToDateTime(TimeOnly.MinValue),
      Resources = resources,
      DayCells = cells,
      Events = Array.Empty<RoomCalendarEventDto>()
    };
  }

  public async Task<IReadOnlyList<BonhomiaExtraOptionDto>> GetExtraOptionsAsync(
    CancellationToken ct = default)
  {
    var scope = await _scopeAccessor.ResolveRequiredAsync(ct);
    await using var conn = new SqlConnection(_connectionString);
    await HospitalityConnectionFactory.InitializeAsync(conn, new HospitalityScope(scope.CompanyId, scope.SiteId, scope.CompanyRfc), ct);
    var rows = (await conn.QueryAsync<ExtraCatalogRow>(new CommandDefinition(
      ExtraCatalogSql,
      ScopeParameters(scope),
      cancellationToken: ct))).AsList();

    var options = new List<BonhomiaExtraOptionDto>();
    foreach (var item in _hospitalityWebsite.FeaturedExtras)
    {
      var matches = rows
        .Where(row => item.Aliases.Any(alias => NamesMatch(alias, row.Name)))
        .ToArray();
      if (matches.Length == 0)
      {
        throw new InvalidOperationException(
          $"Configured public extra '{item.Code}' does not match one active extra in the bound company/site catalog.");
      }
      if (matches.Length > 1)
      {
        throw new InvalidOperationException(
          $"Configured public extra '{item.Code}' is ambiguous in the bound company/site catalog.");
      }
      var match = matches[0];

      options.Add(new BonhomiaExtraOptionDto
      {
        Code = item.Code,
        Name = item.Name,
        Detail = item.Detail,
        CatalogName = match.Name,
        Icon = item.Icon,
        UnitPrice = match.Price,
        MaxQuantity = item.MaxQuantity
      });
    }

    return options;
  }

  public async Task<IReadOnlyList<ExperienceCatalogItemDto>> GetExperienceCatalogAsync(
    DateOnly startDate,
    DateOnly endDateExclusive,
    CancellationToken ct = default)
  {
    var scope = await _scopeAccessor.ResolveRequiredAsync(ct);
    await using var conn = new SqlConnection(_connectionString);
    await HospitalityConnectionFactory.InitializeAsync(conn, new HospitalityScope(scope.CompanyId, scope.SiteId, scope.CompanyRfc), ct);
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(
      ExperienceCatalogSql,
      new
      {
        ScopeCompanyId = scope.CompanyId,
        ScopeSiteId = scope.SiteId,
        StartDate = startDate.ToDateTime(TimeOnly.MinValue),
        EndDateExclusive = endDateExclusive.ToDateTime(TimeOnly.MinValue)
      },
      cancellationToken: ct));

    var experiences = (await multi.ReadAsync<ExperienceCatalogRow>()).AsList();
    var packages = (await multi.ReadAsync<ExperiencePackageOptionDto>()).AsList();
    var addOns = (await multi.ReadAsync<ExperienceAddOnOptionDto>()).AsList();
    var packagesByExperience = packages
      .GroupBy(item => item.ExperienceId)
      .ToDictionary(group => group.Key, group => (IReadOnlyList<ExperiencePackageOptionDto>)group.ToArray());
    var addOnsByExperience = addOns
      .GroupBy(item => item.ExperienceId)
      .ToDictionary(group => group.Key, group => (IReadOnlyList<ExperienceAddOnOptionDto>)group.ToArray());

    return experiences
      .Select(row => new ExperienceCatalogItemDto
      {
        ExperienceId = row.ExperienceID,
        Code = row.Code,
        Name = row.Name,
        Description = row.Description,
        Category = row.Category,
        ProviderName = row.ProviderName,
        SeasonStart = ToDateOnly(row.SeasonStart),
        SeasonEnd = ToDateOnly(row.SeasonEnd),
        MinimumParticipants = row.MinimumParticipants,
        MaximumParticipants = row.MaximumParticipants,
        IsPublic = row.IsPublic,
        IsActive = row.IsActive,
        Packages = packagesByExperience.TryGetValue(row.ExperienceID, out var packageItems)
          ? packageItems
          : Array.Empty<ExperiencePackageOptionDto>(),
        AddOns = addOnsByExperience.TryGetValue(row.ExperienceID, out var addOnItems)
          ? addOnItems
          : Array.Empty<ExperienceAddOnOptionDto>()
      })
      .Where(item => item.Packages.Count > 0)
      .ToArray();
  }

  public async Task<IReadOnlyList<int>> GetRoomCalendarIdsAsync(
    string roomName,
    DateOnly checkIn,
    DateOnly checkOut,
    CancellationToken ct = default)
  {
    var scope = await _scopeAccessor.ResolveRequiredAsync(ct);
    await using var conn = new SqlConnection(_connectionString);
    await HospitalityConnectionFactory.InitializeAsync(conn, new HospitalityScope(scope.CompanyId, scope.SiteId, scope.CompanyRfc), ct);
    var ids = await conn.QueryAsync<int>(new CommandDefinition(
      RoomCalendarIdsSql,
      new
      {
        ScopeCompanyId = scope.CompanyId,
        ScopeSiteId = scope.SiteId,
        RoomName = roomName,
        CheckIn = checkIn.ToDateTime(TimeOnly.MinValue),
        CheckOut = checkOut.ToDateTime(TimeOnly.MinValue)
      },
      cancellationToken: ct));
    return ids.AsList();
  }

  public async Task<ReservacionDetailDto?> GetReservationDetailAsync(
    int reservationId,
    CancellationToken ct = default)
  {
    var scope = await _scopeAccessor.ResolveRequiredAsync(ct);
    await using var conn = new SqlConnection(_connectionString);
    await HospitalityConnectionFactory.InitializeAsync(conn, new HospitalityScope(scope.CompanyId, scope.SiteId, scope.CompanyRfc), ct);
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(
      ReservationDetailSql,
      new
      {
        ReservationId = reservationId,
        ScopeCompanyId = scope.CompanyId,
        ScopeSiteId = scope.SiteId
      },
      cancellationToken: ct));

    var detail = await multi.ReadFirstOrDefaultAsync<ReservacionDetailDto>();
    if (detail is null)
      return null;

    var suites = (await multi.ReadAsync<ReservacionSuiteDto>()).AsList();
    var extras = (await multi.ReadAsync<ReservacionExtraDto>()).AsList();
    var experiences = (await multi.ReadAsync<ReservacionExperienceDto>()).AsList();
    var experienceAddOns = (await multi.ReadAsync<ReservacionExperienceAddOnDto>()).AsList();
    var payments = (await multi.ReadAsync<ReservacionPagoDto>()).AsList();
    var attachments = (await multi.ReadAsync<ReservacionAttachmentDto>()).AsList();
    var airbnb = await multi.ReadFirstOrDefaultAsync<AirbnbReservationBreakdownDto>();

    var addOnsByExperience = experienceAddOns
      .GroupBy(item => item.ReservationExperienceId)
      .ToDictionary(group => group.Key, group => (IReadOnlyList<ReservacionExperienceAddOnDto>)group.ToArray());
    foreach (var experience in experiences)
    {
      experience.AddOns = addOnsByExperience.TryGetValue(experience.Id, out var items)
        ? items
        : Array.Empty<ReservacionExperienceAddOnDto>();
    }

    ApplyCalculatedTotals(detail, suites, extras, experiences, payments);
    detail.Suites = suites;
    detail.Extras = extras;
    detail.Experiences = experiences;
    detail.Pagos = payments;
    detail.Attachments = attachments;
    detail.AirbnbBreakdown = airbnb;
    return detail;
  }

  public async Task ValidateSchemaAsync(CancellationToken ct = default)
  {
    var scope = await _scopeAccessor.ResolveRequiredAsync(ct);
    await using var conn = new SqlConnection(_connectionString);
    await HospitalityConnectionFactory.InitializeAsync(conn, new HospitalityScope(scope.CompanyId, scope.SiteId, scope.CompanyRfc), ct);
    var valid = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
      SchemaReadinessSql,
      ScopeParameters(scope),
      cancellationToken: ct));
    if (!valid)
    {
      throw new InvalidOperationException(
        "Hospitality data isolation schema is missing or the configured website has no scoped room catalog.");
    }
  }

  private static object ScopeParameters(HospitalityWebsiteScope scope)
    => new
    {
      ScopeCompanyId = scope.CompanyId,
      ScopeSiteId = scope.SiteId
    };

  private static bool NamesMatch(string left, string right)
    => string.Equals(
      Application.Features.Reservaciones.ReservationCatalogNaming.NormalizeLookupKey(left),
      Application.Features.Reservaciones.ReservationCatalogNaming.NormalizeLookupKey(right),
      StringComparison.OrdinalIgnoreCase);

  private static DateOnly? ToDateOnly(DateTime? value)
    => value.HasValue ? DateOnly.FromDateTime(value.Value) : null;

  private static void ApplyCalculatedTotals(
    ReservacionDetailDto detail,
    IReadOnlyList<ReservacionSuiteDto> suites,
    IReadOnlyList<ReservacionExtraDto> extras,
    IReadOnlyList<ReservacionExperienceDto> experiences,
    IReadOnlyList<ReservacionPagoDto> payments)
  {
    var totals = ReservacionTotalsCalculator.Calculate(
      detail.CheckIn,
      detail.CheckOut,
      suites.Select(item => item.Precio),
      extras.Select(item => new ReservationChargeLine(item.Price, MapTaxMode(item.TaxMode))),
      experiences.Select(item => new ReservationChargeLine(item.Total, MapTaxMode(item.TaxMode))),
      payments.Sum(item => item.Monto),
      detail.SuiteDiscountPercent);

    detail.TotalSuites = totals.TotalSuites;
    detail.SuiteDiscountAmount = totals.SuiteDiscountAmount;
    detail.TotalExtras = totals.TotalExtras;
    detail.TotalExperiences = totals.TotalExperiences;
    detail.SubTotal = totals.SubTotal;
    detail.Tax = totals.Tax;
    detail.Ish = totals.Ish;
    detail.TotalPrice = totals.TotalReservacion;
    detail.Pagado = totals.TotalPagado;
    detail.PorPagar = totals.PorPagar;
    detail.NumNoches = totals.NumNoches;
  }

  private static ReservationChargeTaxMode MapTaxMode(string? value)
    => value switch
    {
      ExperienceTaxModes.TaxIncluded => ReservationChargeTaxMode.TaxIncluded,
      ExperienceTaxModes.NonTaxable => ReservationChargeTaxMode.NonTaxable,
      _ => ReservationChargeTaxMode.TaxableExclusive
    };

  internal const string CalendarTimelineSql = """
WITH ScopedRooms AS
(
    SELECT
        r.ID AS RoomId,
        r.ROOM_NAME AS RoomCode,
        r.ROOM_NAME AS RoomName,
        r.ROOM_TYPE AS RoomType,
        CAST(ISNULL(r.BASE_PRICE, 0) AS decimal(18,2)) AS BasePrice,
        ROW_NUMBER() OVER (ORDER BY r.ROOM_NAME) AS DisplayOrder,
        CAST(CASE WHEN EXISTS
        (
            SELECT 1
            FROM dbo.ROOM_CALENDAR rc
            WHERE rc.OrionCompanyId = @ScopeCompanyId
              AND rc.OrionSiteId = @ScopeSiteId
              AND rc.RoomId = r.ID
        ) THEN 1 ELSE 0 END AS bit) AS CalendarEnabled
    FROM dbo.ROOM r
    WHERE r.OrionCompanyId = @ScopeCompanyId
      AND r.OrionSiteId = @ScopeSiteId
      AND (@RoomType IS NULL OR r.ROOM_TYPE = @RoomType)
)
SELECT RoomId, RoomCode, RoomName, RoomType, BasePrice, DisplayOrder, CalendarEnabled
FROM ScopedRooms
WHERE CalendarEnabled = 1
ORDER BY DisplayOrder, RoomName;

WITH Dates AS
(
    SELECT CAST(@StartDate AS date) AS RoomDate
    UNION ALL
    SELECT DATEADD(day, 1, RoomDate)
    FROM Dates
    WHERE RoomDate < DATEADD(day, -1, CAST(@EndDateExclusive AS date))
),
ScopedRooms AS
(
    SELECT r.ID AS RoomId, r.ROOM_NAME AS RoomCode, r.ROOM_NAME AS RoomName,
           CAST(ISNULL(r.BASE_PRICE, 0) AS decimal(18,2)) AS BasePrice
    FROM dbo.ROOM r
    WHERE r.OrionCompanyId = @ScopeCompanyId
      AND r.OrionSiteId = @ScopeSiteId
      AND (@RoomType IS NULL OR r.ROOM_TYPE = @RoomType)
)
SELECT
    room.RoomId,
    room.RoomCode,
    room.RoomName,
    dates.RoomDate,
    rc.ID AS RoomCalendarId,
    CAST(ISNULL(rc.IS_LOCKED, 0) AS bit) AS IsLocked,
    NULLIF(LTRIM(RTRIM(rc.LOCKED_BY)), '') AS LockedBy,
    NULLIF(LTRIM(RTRIM(rc.LOCK_DESCRIPTION)), '') AS LockDescription,
    CASE
      WHEN rc.ID IS NULL THEN 'missing'
      WHEN ISNULL(rc.IS_LOCKED, 0) = 0 THEN 'available'
      WHEN TRY_CAST(rc.LOCK_DESCRIPTION AS int) IS NOT NULL AND reservation.ID IS NULL THEN 'orphan'
      WHEN reservation.ID IS NOT NULL
       AND UPPER(LTRIM(RTRIM(ISNULL(reservation.STATUS, '')))) COLLATE Latin1_General_100_CI_AI = N'COTIZACION' THEN 'soft_hold'
      WHEN reservation.ID IS NOT NULL THEN 'reserved'
      ELSE 'blocked'
    END AS StateCode,
    reservation.ID AS ReservationId,
    reservation.STATUS AS ReservationStatus,
    CAST(CASE WHEN reservation.ID IS NOT NULL AND dates.RoomDate = reservation.CHECKIN THEN 1 ELSE 0 END AS bit) AS IsArrival,
    CAST(CASE WHEN reservation.ID IS NOT NULL AND dates.RoomDate = DATEADD(day, -1, reservation.CHECKOUT) THEN 1 ELSE 0 END AS bit) AS IsDeparture,
    CAST(CASE WHEN reservation.ID IS NOT NULL AND EXISTS
    (
      SELECT 1 FROM dbo.Reservation_Extra extraLine
      WHERE extraLine.ReservationID = reservation.ID
        AND extraLine.OrionCompanyId = @ScopeCompanyId
        AND extraLine.OrionSiteId = @ScopeSiteId
    ) THEN 1 ELSE 0 END AS bit) AS HasExtras,
    CAST(ISNULL(rc.LIMPIEZA_PROFUNDA, 0) AS bit) AS HasDeepCleaning,
    CAST(ISNULL(rc.CHECK_DIARIO, 0) AS bit) AS HasDailyCheck,
    CAST(ISNULL(rc.PRECIO, room.BasePrice) AS decimal(18,2)) AS Price,
    rc.NOTES AS Notes,
    CASE
      WHEN rc.ID IS NULL THEN 'missing-room-calendar'
      WHEN ISNULL(rc.IS_LOCKED, 0) = 1
       AND TRY_CAST(rc.LOCK_DESCRIPTION AS int) IS NOT NULL
       AND reservation.ID IS NULL THEN 'missing-or-unscoped-reservation'
      ELSE NULL
    END AS DataQualityFlag
FROM ScopedRooms room
CROSS JOIN Dates dates
LEFT JOIN dbo.ROOM_CALENDAR rc
  ON rc.OrionCompanyId = @ScopeCompanyId
 AND rc.OrionSiteId = @ScopeSiteId
 AND rc.RoomId = room.RoomId
 AND rc.ROOM_DATE = dates.RoomDate
LEFT JOIN dbo.RESERVATION reservation
  ON reservation.OrionCompanyId = @ScopeCompanyId
 AND reservation.OrionSiteId = @ScopeSiteId
 AND reservation.ID = rc.ReservationId
ORDER BY room.RoomName, dates.RoomDate
OPTION (MAXRECURSION 32767);
""";

  internal const string ExtraCatalogSql = """
SELECT e.ExtraID, e.[Name], e.[Description],
       CAST(ISNULL(e.Price, 0) AS decimal(18,2)) AS Price
FROM dbo.Extra e
WHERE e.OrionCompanyId = @ScopeCompanyId
  AND e.OrionSiteId = @ScopeSiteId
  AND e.IsActive = 1;
""";

  internal const string RoomCalendarIdsSql = """
SELECT rc.ID
FROM dbo.ROOM_CALENDAR rc
WHERE rc.OrionCompanyId = @ScopeCompanyId
  AND rc.OrionSiteId = @ScopeSiteId
  AND rc.ROOM = @RoomName
  AND rc.ROOM_DATE >= @CheckIn
  AND rc.ROOM_DATE < @CheckOut
ORDER BY rc.ROOM_DATE;
""";

  internal const string ExperienceCatalogSql = """
SELECT
    e.ExperienceID, e.Code, e.[Name], e.[Description], e.Category,
    ISNULL(provider.[Name], '') AS ProviderName,
    e.SeasonStart, e.SeasonEnd, e.MinimumParticipants, e.MaximumParticipants,
    CAST(ISNULL(e.IsPublic, 0) AS bit) AS IsPublic,
    CAST(ISNULL(e.IsActive, 0) AS bit) AS IsActive
FROM dbo.Experience e
LEFT JOIN dbo.ExperienceProvider provider
  ON provider.ExperienceProviderID = e.ExperienceProviderID
 AND provider.OrionCompanyId = @ScopeCompanyId
 AND provider.OrionSiteId = @ScopeSiteId
WHERE e.OrionCompanyId = @ScopeCompanyId
  AND e.OrionSiteId = @ScopeSiteId
  AND e.IsActive = 1
  AND e.IsPublic = 1
  AND (e.SeasonStart IS NULL OR e.SeasonStart < @EndDateExclusive)
  AND (e.SeasonEnd IS NULL OR e.SeasonEnd >= @StartDate)
ORDER BY e.SeasonStart, e.[Name];

SELECT
    package.ExperiencePackageID, package.ExperienceID, package.Code,
    package.[Name], package.[Description], package.Includes,
    package.ProviderPackageName,
    CAST(ISNULL(package.UnitPrice, 0) AS decimal(18,2)) AS UnitPrice,
    ISNULL(package.TaxMode, 'TaxableExclusive') AS TaxMode,
    CAST(ISNULL(package.IsPublic, 0) AS bit) AS IsPublic,
    CAST(ISNULL(package.IsActive, 0) AS bit) AS IsActive,
    package.DisplayOrder
FROM dbo.ExperiencePackage package
INNER JOIN dbo.Experience e
  ON e.ExperienceID = package.ExperienceID
 AND e.OrionCompanyId = @ScopeCompanyId
 AND e.OrionSiteId = @ScopeSiteId
WHERE package.OrionCompanyId = @ScopeCompanyId
  AND package.OrionSiteId = @ScopeSiteId
  AND e.IsActive = 1 AND e.IsPublic = 1
  AND package.IsActive = 1 AND package.IsPublic = 1
ORDER BY package.ExperienceID, package.DisplayOrder, package.[Name];

SELECT
    addOn.ExperienceAddOnID, addOn.ExperienceID, addOn.Code,
    addOn.[Name], addOn.[Description],
    CAST(ISNULL(addOn.UnitPrice, 0) AS decimal(18,2)) AS UnitPrice,
    CAST(ISNULL(addOn.AppliesPerParticipant, 0) AS bit) AS AppliesPerParticipant,
    ISNULL(addOn.TaxMode, 'TaxableExclusive') AS TaxMode,
    CAST(ISNULL(addOn.IsPublic, 0) AS bit) AS IsPublic,
    CAST(ISNULL(addOn.IsActive, 0) AS bit) AS IsActive,
    addOn.DisplayOrder
FROM dbo.ExperienceAddOn addOn
INNER JOIN dbo.Experience e
  ON e.ExperienceID = addOn.ExperienceID
 AND e.OrionCompanyId = @ScopeCompanyId
 AND e.OrionSiteId = @ScopeSiteId
WHERE addOn.OrionCompanyId = @ScopeCompanyId
  AND addOn.OrionSiteId = @ScopeSiteId
  AND e.IsActive = 1 AND e.IsPublic = 1
  AND addOn.IsActive = 1 AND addOn.IsPublic = 1
ORDER BY addOn.ExperienceID, addOn.DisplayOrder, addOn.[Name];
""";

  internal const string ReservationDetailSql = """
SELECT TOP (1)
    reservation.ID AS Id, reservation.CLIENTE_ID AS ClienteId,
    ISNULL(customer.Nombre, '(Sin cliente)') AS Cliente,
    reservation.CHECKIN AS CheckIn, reservation.CHECKOUT AS CheckOut,
    reservation.STATUS AS Status, reservation.RECOMMENED_BY AS RecommenedBy,
    CAST(ISNULL(reservation.TAXABLE, 0) AS bit) AS RequiresCfdi,
    CAST(ISNULL(reservation.TOTAL_PRICE, 0) AS decimal(18,2)) AS TotalPrice,
    CAST(ISNULL(reservation.SUITE_DISCOUNT_PERCENT, 0) AS decimal(18,2)) AS SuiteDiscountPercent,
    reservation.NOTES AS Notes
FROM dbo.RESERVATION reservation
LEFT JOIN orion.HospitalitySiteCustomer customerScope
  ON customerScope.CompanyId = @ScopeCompanyId
 AND customerScope.SiteId = @ScopeSiteId
 AND customerScope.ClienteId = reservation.CLIENTE_ID
LEFT JOIN dbo.Clientes customer
  ON customer.ID = customerScope.ClienteId
WHERE reservation.ID = @ReservationId
  AND reservation.OrionCompanyId = @ScopeCompanyId
  AND reservation.OrionSiteId = @ScopeSiteId;

SELECT rc.ID AS Id, rc.ROOM_DATE AS Fecha, ISNULL(rc.ROOM, '') AS Suite,
       CAST(ISNULL(rc.PRECIO, 0) AS decimal(18,2)) AS Precio,
       rc.LOCK_DESCRIPTION AS LockDescription,
       CAST(ISNULL(rc.LIMPIEZA_PROFUNDA, 0) AS bit) AS LimpiezaProfunda
FROM dbo.ROOM_CALENDAR rc
WHERE rc.ReservationId = @ReservationId
  AND rc.OrionCompanyId = @ScopeCompanyId
  AND rc.OrionSiteId = @ScopeSiteId
ORDER BY rc.ROOM_DATE, rc.ROOM;

SELECT extraLine.ReservationExtraID AS Id, extraLine.ExtraID,
       ISNULL(extraLine.ExtraNameSnapshot, '') AS [Name],
       extraLine.ExtraDescriptionSnapshot AS [Description],
       CAST(ISNULL(extraLine.UnitPriceSnapshot, 0) AS decimal(18,2)) AS UnitPrice,
       ISNULL(extraLine.Quantity, 1) AS Quantity,
       CAST(ISNULL(extraLine.UnitPriceSnapshot, 0) * ISNULL(extraLine.Quantity, 1) AS decimal(18,2)) AS Price,
       ISNULL(extraLine.TaxMode, 'TaxableExclusive') AS TaxMode, extraLine.Notes
FROM dbo.Reservation_Extra extraLine
WHERE extraLine.ReservationID = @ReservationId
  AND extraLine.OrionCompanyId = @ScopeCompanyId
  AND extraLine.OrionSiteId = @ScopeSiteId
ORDER BY extraLine.ReservationExtraID;

SELECT line.ReservationExperienceID AS Id, line.ReservationID AS ReservationId,
       line.ExperienceID AS ExperienceId, line.ExperiencePackageID AS ExperiencePackageId,
       CAST(line.ExperienceDate AS datetime2) AS ExperienceDate,
       ISNULL(line.ExperienceNameSnapshot, '') AS ExperienceName,
       ISNULL(line.PackageNameSnapshot, '') AS PackageName,
       ISNULL(line.ProviderNameSnapshot, '') AS ProviderName,
       line.PackageIncludesSnapshot AS PackageIncludes,
       line.PayingParticipants AS AdultParticipants,
       line.NonPayingParticipants AS ChildParticipants,
       CAST(ISNULL(line.UnitPriceSnapshot, 0) AS decimal(18,2)) AS UnitPrice,
       CAST(ISNULL(line.PackageSubtotalSnapshot, 0) AS decimal(18,2)) AS PackageSubtotal,
       CAST(ISNULL(line.AddOnsTotalSnapshot, 0) AS decimal(18,2)) AS AddOnsTotal,
       CAST(ISNULL(line.TotalSnapshot, 0) AS decimal(18,2)) AS Total,
       ISNULL(line.TaxMode, 'TaxableExclusive') AS TaxMode, line.Notes
FROM dbo.Reservation_Experience line
WHERE line.ReservationID = @ReservationId
  AND line.OrionCompanyId = @ScopeCompanyId
  AND line.OrionSiteId = @ScopeSiteId
ORDER BY line.ExperienceDate, line.ReservationExperienceID;

SELECT addOn.ReservationExperienceAddOnID AS Id,
       addOn.ReservationExperienceID, addOn.ExperienceAddOnID,
       ISNULL(addOn.AddOnNameSnapshot, '') AS AddOnName,
       addOn.Quantity,
       CAST(ISNULL(addOn.UnitPriceSnapshot, 0) AS decimal(18,2)) AS UnitPrice,
       CAST(ISNULL(addOn.TotalSnapshot, 0) AS decimal(18,2)) AS Total,
       ISNULL(addOn.TaxMode, 'TaxableExclusive') AS TaxMode
FROM dbo.Reservation_ExperienceAddOn addOn
INNER JOIN dbo.Reservation_Experience line
  ON line.ReservationExperienceID = addOn.ReservationExperienceID
 AND line.OrionCompanyId = @ScopeCompanyId
 AND line.OrionSiteId = @ScopeSiteId
WHERE line.ReservationID = @ReservationId
  AND addOn.OrionCompanyId = @ScopeCompanyId
  AND addOn.OrionSiteId = @ScopeSiteId
ORDER BY addOn.ReservationExperienceAddOnID;

SELECT link.TransaccionID AS TransaccionId, payment.Fecha,
       ISNULL(payment.Concepto, '') AS Concepto,
       CAST(ISNULL(link.Amount, ISNULL(payment.Monto, 0)) AS decimal(18,2)) AS Monto
FROM dbo.Reservation_Transacciones link
INNER JOIN dbo.Transacciones payment
  ON payment.ID = link.TransaccionID AND payment.RFC = CONVERT(varchar(50), SESSION_CONTEXT(N'OrionERP.HospitalityRfc'))
WHERE link.ReservationID = @ReservationId
  AND link.OrionCompanyId = @ScopeCompanyId
  AND link.OrionSiteId = @ScopeSiteId
ORDER BY payment.Fecha DESC, link.TransaccionID DESC;

SELECT attachment.ID AS Id, attachment.ReservationID AS ReservationId,
       ISNULL(attachment.AttachmentName, CONCAT('Archivo ', attachment.ID)) AS AttachmentName,
       ISNULL(attachment.AttachmentExtension, '') AS AttachmentExtension,
       attachment.AttachmentDescription,
       CAST(DATALENGTH(attachment.Attachment) AS bigint) AS Length
FROM dbo.RESERVATION_ATTACHMENT attachment
WHERE attachment.ReservationID = @ReservationId
  AND attachment.OrionCompanyId = @ScopeCompanyId
  AND attachment.OrionSiteId = @ScopeSiteId
ORDER BY attachment.ID DESC;

SELECT breakdown.ReservationID AS ReservationId, breakdown.PayoutAmount,
       breakdown.TaxableBase, breakdown.RoomRateAmount, breakdown.CleaningFee,
       breakdown.IvaTransferredAmount, breakdown.IvaRetainedAmount,
       breakdown.IsrRetainedAmount, breakdown.HostServiceFeeBaseAmount,
       breakdown.HostServiceFeeIvaAmount, breakdown.HostServiceFeeTotalAmount,
       breakdown.GrossCfdiTotal, breakdown.IvaRate, breakdown.IvaRetentionRate,
       breakdown.IsrRetentionRate, breakdown.HostServiceFeeRate,
       breakdown.HostServiceFeeIvaRate, breakdown.CreatedAtUtc, breakdown.UpdatedAtUtc
FROM dbo.ReservationAirbnbBreakdown breakdown
WHERE breakdown.ReservationID = @ReservationId
  AND breakdown.OrionCompanyId = @ScopeCompanyId
  AND breakdown.OrionSiteId = @ScopeSiteId;
""";

  internal const string SchemaReadinessSql = """
WITH ExpectedForeignKeys AS
(
  SELECT foreignKeyName, parentObjectId, referencedObjectId
  FROM (VALUES
    (N'FK_ROOM_CALENDAR_OrionRoom', OBJECT_ID(N'dbo.ROOM_CALENDAR'), OBJECT_ID(N'dbo.ROOM')),
    (N'FK_ROOM_CALENDAR_OrionReservation', OBJECT_ID(N'dbo.ROOM_CALENDAR'), OBJECT_ID(N'dbo.RESERVATION')),
    (N'FK_RESERVATION_HospitalitySiteCustomer', OBJECT_ID(N'dbo.RESERVATION'), OBJECT_ID(N'orion.HospitalitySiteCustomer')),
    (N'FK_ReservationExtra_OrionReservation', OBJECT_ID(N'dbo.Reservation_Extra'), OBJECT_ID(N'dbo.RESERVATION')),
    (N'FK_ReservationExtra_OrionExtra', OBJECT_ID(N'dbo.Reservation_Extra'), OBJECT_ID(N'dbo.Extra')),
    (N'FK_ReservationTransactions_OrionReservation', OBJECT_ID(N'dbo.Reservation_Transacciones'), OBJECT_ID(N'dbo.RESERVATION')),
    (N'FK_ReservationAttachment_OrionReservation', OBJECT_ID(N'dbo.RESERVATION_ATTACHMENT'), OBJECT_ID(N'dbo.RESERVATION')),
    (N'FK_ReservationAirbnb_OrionReservation', OBJECT_ID(N'dbo.ReservationAirbnbBreakdown'), OBJECT_ID(N'dbo.RESERVATION')),
    (N'FK_ReservationExperience_OrionReservation', OBJECT_ID(N'dbo.Reservation_Experience'), OBJECT_ID(N'dbo.RESERVATION')),
    (N'FK_ReservationExperience_OrionExperience', OBJECT_ID(N'dbo.Reservation_Experience'), OBJECT_ID(N'dbo.Experience')),
    (N'FK_ReservationExperience_OrionPackage', OBJECT_ID(N'dbo.Reservation_Experience'), OBJECT_ID(N'dbo.ExperiencePackage')),
    (N'FK_ReservationExperienceAddOn_OrionParent', OBJECT_ID(N'dbo.Reservation_ExperienceAddOn'), OBJECT_ID(N'dbo.Reservation_Experience')),
    (N'FK_ReservationExperienceAddOn_OrionCatalog', OBJECT_ID(N'dbo.Reservation_ExperienceAddOn'), OBJECT_ID(N'dbo.ExperienceAddOn'))
  ) expected(foreignKeyName, parentObjectId, referencedObjectId)
),
ExpectedForeignKeyColumns AS
(
  SELECT foreignKeyName, columnOrdinal, parentColumnName, referencedColumnName
  FROM (VALUES
    (N'FK_ROOM_CALENDAR_OrionRoom', 1, N'OrionCompanyId', N'OrionCompanyId'),
    (N'FK_ROOM_CALENDAR_OrionRoom', 2, N'OrionSiteId', N'OrionSiteId'),
    (N'FK_ROOM_CALENDAR_OrionRoom', 3, N'RoomId', N'ID'),
    (N'FK_ROOM_CALENDAR_OrionReservation', 1, N'OrionCompanyId', N'OrionCompanyId'),
    (N'FK_ROOM_CALENDAR_OrionReservation', 2, N'OrionSiteId', N'OrionSiteId'),
    (N'FK_ROOM_CALENDAR_OrionReservation', 3, N'ReservationId', N'ID'),
    (N'FK_RESERVATION_HospitalitySiteCustomer', 1, N'OrionCompanyId', N'CompanyId'),
    (N'FK_RESERVATION_HospitalitySiteCustomer', 2, N'OrionSiteId', N'SiteId'),
    (N'FK_RESERVATION_HospitalitySiteCustomer', 3, N'CLIENTE_ID', N'ClienteId'),
    (N'FK_ReservationExtra_OrionReservation', 1, N'OrionCompanyId', N'OrionCompanyId'),
    (N'FK_ReservationExtra_OrionReservation', 2, N'OrionSiteId', N'OrionSiteId'),
    (N'FK_ReservationExtra_OrionReservation', 3, N'ReservationID', N'ID'),
    (N'FK_ReservationExtra_OrionExtra', 1, N'OrionCompanyId', N'OrionCompanyId'),
    (N'FK_ReservationExtra_OrionExtra', 2, N'OrionSiteId', N'OrionSiteId'),
    (N'FK_ReservationExtra_OrionExtra', 3, N'ExtraID', N'ExtraID'),
    (N'FK_ReservationTransactions_OrionReservation', 1, N'OrionCompanyId', N'OrionCompanyId'),
    (N'FK_ReservationTransactions_OrionReservation', 2, N'OrionSiteId', N'OrionSiteId'),
    (N'FK_ReservationTransactions_OrionReservation', 3, N'ReservationID', N'ID'),
    (N'FK_ReservationAttachment_OrionReservation', 1, N'OrionCompanyId', N'OrionCompanyId'),
    (N'FK_ReservationAttachment_OrionReservation', 2, N'OrionSiteId', N'OrionSiteId'),
    (N'FK_ReservationAttachment_OrionReservation', 3, N'ReservationID', N'ID'),
    (N'FK_ReservationAirbnb_OrionReservation', 1, N'OrionCompanyId', N'OrionCompanyId'),
    (N'FK_ReservationAirbnb_OrionReservation', 2, N'OrionSiteId', N'OrionSiteId'),
    (N'FK_ReservationAirbnb_OrionReservation', 3, N'ReservationID', N'ID'),
    (N'FK_ReservationExperience_OrionReservation', 1, N'OrionCompanyId', N'OrionCompanyId'),
    (N'FK_ReservationExperience_OrionReservation', 2, N'OrionSiteId', N'OrionSiteId'),
    (N'FK_ReservationExperience_OrionReservation', 3, N'ReservationID', N'ID'),
    (N'FK_ReservationExperience_OrionExperience', 1, N'OrionCompanyId', N'OrionCompanyId'),
    (N'FK_ReservationExperience_OrionExperience', 2, N'OrionSiteId', N'OrionSiteId'),
    (N'FK_ReservationExperience_OrionExperience', 3, N'ExperienceID', N'ExperienceID'),
    (N'FK_ReservationExperience_OrionPackage', 1, N'OrionCompanyId', N'OrionCompanyId'),
    (N'FK_ReservationExperience_OrionPackage', 2, N'OrionSiteId', N'OrionSiteId'),
    (N'FK_ReservationExperience_OrionPackage', 3, N'ExperiencePackageID', N'ExperiencePackageID'),
    (N'FK_ReservationExperienceAddOn_OrionParent', 1, N'OrionCompanyId', N'OrionCompanyId'),
    (N'FK_ReservationExperienceAddOn_OrionParent', 2, N'OrionSiteId', N'OrionSiteId'),
    (N'FK_ReservationExperienceAddOn_OrionParent', 3, N'ReservationExperienceID', N'ReservationExperienceID'),
    (N'FK_ReservationExperienceAddOn_OrionCatalog', 1, N'OrionCompanyId', N'OrionCompanyId'),
    (N'FK_ReservationExperienceAddOn_OrionCatalog', 2, N'OrionSiteId', N'OrionSiteId'),
    (N'FK_ReservationExperienceAddOn_OrionCatalog', 3, N'ExperienceAddOnID', N'ExperienceAddOnID')
  ) expected(foreignKeyName, columnOrdinal, parentColumnName, referencedColumnName)
),
ExpectedCalendarIndexes AS
(
  SELECT indexName, keyColumnCount, normalizedFilter
  FROM (VALUES
    (N'UX_ROOM_CALENDAR_OrionScope_Room_Date', 4, N'orioncompanyidisnotnullandorionsiteidisnotnullandroomidisnotnull'),
    (N'UX_ROOM_CALENDAR_Unscoped_Room_Date', 2, N'orioncompanyidisnullandorionsiteidisnull')
  ) expected(indexName, keyColumnCount, normalizedFilter)
),
ExpectedCalendarIndexColumns AS
(
  SELECT indexName, keyOrdinal, columnName
  FROM (VALUES
    (N'UX_ROOM_CALENDAR_OrionScope_Room_Date', 1, N'OrionCompanyId'),
    (N'UX_ROOM_CALENDAR_OrionScope_Room_Date', 2, N'OrionSiteId'),
    (N'UX_ROOM_CALENDAR_OrionScope_Room_Date', 3, N'RoomId'),
    (N'UX_ROOM_CALENDAR_OrionScope_Room_Date', 4, N'ROOM_DATE'),
    (N'UX_ROOM_CALENDAR_Unscoped_Room_Date', 1, N'ROOM'),
    (N'UX_ROOM_CALENDAR_Unscoped_Room_Date', 2, N'ROOM_DATE')
  ) expected(indexName, keyOrdinal, columnName)
)
SELECT CAST(CASE WHEN
  OBJECT_ID(N'orion.SchemaMigration', N'U') IS NOT NULL
  AND EXISTS
  (
    SELECT 1 FROM orion.SchemaMigration
    WHERE MigrationId = N'20260903_hospitality_public_scope_sandbox'
  )
  AND EXISTS
  (
    SELECT 1 FROM orion.SchemaMigration
    WHERE MigrationId = N'20260905_hospitality_legal_consent_sandbox'
  )
  AND COL_LENGTH(N'dbo.ROOM', N'OrionCompanyId') IS NOT NULL
  AND COL_LENGTH(N'dbo.ROOM', N'OrionSiteId') IS NOT NULL
  AND COL_LENGTH(N'dbo.ROOM_CALENDAR', N'OrionCompanyId') IS NOT NULL
  AND COL_LENGTH(N'dbo.ROOM_CALENDAR', N'OrionSiteId') IS NOT NULL
  AND COL_LENGTH(N'dbo.ROOM_CALENDAR', N'RoomId') IS NOT NULL
  AND COL_LENGTH(N'dbo.ROOM_CALENDAR', N'ReservationId') IS NOT NULL
  AND COL_LENGTH(N'dbo.RESERVATION', N'OrionCompanyId') IS NOT NULL
  AND COL_LENGTH(N'dbo.RESERVATION', N'OrionSiteId') IS NOT NULL
  AND EXISTS
  (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.RESERVATION')
      AND name = N'PrivacyVersionAccepted'
      AND system_type_id = TYPE_ID(N'nvarchar')
      AND max_length = 60
      AND is_nullable = 1
  )
  AND EXISTS
  (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.RESERVATION')
      AND name = N'TermsVersionAccepted'
      AND system_type_id = TYPE_ID(N'nvarchar')
      AND max_length = 60
      AND is_nullable = 1
  )
  AND EXISTS
  (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.RESERVATION')
      AND name = N'LegalAcceptedAtUtc'
      AND system_type_id = TYPE_ID(N'datetime2')
      AND scale = 0
      AND is_nullable = 1
  )
  AND EXISTS
  (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.RESERVATION')
      AND name = N'CK_RESERVATION_HospitalityLegalConsent_AllOrNone'
      AND is_disabled = 0
      AND is_not_trusted = 0
  )
  AND OBJECT_ID(N'orion.HospitalitySiteCustomer', N'U') IS NOT NULL
  AND EXISTS
  (
    SELECT 1
    FROM sys.indexes customerIndex
    INNER JOIN sys.index_columns customerKey
      ON customerKey.object_id = customerIndex.object_id
     AND customerKey.index_id = customerIndex.index_id
     AND customerKey.key_ordinal = 1
     AND customerKey.is_included_column = 0
     AND customerKey.is_descending_key = 0
    INNER JOIN sys.columns customerColumn
      ON customerColumn.object_id = customerKey.object_id
     AND customerColumn.column_id = customerKey.column_id
    WHERE customerIndex.object_id = OBJECT_ID(N'orion.HospitalitySiteCustomer')
      AND customerIndex.name = N'UQ_orion_HospitalitySiteCustomer_Cliente'
      AND customerIndex.is_unique = 1
      AND customerIndex.is_unique_constraint = 1
      AND customerIndex.is_disabled = 0
      AND customerIndex.is_hypothetical = 0
      AND customerIndex.has_filter = 0
      AND customerIndex.filter_definition IS NULL
      AND customerColumn.name = N'ClienteId'
      AND 1 =
      (
        SELECT COUNT_BIG(*)
        FROM sys.index_columns keyColumn
        WHERE keyColumn.object_id = customerIndex.object_id
          AND keyColumn.index_id = customerIndex.index_id
          AND keyColumn.key_ordinal > 0
      )
      AND NOT EXISTS
      (
        SELECT 1
        FROM sys.index_columns includedColumn
        WHERE includedColumn.object_id = customerIndex.object_id
          AND includedColumn.index_id = customerIndex.index_id
          AND includedColumn.is_included_column = 1
      )
  )
  AND COL_LENGTH(N'dbo.Extra', N'OrionCompanyId') IS NOT NULL
  AND COL_LENGTH(N'dbo.Extra', N'OrionSiteId') IS NOT NULL
  AND COL_LENGTH(N'dbo.Reservation_Extra', N'OrionCompanyId') IS NOT NULL
  AND COL_LENGTH(N'dbo.Reservation_Extra', N'OrionSiteId') IS NOT NULL
  AND COL_LENGTH(N'dbo.Reservation_Transacciones', N'OrionCompanyId') IS NOT NULL
  AND COL_LENGTH(N'dbo.Reservation_Transacciones', N'OrionSiteId') IS NOT NULL
  AND COL_LENGTH(N'dbo.RESERVATION_ATTACHMENT', N'OrionCompanyId') IS NOT NULL
  AND COL_LENGTH(N'dbo.RESERVATION_ATTACHMENT', N'OrionSiteId') IS NOT NULL
  AND COL_LENGTH(N'dbo.ReservationAirbnbBreakdown', N'OrionCompanyId') IS NOT NULL
  AND COL_LENGTH(N'dbo.ReservationAirbnbBreakdown', N'OrionSiteId') IS NOT NULL
  AND COL_LENGTH(N'dbo.ExperienceProvider', N'OrionCompanyId') IS NOT NULL
  AND COL_LENGTH(N'dbo.ExperienceProvider', N'OrionSiteId') IS NOT NULL
  AND COL_LENGTH(N'dbo.Experience', N'OrionCompanyId') IS NOT NULL
  AND COL_LENGTH(N'dbo.Experience', N'OrionSiteId') IS NOT NULL
  AND COL_LENGTH(N'dbo.ExperiencePackage', N'OrionCompanyId') IS NOT NULL
  AND COL_LENGTH(N'dbo.ExperiencePackage', N'OrionSiteId') IS NOT NULL
  AND COL_LENGTH(N'dbo.ExperienceAddOn', N'OrionCompanyId') IS NOT NULL
  AND COL_LENGTH(N'dbo.ExperienceAddOn', N'OrionSiteId') IS NOT NULL
  AND COL_LENGTH(N'dbo.Reservation_Experience', N'OrionCompanyId') IS NOT NULL
  AND COL_LENGTH(N'dbo.Reservation_Experience', N'OrionSiteId') IS NOT NULL
  AND COL_LENGTH(N'dbo.Reservation_ExperienceAddOn', N'OrionCompanyId') IS NOT NULL
  AND COL_LENGTH(N'dbo.Reservation_ExperienceAddOn', N'OrionSiteId') IS NOT NULL
  AND NOT EXISTS
  (
    SELECT 1
    FROM ExpectedForeignKeys expected
    WHERE NOT EXISTS
    (
      SELECT 1
      FROM sys.foreign_keys foreignKey
      WHERE foreignKey.name = expected.foreignKeyName
        AND foreignKey.parent_object_id = expected.parentObjectId
        AND foreignKey.referenced_object_id = expected.referencedObjectId
        AND foreignKey.is_disabled = 0
        AND foreignKey.is_not_trusted = 0
        AND 3 =
        (
          SELECT COUNT_BIG(*)
          FROM sys.foreign_key_columns actualColumn
          WHERE actualColumn.constraint_object_id = foreignKey.object_id
        )
        AND NOT EXISTS
        (
          SELECT 1
          FROM ExpectedForeignKeyColumns expectedColumn
          WHERE expectedColumn.foreignKeyName = expected.foreignKeyName
            AND NOT EXISTS
            (
              SELECT 1
              FROM sys.foreign_key_columns actualColumn
              WHERE actualColumn.constraint_object_id = foreignKey.object_id
                AND actualColumn.constraint_column_id = expectedColumn.columnOrdinal
                AND COL_NAME(actualColumn.parent_object_id, actualColumn.parent_column_id) = expectedColumn.parentColumnName
                AND COL_NAME(actualColumn.referenced_object_id, actualColumn.referenced_column_id) = expectedColumn.referencedColumnName
            )
        )
    )
  )
  AND NOT EXISTS
  (
    SELECT 1
    FROM ExpectedCalendarIndexes expectedIndex
    WHERE NOT EXISTS
    (
      SELECT 1
      FROM sys.indexes indexInfo
      WHERE indexInfo.object_id = OBJECT_ID(N'dbo.ROOM_CALENDAR')
        AND indexInfo.name = expectedIndex.indexName
        AND indexInfo.is_unique = 1
        AND indexInfo.is_disabled = 0
        AND indexInfo.is_hypothetical = 0
        AND indexInfo.has_filter = 1
        AND LOWER(
          REPLACE(
            REPLACE(
              REPLACE(
                REPLACE(
                  REPLACE(
                    REPLACE(
                      REPLACE(
                        REPLACE(indexInfo.filter_definition, N'[', N''),
                        N']', N''),
                      N'(', N''),
                    N')', N''),
                  N' ', N''),
                NCHAR(9), N''),
              NCHAR(10), N''),
            NCHAR(13), N'')) = expectedIndex.normalizedFilter
        AND expectedIndex.keyColumnCount =
        (
          SELECT COUNT_BIG(*)
          FROM sys.index_columns actualKey
          WHERE actualKey.object_id = indexInfo.object_id
            AND actualKey.index_id = indexInfo.index_id
            AND actualKey.key_ordinal > 0
        )
        AND NOT EXISTS
        (
          SELECT 1
          FROM sys.index_columns includedColumn
          WHERE includedColumn.object_id = indexInfo.object_id
            AND includedColumn.index_id = indexInfo.index_id
            AND includedColumn.is_included_column = 1
        )
        AND NOT EXISTS
        (
          SELECT 1
          FROM ExpectedCalendarIndexColumns expectedColumn
          WHERE expectedColumn.indexName = expectedIndex.indexName
            AND NOT EXISTS
            (
              SELECT 1
              FROM sys.index_columns actualKey
              INNER JOIN sys.columns actualColumn
                ON actualColumn.object_id = actualKey.object_id
               AND actualColumn.column_id = actualKey.column_id
              WHERE actualKey.object_id = indexInfo.object_id
                AND actualKey.index_id = indexInfo.index_id
                AND actualKey.key_ordinal = expectedColumn.keyOrdinal
                AND actualKey.is_included_column = 0
                AND actualKey.is_descending_key = 0
                AND actualColumn.name = expectedColumn.columnName
            )
        )
    )
  )
  AND EXISTS
  (
    SELECT 1 FROM dbo.ROOM
    WHERE OrionCompanyId = @ScopeCompanyId AND OrionSiteId = @ScopeSiteId
  )
THEN 1 ELSE 0 END AS bit);
""";

  private sealed class ExtraCatalogRow
  {
    public int ExtraID { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal Price { get; set; }
  }

  private sealed class ExperienceCatalogRow
  {
    public int ExperienceID { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Category { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public DateTime? SeasonStart { get; set; }
    public DateTime? SeasonEnd { get; set; }
    public int MinimumParticipants { get; set; }
    public int? MaximumParticipants { get; set; }
    public bool IsPublic { get; set; }
    public bool IsActive { get; set; }
  }

}
