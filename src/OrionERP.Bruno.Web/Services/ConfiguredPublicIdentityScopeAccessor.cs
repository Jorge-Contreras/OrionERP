using OrionERP.Application.Features.Platform;
using OrionERP.Infrastructure.Auth;
using OrionERP.Infrastructure.Features.Platform;

namespace OrionERP.Bruno.Web.Services;

/// <summary>
/// Exposes only the binding produced by the fail-closed public-site gate. HTTP
/// host, route, query and header values are intentionally ignored.
/// </summary>
public sealed class ConfiguredRestaurantIdentityScopeAccessor
  : IRestaurantPublicIdentityScopeAccessor
{
  private readonly IHttpContextAccessor _httpContextAccessor;
  private readonly IPublicWebsiteInstanceContext _website;

  public ConfiguredRestaurantIdentityScopeAccessor(
    IHttpContextAccessor httpContextAccessor,
    IPublicWebsiteInstanceContext website)
  {
    _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
    _website = website ?? throw new ArgumentNullException(nameof(website));
  }

  public RestaurantPublicIdentityScope Current
  {
    get
    {
      var context = _httpContextAccessor.HttpContext
        ?? throw new InvalidOperationException("Restaurant identity requires an active verified website request.");
      if (!context.Items.TryGetValue(PublicWebsiteBindingGateMiddleware.BindingItemKey, out var value)
          || value is not PublicSiteBinding binding)
      {
        throw new InvalidOperationException("Restaurant identity is unavailable before public-site verification.");
      }

      var instance = _website.Instance;
      if (!string.Equals(binding.PublicSiteKey, instance.PublicSiteKey, StringComparison.Ordinal)
          || !string.Equals(binding.CompanyRfc, instance.ExpectedCompanyRfc, StringComparison.Ordinal)
          || !string.Equals(binding.SiteKey, instance.SiteKey, StringComparison.Ordinal)
          || !string.Equals(binding.ModuleCode, PlatformModuleCodes.Restaurant, StringComparison.Ordinal)
          || !string.Equals(binding.ModuleCode, instance.ModuleCode, StringComparison.Ordinal))
      {
        throw new InvalidOperationException("The verified website binding does not match the configured restaurant identity scope.");
      }

      return new RestaurantPublicIdentityScope(
        binding.PublicSiteId,
        binding.PublicSiteKey,
        binding.CompanyId,
        binding.CompanyRfc,
        binding.SiteId,
        binding.SiteKey);
    }
  }
}
