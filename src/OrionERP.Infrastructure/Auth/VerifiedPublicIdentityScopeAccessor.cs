using Microsoft.AspNetCore.Http;
using OrionERP.Application.Features.Platform;
using OrionERP.Infrastructure.Features.Platform;

namespace OrionERP.Infrastructure.Auth;

/// <summary>Builds Identity scope only from the binding installed by the public-site gate.</summary>
public sealed class VerifiedPublicIdentityScopeAccessor(
  IHttpContextAccessor httpContextAccessor,
  IPublicWebsiteInstanceContext website) : IPublicIdentityScopeAccessor
{
  public PublicIdentityScope Current
  {
    get
    {
      var context = httpContextAccessor.HttpContext
        ?? throw new InvalidOperationException("Public identity requires an active verified website request.");
      if (!context.Items.TryGetValue(PublicWebsiteBindingGateMiddleware.BindingItemKey,out var value)
          || value is not PublicSiteBinding binding)
        throw new InvalidOperationException("Public identity is unavailable before public-site verification.");

      var instance=website.Instance;
      if (!string.Equals(binding.PublicSiteKey,instance.PublicSiteKey,StringComparison.Ordinal)
          || !string.Equals(binding.ModuleCode,instance.ModuleCode,StringComparison.Ordinal)
          || (!string.IsNullOrWhiteSpace(instance.ExpectedCompanyRfc)
            && !string.Equals(binding.CompanyRfc,instance.ExpectedCompanyRfc,StringComparison.Ordinal))
          || (!string.IsNullOrWhiteSpace(instance.SiteKey)
            && !string.Equals(binding.SiteKey,instance.SiteKey,StringComparison.Ordinal)))
        throw new InvalidOperationException("The verified website binding does not match the configured identity scope.");

      return new PublicIdentityScope(binding.PublicSiteId,binding.PublicSiteKey,binding.CompanyId,
        binding.CompanyRfc,binding.SiteId,binding.SiteKey,binding.ModuleCode);
    }
  }
}
