using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Features.Reservaciones.Experiencias;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;
using OrionERP.Infrastructure.Features.Reservaciones;

namespace OrionERP.Infrastructure.Features.Reservaciones.Experiencias;

public sealed class ReservacionExperiencesService : IReservacionExperiencesService
{
  private readonly string _connectionString;

  public ReservacionExperiencesService(IConfiguration configuration)
  {
    _connectionString = configuration.GetConnectionString("OrionDb")
      ?? throw new InvalidOperationException("Missing ConnectionStrings:OrionDb.");
  }

  public Task<IReadOnlyList<ExperienceCatalogItemDto>> GetActiveExperienceCatalogAsync(CancellationToken ct = default)
    => GetCatalogAsync(publicOnly: false, startDate: null, endDateExclusive: null, ct);

  public Task<IReadOnlyList<ExperienceCatalogItemDto>> GetPublicExperienceCatalogAsync(
    DateOnly startDate,
    DateOnly endDateExclusive,
    CancellationToken ct = default)
    => GetCatalogAsync(publicOnly: true, startDate, endDateExclusive, ct);

  public async Task<IReadOnlyList<ReservacionExperienceDto>> GetExperiencesAsync(int reservationId, CancellationToken ct = default)
  {
    const string sql = """
SELECT
    re.ReservationExperienceID AS Id,
    re.ReservationID AS ReservationId,
    re.ExperienceID AS ExperienceId,
    re.ExperiencePackageID AS ExperiencePackageId,
    CAST(re.ExperienceDate AS datetime2) AS ExperienceDate,
    ISNULL(re.ExperienceNameSnapshot, '') AS ExperienceName,
    ISNULL(re.PackageNameSnapshot, '') AS PackageName,
    ISNULL(re.ProviderNameSnapshot, '') AS ProviderName,
    re.PackageIncludesSnapshot AS PackageIncludes,
    re.PayingParticipants AS AdultParticipants,
    re.NonPayingParticipants AS ChildParticipants,
    CAST(ISNULL(re.UnitPriceSnapshot, 0) AS decimal(18,2)) AS UnitPrice,
    CAST(ISNULL(re.PackageSubtotalSnapshot, 0) AS decimal(18,2)) AS PackageSubtotal,
    CAST(ISNULL(re.AddOnsTotalSnapshot, 0) AS decimal(18,2)) AS AddOnsTotal,
    CAST(ISNULL(re.TotalSnapshot, 0) AS decimal(18,2)) AS Total,
    ISNULL(re.TaxMode, 'TaxableExclusive') AS TaxMode,
    re.Notes
FROM dbo.Reservation_Experience re
WHERE re.ReservationID = @ReservationId
ORDER BY re.ExperienceDate, re.ReservationExperienceID;

SELECT
    rea.ReservationExperienceAddOnID AS Id,
    rea.ReservationExperienceID,
    rea.ExperienceAddOnID,
    ISNULL(rea.AddOnNameSnapshot, '') AS AddOnName,
    rea.Quantity,
    CAST(ISNULL(rea.UnitPriceSnapshot, 0) AS decimal(18,2)) AS UnitPrice,
    CAST(ISNULL(rea.TotalSnapshot, 0) AS decimal(18,2)) AS Total,
    ISNULL(rea.TaxMode, 'TaxableExclusive') AS TaxMode
FROM dbo.Reservation_ExperienceAddOn rea
INNER JOIN dbo.Reservation_Experience re
    ON re.ReservationExperienceID = rea.ReservationExperienceID
WHERE re.ReservationID = @ReservationId
ORDER BY rea.ReservationExperienceAddOnID;
""";

    await using var conn = new SqlConnection(_connectionString);
    await conn.OpenAsync(ct);
    if (!await ReservationExperienceTablesExistAsync(conn, ct))
    {
      return Array.Empty<ReservacionExperienceDto>();
    }

    using var multi = await conn.QueryMultipleAsync(
      new CommandDefinition(sql, new { ReservationId = reservationId }, cancellationToken: ct));

    var experiences = (await multi.ReadAsync<ReservacionExperienceDto>()).AsList();
    var addOns = (await multi.ReadAsync<ReservacionExperienceAddOnDto>()).AsList();
    var addOnsByExperience = addOns
      .GroupBy(item => item.ReservationExperienceId)
      .ToDictionary(group => group.Key, group => (IReadOnlyList<ReservacionExperienceAddOnDto>)group.ToArray());

    foreach (var experience in experiences)
    {
      experience.AddOns = addOnsByExperience.TryGetValue(experience.Id, out var items)
        ? items
        : Array.Empty<ReservacionExperienceAddOnDto>();
    }

    return experiences;
  }

  public async Task<ReservacionCommandResult> AddExperienceAsync(ReservacionExperienceCreateRequest request, CancellationToken ct = default)
  {
    if (request is null)
      throw new ArgumentNullException(nameof(request));

    var validation = await BuildValidatedPricingAsync(request, ct);
    if (!validation.Success)
      return ReservacionCommandResult.Fail(validation.Message);

    await using var conn = new SqlConnection(_connectionString);
    await conn.OpenAsync(ct);
    await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

    try
    {
      var reservationExperienceId = await InsertReservationExperienceAsync(conn, tx, request, validation, ct);
      await InsertReservationExperienceAddOnsAsync(conn, tx, reservationExperienceId, validation.Pricing.AddOns, ct);
      await ReservationStoredTotalSynchronizer.RecalculateAsync(conn, tx, request.ReservationId, ct);
      await tx.CommitAsync(ct);
      return ReservacionCommandResult.Ok("Experiencia agregada.");
    }
    catch
    {
      try { await tx.RollbackAsync(ct); } catch { /* ignore */ }
      throw;
    }
  }

  public async Task<ReservacionCommandResult> UpdateExperienceAsync(ReservacionExperienceUpdateRequest request, CancellationToken ct = default)
  {
    if (request is null)
      throw new ArgumentNullException(nameof(request));

    if (request.Id <= 0)
      return ReservacionCommandResult.Fail("Selecciona una experiencia valida.");

    var validation = await BuildValidatedPricingAsync(request, ct);
    if (!validation.Success)
      return ReservacionCommandResult.Fail(validation.Message);

    await using var conn = new SqlConnection(_connectionString);
    await conn.OpenAsync(ct);
    await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

    try
    {
      var affected = await conn.ExecuteAsync(
        new CommandDefinition(
          """
UPDATE dbo.Reservation_Experience
SET
    ExperienceID = @ExperienceId,
    ExperiencePackageID = @ExperiencePackageId,
    ExperienceDate = @ExperienceDate,
    ExperienceNameSnapshot = @ExperienceName,
    PackageNameSnapshot = @PackageName,
    ProviderNameSnapshot = @ProviderName,
    PackageIncludesSnapshot = @PackageIncludes,
    PayingParticipants = @AdultParticipants,
    NonPayingParticipants = @ChildParticipants,
    UnitPriceSnapshot = @UnitPrice,
    PackageSubtotalSnapshot = @PackageSubtotal,
    AddOnsTotalSnapshot = @AddOnsTotal,
    TotalSnapshot = @Total,
    TaxMode = @TaxMode,
    Notes = @Notes,
    UpdatedAtUtc = SYSUTCDATETIME()
WHERE ReservationExperienceID = @Id
  AND ReservationID = @ReservationId;
""",
          BuildReservationExperienceParameters(request, validation) with { Id = request.Id },
          tx,
          cancellationToken: ct));

      if (affected == 0)
      {
        await tx.RollbackAsync(ct);
        return ReservacionCommandResult.Fail("No se encontro la experiencia seleccionada.");
      }

      await conn.ExecuteAsync(
        new CommandDefinition(
          "DELETE FROM dbo.Reservation_ExperienceAddOn WHERE ReservationExperienceID = @Id;",
          new { request.Id },
          tx,
          cancellationToken: ct));

      await InsertReservationExperienceAddOnsAsync(conn, tx, request.Id, validation.Pricing.AddOns, ct);
      await ReservationStoredTotalSynchronizer.RecalculateAsync(conn, tx, request.ReservationId, ct);
      await tx.CommitAsync(ct);
      return ReservacionCommandResult.Ok("Experiencia actualizada.");
    }
    catch
    {
      try { await tx.RollbackAsync(ct); } catch { /* ignore */ }
      throw;
    }
  }

  public async Task<ReservacionCommandResult> DeleteExperienceAsync(int reservationExperienceId, CancellationToken ct = default)
  {
    const string sql = """
DECLARE @ReservationId int=(SELECT ReservationID FROM dbo.Reservation_Experience WITH (UPDLOCK) WHERE ReservationExperienceID=@Id);
DELETE FROM dbo.Reservation_Experience WHERE ReservationExperienceID=@Id;
SELECT @ReservationId;
""";

    await using var conn = new SqlConnection(_connectionString);
    await conn.OpenAsync(ct);
    if (!await ReservationExperienceTablesExistAsync(conn, ct))
    {
      return ReservacionCommandResult.Fail("La infraestructura de experiencias aun no esta instalada.");
    }

    await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
    var reservationId = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(sql, new { Id = reservationExperienceId }, tx, cancellationToken: ct));
    var affected = reservationId.HasValue ? 1 : 0;
    if (reservationId.HasValue)
      await ReservationStoredTotalSynchronizer.RecalculateAsync(conn, tx, reservationId.Value, ct);
    await tx.CommitAsync(ct);

    return affected > 0
      ? ReservacionCommandResult.Ok("Experiencia eliminada.")
      : ReservacionCommandResult.Fail("No se encontro la experiencia seleccionada.");
  }

  private async Task<IReadOnlyList<ExperienceCatalogItemDto>> GetCatalogAsync(
    bool publicOnly,
    DateOnly? startDate,
    DateOnly? endDateExclusive,
    CancellationToken ct)
  {
    const string sql = """
SELECT
    e.ExperienceID,
    e.Code,
    e.[Name],
    e.[Description],
    e.Category,
    ISNULL(p.[Name], '') AS ProviderName,
    e.SeasonStart,
    e.SeasonEnd,
    e.MinimumParticipants,
    e.MaximumParticipants,
    CAST(ISNULL(e.IsPublic, 0) AS bit) AS IsPublic,
    CAST(ISNULL(e.IsActive, 0) AS bit) AS IsActive
FROM dbo.Experience e
LEFT JOIN dbo.ExperienceProvider p
    ON p.ExperienceProviderID = e.ExperienceProviderID
WHERE e.IsActive = 1
  AND (@PublicOnly = 0 OR e.IsPublic = 1)
  AND (
        @StartDate IS NULL
        OR (
            (e.SeasonStart IS NULL OR e.SeasonStart < @EndDateExclusive)
            AND (e.SeasonEnd IS NULL OR e.SeasonEnd >= @StartDate)
        )
      )
ORDER BY e.SeasonStart, e.[Name];

SELECT
    ep.ExperiencePackageID,
    ep.ExperienceID,
    ep.Code,
    ep.[Name],
    ep.[Description],
    ep.Includes,
    ep.ProviderPackageName,
    CAST(ISNULL(ep.UnitPrice, 0) AS decimal(18,2)) AS UnitPrice,
    ISNULL(ep.TaxMode, 'TaxableExclusive') AS TaxMode,
    CAST(ISNULL(ep.IsPublic, 0) AS bit) AS IsPublic,
    CAST(ISNULL(ep.IsActive, 0) AS bit) AS IsActive,
    ep.DisplayOrder
FROM dbo.ExperiencePackage ep
INNER JOIN dbo.Experience e
    ON e.ExperienceID = ep.ExperienceID
WHERE e.IsActive = 1
  AND ep.IsActive = 1
  AND (@PublicOnly = 0 OR (e.IsPublic = 1 AND ep.IsPublic = 1))
ORDER BY ep.ExperienceID, ep.DisplayOrder, ep.[Name];

SELECT
    ea.ExperienceAddOnID,
    ea.ExperienceID,
    ea.Code,
    ea.[Name],
    ea.[Description],
    CAST(ISNULL(ea.UnitPrice, 0) AS decimal(18,2)) AS UnitPrice,
    CAST(ISNULL(ea.AppliesPerParticipant, 0) AS bit) AS AppliesPerParticipant,
    ISNULL(ea.TaxMode, 'TaxableExclusive') AS TaxMode,
    CAST(ISNULL(ea.IsPublic, 0) AS bit) AS IsPublic,
    CAST(ISNULL(ea.IsActive, 0) AS bit) AS IsActive,
    ea.DisplayOrder
FROM dbo.ExperienceAddOn ea
INNER JOIN dbo.Experience e
    ON e.ExperienceID = ea.ExperienceID
WHERE e.IsActive = 1
  AND ea.IsActive = 1
  AND (@PublicOnly = 0 OR (e.IsPublic = 1 AND ea.IsPublic = 1))
ORDER BY ea.ExperienceID, ea.DisplayOrder, ea.[Name];
""";

    await using var conn = new SqlConnection(_connectionString);
    await conn.OpenAsync(ct);
    if (!await CatalogTablesExistAsync(conn, ct))
    {
      return Array.Empty<ExperienceCatalogItemDto>();
    }

    using var multi = await conn.QueryMultipleAsync(
      new CommandDefinition(
        sql,
        new
        {
          PublicOnly = publicOnly,
          StartDate = startDate?.ToDateTime(TimeOnly.MinValue),
          EndDateExclusive = endDateExclusive?.ToDateTime(TimeOnly.MinValue)
        },
        cancellationToken: ct));

    var rows = (await multi.ReadAsync<ExperienceCatalogRow>()).AsList();
    var packages = (await multi.ReadAsync<ExperiencePackageOptionDto>()).AsList();
    var addOns = (await multi.ReadAsync<ExperienceAddOnOptionDto>()).AsList();

    var packagesByExperience = packages
      .GroupBy(item => item.ExperienceId)
      .ToDictionary(group => group.Key, group => (IReadOnlyList<ExperiencePackageOptionDto>)group.ToArray());

    var addOnsByExperience = addOns
      .GroupBy(item => item.ExperienceId)
      .ToDictionary(group => group.Key, group => (IReadOnlyList<ExperienceAddOnOptionDto>)group.ToArray());

    return rows
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

  private static async Task<bool> CatalogTablesExistAsync(SqlConnection conn, CancellationToken ct)
  {
    const string sql = """
SELECT CAST(CASE
    WHEN OBJECT_ID(N'dbo.ExperienceProvider', N'U') IS NOT NULL
     AND OBJECT_ID(N'dbo.Experience', N'U') IS NOT NULL
     AND OBJECT_ID(N'dbo.ExperiencePackage', N'U') IS NOT NULL
     AND OBJECT_ID(N'dbo.ExperienceAddOn', N'U') IS NOT NULL
     AND COL_LENGTH(N'dbo.ExperiencePackage', N'ProviderPackageName') IS NOT NULL
     AND COL_LENGTH(N'dbo.ExperiencePackage', N'UnitPrice') IS NOT NULL
     AND COL_LENGTH(N'dbo.ExperienceAddOn', N'AppliesPerParticipant') IS NOT NULL
    THEN 1 ELSE 0 END AS bit);
""";

    return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(sql, cancellationToken: ct));
  }

  private static async Task<bool> ReservationExperienceTablesExistAsync(SqlConnection conn, CancellationToken ct)
  {
    const string sql = """
SELECT CAST(CASE
    WHEN OBJECT_ID(N'dbo.Reservation_Experience', N'U') IS NOT NULL
     AND OBJECT_ID(N'dbo.Reservation_ExperienceAddOn', N'U') IS NOT NULL
    THEN 1 ELSE 0 END AS bit);
""";

    return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(sql, cancellationToken: ct));
  }

  private async Task<ExperienceValidationResult> BuildValidatedPricingAsync(
    ReservacionExperienceCreateRequest request,
    CancellationToken ct)
  {
    if (request.ReservationId <= 0 || request.ExperienceId <= 0 || request.ExperiencePackageId <= 0)
      return ExperienceValidationResult.Fail("Selecciona una reservacion, experiencia y paquete validos.");

    if (request.AdultParticipants <= 0)
      return ExperienceValidationResult.Fail("La experiencia requiere al menos un adulto.");

    if (request.ChildParticipants < 0)
      return ExperienceValidationResult.Fail("Los menores no pueden ser negativos.");

    var catalog = await GetActiveExperienceCatalogAsync(ct);
    var experience = catalog.FirstOrDefault(item => item.ExperienceId == request.ExperienceId);
    if (experience is null)
      return ExperienceValidationResult.Fail("La experiencia seleccionada ya no esta activa.");

    var package = experience.Packages.FirstOrDefault(item => item.ExperiencePackageId == request.ExperiencePackageId);
    if (package is null)
      return ExperienceValidationResult.Fail("El paquete seleccionado ya no esta activo.");

    var addOnInputs = new List<ExperiencePricingAddOnInput>();
    foreach (var selected in request.AddOns.Where(item => item.Quantity > 0))
    {
      var addOn = experience.AddOns.FirstOrDefault(item => item.ExperienceAddOnId == selected.ExperienceAddOnId);
      if (addOn is null)
      {
        return ExperienceValidationResult.Fail("Uno de los adicionales de la experiencia ya no esta activo.");
      }

      addOnInputs.Add(new ExperiencePricingAddOnInput
      {
        AddOn = addOn,
        Quantity = selected.Quantity
      });
    }

    try
    {
      var pricing = ExperiencePricingCalculator.Calculate(new ExperiencePricingInput
      {
        ExperienceDate = DateOnly.FromDateTime(request.ExperienceDate.Date),
        Experience = experience,
        Package = package,
        AdultParticipants = request.AdultParticipants,
        ChildParticipants = request.ChildParticipants,
        AddOns = addOnInputs
      });

      return ExperienceValidationResult.Ok(experience, package, pricing);
    }
    catch (Exception ex)
    {
      return ExperienceValidationResult.Fail(ex.Message);
    }
  }

  private static async Task<int> InsertReservationExperienceAsync(
    SqlConnection conn,
    SqlTransaction tx,
    ReservacionExperienceCreateRequest request,
    ExperienceValidationResult validation,
    CancellationToken ct)
  {
    return await conn.ExecuteScalarAsync<int>(
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
    Notes
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
    @Notes
);
SELECT CAST(SCOPE_IDENTITY() AS int);
""",
        BuildReservationExperienceParameters(request, validation),
        tx,
        cancellationToken: ct));
  }

  private static async Task InsertReservationExperienceAddOnsAsync(
    SqlConnection conn,
    SqlTransaction tx,
    int reservationExperienceId,
    IReadOnlyList<ExperiencePricingAddOnResult> addOns,
    CancellationToken ct)
  {
    if (addOns.Count == 0)
    {
      return;
    }

    await conn.ExecuteAsync(
      new CommandDefinition(
        """
INSERT INTO dbo.Reservation_ExperienceAddOn
(
    ReservationExperienceID,
    ExperienceAddOnID,
    AddOnNameSnapshot,
    Quantity,
    UnitPriceSnapshot,
    TotalSnapshot,
    TaxMode
)
VALUES
(
    @ReservationExperienceId,
    @ExperienceAddOnId,
    @AddOnName,
    @Quantity,
    @UnitPrice,
    @Total,
    @TaxMode
);
""",
        addOns.Select(addOn => new
        {
          ReservationExperienceId = reservationExperienceId,
          addOn.AddOn.ExperienceAddOnId,
          AddOnName = addOn.AddOn.Name,
          addOn.Quantity,
          addOn.UnitPrice,
          addOn.Total,
          addOn.TaxMode
        }).ToArray(),
        tx,
        cancellationToken: ct));
  }

  private static ReservationExperienceSqlParameters BuildReservationExperienceParameters(
    ReservacionExperienceCreateRequest request,
    ExperienceValidationResult validation)
  {
    return new ReservationExperienceSqlParameters
    {
      Id = request is ReservacionExperienceUpdateRequest updateRequest ? updateRequest.Id : 0,
      ReservationId = request.ReservationId,
      ExperienceId = validation.Experience.ExperienceId,
      ExperiencePackageId = validation.Package.ExperiencePackageId,
      ExperienceDate = request.ExperienceDate.Date,
      ExperienceName = validation.Experience.Name,
      PackageName = validation.Package.Name,
      ProviderName = validation.Experience.ProviderName,
      PackageIncludes = validation.Package.Includes,
      AdultParticipants = request.AdultParticipants,
      ChildParticipants = request.ChildParticipants,
      UnitPrice = validation.Pricing.UnitPrice,
      PackageSubtotal = validation.Pricing.PackageSubtotal,
      AddOnsTotal = validation.Pricing.AddOnsTotal,
      Total = validation.Pricing.Total,
      TaxMode = validation.Pricing.TaxMode,
      Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim()
    };
  }

  private static DateOnly? ToDateOnly(DateTime? value)
    => value.HasValue ? DateOnly.FromDateTime(value.Value) : null;

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

  private sealed class ExperienceValidationResult
  {
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public ExperienceCatalogItemDto Experience { get; init; } = new();
    public ExperiencePackageOptionDto Package { get; init; } = new();
    public ExperiencePricingResult Pricing { get; init; } = new();

    public static ExperienceValidationResult Ok(
      ExperienceCatalogItemDto experience,
      ExperiencePackageOptionDto package,
      ExperiencePricingResult pricing)
      => new()
      {
        Success = true,
        Experience = experience,
        Package = package,
        Pricing = pricing
      };

    public static ExperienceValidationResult Fail(string message)
      => new()
      {
        Success = false,
        Message = string.IsNullOrWhiteSpace(message) ? "No se pudo calcular la experiencia." : message
      };
  }

  private sealed record ReservationExperienceSqlParameters
  {
    public int Id { get; init; }
    public int ReservationId { get; init; }
    public int ExperienceId { get; init; }
    public int ExperiencePackageId { get; init; }
    public DateTime ExperienceDate { get; init; }
    public string ExperienceName { get; init; } = string.Empty;
    public string PackageName { get; init; } = string.Empty;
    public string ProviderName { get; init; } = string.Empty;
    public string? PackageIncludes { get; init; }
    public int AdultParticipants { get; init; }
    public int ChildParticipants { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal PackageSubtotal { get; init; }
    public decimal AddOnsTotal { get; init; }
    public decimal Total { get; init; }
    public string TaxMode { get; init; } = ExperienceTaxModes.TaxableExclusive;
    public string? Notes { get; init; }
  }
}
