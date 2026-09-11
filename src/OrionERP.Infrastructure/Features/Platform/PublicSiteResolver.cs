using Microsoft.EntityFrameworkCore;
using OrionERP.Application.Features.Platform;
using OrionERP.Infrastructure.Features.Platform.Data;

namespace OrionERP.Infrastructure.Features.Platform;

public sealed class PublicSiteResolver : IPublicSiteResolver, IPlatformExecutionScopeResolver
{
  private readonly PlatformDbContext _db;
  private readonly TimeProvider _timeProvider;

  public PublicSiteResolver(PlatformDbContext db, TimeProvider timeProvider)
  {
    _db = db;
    _timeProvider = timeProvider;
  }

  public async Task<PublicSiteBinding> ResolveRequiredAsync(
    PublicSiteResolutionRequest request,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);

    string publicSiteKey;
    try
    {
      publicSiteKey = PublicSiteResolutionPolicy.NormalizePublicSiteKey(request.PublicSiteKey);
    }
    catch (ArgumentException exception)
    {
      throw new PublicSiteResolutionException(
        PublicSiteResolutionFailure.InvalidExpectation,
        exception.Message);
    }

    var candidate = await (
      from publicSite in _db.PublicSites.AsNoTracking()
      where publicSite.PublicSiteKey == publicSiteKey
      join company in _db.Companies.AsNoTracking()
        on publicSite.CompanyId equals company.CompanyId
      join site in _db.Sites.AsNoTracking()
        on new { publicSite.CompanyId, publicSite.SiteId }
        equals new { site.CompanyId, site.SiteId }
      join module in _db.Modules.AsNoTracking()
        on publicSite.ModuleCode equals module.ModuleCode
      join companyModuleCandidate in _db.CompanyModules.AsNoTracking()
        on new { publicSite.CompanyId, publicSite.ModuleCode }
        equals new { companyModuleCandidate.CompanyId, companyModuleCandidate.ModuleCode }
        into companyModuleGroup
      from companyModule in companyModuleGroup.DefaultIfEmpty()
      join capabilityCandidate in _db.SiteCapabilities.AsNoTracking()
        on new { publicSite.CompanyId, publicSite.SiteId, publicSite.ModuleCode }
        equals new { capabilityCandidate.CompanyId, capabilityCandidate.SiteId, capabilityCandidate.ModuleCode }
        into capabilityGroup
      from capability in capabilityGroup.DefaultIfEmpty()
      select new PublicSiteResolutionCandidate(
        publicSite.PublicSiteId,
        publicSite.PublicSiteKey,
        company.CompanyId,
        company.Rfc,
        company.TaxRfc,
        company.LegacyTenantKey,
        company.IsActive,
        site.SiteId,
        site.SiteKey,
        site.DisplayName,
        site.TimeZoneId,
        site.IsActive,
        module.ModuleCode,
        module.IsActive,
        module.RequiresSite,
        companyModule != null,
        companyModule == null ? null : companyModule.Status,
        companyModule == null ? null : companyModule.EffectiveFromUtc,
        companyModule == null ? null : companyModule.EffectiveToUtc,
        companyModule == null ? 0 : companyModule.ConfigurationVersion,
        capability != null,
        capability != null && capability.IsEnabled,
        publicSite.CanonicalHost,
        publicSite.IsActive,
        publicSite.ConfigurationVersion,
        publicSite.BrandingVersion,
        publicSite.ContentVersion,
        publicSite.FallbackBrandingVersion,
        publicSite.FallbackContentVersion,
        publicSite.FallbackUntilUtc))
      .SingleOrDefaultAsync(ct);

    return PublicSiteResolutionPolicy.Resolve(
      request,
      candidate,
      _timeProvider.GetUtcNow().UtcDateTime);
  }

  public async Task<PlatformExecutionScope> ResolvePublicSiteAsync(
    PublicSiteResolutionRequest request,
    CancellationToken ct = default)
    => PlatformExecutionScope.FromPublicSite(await ResolveRequiredAsync(request, ct));
}
