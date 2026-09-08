using Microsoft.EntityFrameworkCore;
using OrionERP.Application.Features.Platform;
using OrionERP.Infrastructure.Features.Platform.Data;

namespace OrionERP.Infrastructure.Features.Platform;

public sealed class PlatformAdministrationReader : IPlatformAdministrationReader
{
  private readonly PlatformDbContext _db;
  private readonly IPlatformAdministrationScopeAccessor _scopeAccessor;
  private readonly TimeProvider _timeProvider;

  public PlatformAdministrationReader(
    PlatformDbContext db,
    IPlatformAdministrationScopeAccessor scopeAccessor,
    TimeProvider timeProvider)
  {
    _db = db;
    _scopeAccessor = scopeAccessor;
    _timeProvider = timeProvider;
  }

  public async Task<PlatformAdministrationSnapshot> GetCurrentCompanySnapshotAsync(CancellationToken ct = default)
  {
    var scope = await _scopeAccessor.GetRequiredScopeAsync(ct);
    var normalizedRfc = scope.CompanyRfc.Trim().ToUpperInvariant();
    var companyEntity = await _db.Companies.AsNoTracking()
      .SingleOrDefaultAsync(company => company.Rfc == normalizedRfc, ct)
      ?? throw new UnauthorizedAccessException("La empresa de la sesión no está registrada en la plataforma.");
    if (!companyEntity.IsActive)
      throw new UnauthorizedAccessException("La empresa de la sesión está inactiva.");

    var companyId = companyEntity.CompanyId;
    var now = _timeProvider.GetUtcNow().UtcDateTime;

    // Every tenant-owned query is anchored to the CompanyId resolved from the
    // authenticated session. No request-supplied CompanyId or RFC is accepted.
    var siteEntities = await _db.Sites.AsNoTracking()
      .Where(site => site.CompanyId == companyId)
      .OrderByDescending(site => site.IsActive)
      .ThenBy(site => site.DisplayName)
      .ThenBy(site => site.SiteKey)
      .ToListAsync(ct);
    var moduleEntities = await _db.Modules.AsNoTracking()
      .OrderBy(module => module.DisplayName)
      .ThenBy(module => module.ModuleCode)
      .ToListAsync(ct);
    var assignmentEntities = await _db.CompanyModules.AsNoTracking()
      .Where(assignment => assignment.CompanyId == companyId)
      .ToListAsync(ct);
    var capabilityEntities = await _db.SiteCapabilities.AsNoTracking()
      .Where(capability => capability.CompanyId == companyId)
      .OrderBy(capability => capability.SiteId)
      .ThenBy(capability => capability.ModuleCode)
      .ToListAsync(ct);
    var publicSiteEntities = await _db.PublicSites.AsNoTracking()
      .Where(publicSite => publicSite.CompanyId == companyId)
      .OrderByDescending(publicSite => publicSite.IsActive)
      .ThenBy(publicSite => publicSite.PublicSiteKey)
      .ToListAsync(ct);

    var assignmentsByModule = assignmentEntities.ToDictionary(
      assignment => assignment.ModuleCode,
      StringComparer.OrdinalIgnoreCase);
    var modules = moduleEntities.Select(moduleEntity =>
    {
      assignmentsByModule.TryGetValue(moduleEntity.ModuleCode, out var assignmentEntity);
      var assignment = assignmentEntity is null ? null : Map(assignmentEntity, now);
      var isEntitled = moduleEntity.IsActive
        && (moduleEntity.IsCore || assignment?.IsEffective == true);
      return new PlatformModuleStatus(Map(moduleEntity), assignment, isEntitled);
    }).ToArray();

    return new PlatformAdministrationSnapshot(
      Map(companyEntity),
      siteEntities.Select(Map).ToArray(),
      modules,
      capabilityEntities.Select(Map).ToArray(),
      publicSiteEntities.Select(Map).ToArray(),
      now);
  }

  private static PlatformCompany Map(PlatformCompanyEntity entity)
    => new(
      entity.CompanyId,
      entity.Rfc,
      entity.TaxRfc,
      entity.LegacyTenantKey,
      entity.DisplayName,
      entity.LegalName,
      entity.IsActive,
      entity.BrandingVersion,
      entity.UpdatedAtUtc,
      ToConcurrencyToken(entity.RowVersion));

  private static PlatformSite Map(PlatformSiteEntity entity)
    => new(
      entity.SiteId,
      entity.CompanyId,
      entity.SiteKey,
      entity.DisplayName,
      entity.TimeZoneId,
      entity.IsActive,
      entity.UpdatedAtUtc,
      ToConcurrencyToken(entity.RowVersion));

  private static PlatformModule Map(PlatformModuleEntity entity)
    => new(
      entity.ModuleCode,
      entity.DisplayName,
      entity.Description,
      entity.IsCore,
      entity.RequiresSite,
      entity.IsActive,
      entity.UpdatedAtUtc,
      ToConcurrencyToken(entity.RowVersion));

  private static PlatformCompanyModule Map(PlatformCompanyModuleEntity entity, DateTime now)
    => new(
      entity.CompanyId,
      entity.ModuleCode,
      entity.Status,
      entity.IsEnabled,
      entity.EffectiveFromUtc,
      entity.EffectiveToUtc,
      entity.ConfigurationVersion,
      PublicSiteResolutionPolicy.IsEffective(
        entity.Status,
        entity.EffectiveFromUtc,
        entity.EffectiveToUtc,
        now),
      entity.UpdatedAtUtc,
      ToConcurrencyToken(entity.RowVersion));

  private static PlatformSiteCapability Map(PlatformSiteCapabilityEntity entity)
    => new(
      entity.CompanyId,
      entity.SiteId,
      entity.ModuleCode,
      entity.IsEnabled,
      entity.UpdatedAtUtc,
      ToConcurrencyToken(entity.RowVersion));

  private static PlatformPublicSite Map(PlatformPublicSiteEntity entity)
    => new(
      entity.PublicSiteId,
      entity.PublicSiteKey,
      entity.CompanyId,
      entity.SiteId,
      entity.ModuleCode,
      entity.CanonicalHost,
      entity.IsActive,
      entity.ConfigurationVersion,
      entity.BrandingVersion,
      entity.ContentVersion,
      entity.UpdatedAtUtc,
      ToConcurrencyToken(entity.RowVersion),
      entity.FallbackBrandingVersion,
      entity.FallbackContentVersion,
      entity.FallbackUntilUtc);

  private static string ToConcurrencyToken(byte[] rowVersion)
    => rowVersion.Length == 0 ? string.Empty : Convert.ToBase64String(rowVersion);
}
