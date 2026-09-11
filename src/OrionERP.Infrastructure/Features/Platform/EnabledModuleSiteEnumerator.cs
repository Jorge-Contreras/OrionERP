using Microsoft.EntityFrameworkCore;
using OrionERP.Application.Features.Platform;
using OrionERP.Infrastructure.Features.Platform.Data;

namespace OrionERP.Infrastructure.Features.Platform;

public sealed class EnabledModuleSiteEnumerator(
  PlatformDbContext db,
  TimeProvider timeProvider)
  : IEnabledModuleSiteEnumerator
{
  public async Task<IReadOnlyList<PlatformExecutionScope>> ListAsync(
    string? moduleCode = null,
    CancellationToken ct = default)
  {
    var normalizedModule = string.IsNullOrWhiteSpace(moduleCode)
      ? null
      : moduleCode.Trim().ToUpperInvariant();
    var now = timeProvider.GetUtcNow().UtcDateTime;

    var query =
      from company in db.Companies.AsNoTracking()
      join site in db.Sites.AsNoTracking() on company.CompanyId equals site.CompanyId
      join assignment in db.CompanyModules.AsNoTracking()
        on company.CompanyId equals assignment.CompanyId
      join module in db.Modules.AsNoTracking() on assignment.ModuleCode equals module.ModuleCode
      join capability in db.SiteCapabilities.AsNoTracking()
        on new { company.CompanyId, site.SiteId, assignment.ModuleCode }
        equals new { capability.CompanyId, capability.SiteId, capability.ModuleCode }
      where company.IsActive && site.IsActive && module.IsActive && capability.IsEnabled
        && assignment.Status == "Enabled"
        && (assignment.EffectiveFromUtc == null || assignment.EffectiveFromUtc <= now)
        && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now)
        && (normalizedModule == null || assignment.ModuleCode == normalizedModule)
      orderby company.CompanyId, site.SiteId, assignment.ModuleCode
      select new PlatformExecutionScope(
        company.CompanyId,
        company.Rfc,
        company.TaxRfc,
        site.SiteId,
        assignment.ModuleCode,
        null,
        null,
        null);

    return await query.ToListAsync(ct);
  }
}
