using System.Security.Claims;

namespace OrionERP.Infrastructure.Auth;

/// <summary>
/// The verified, immutable platform binding used by one restaurant website
/// process. Implementations must obtain it from trusted process configuration
/// and the corresponding database binding, never from request input.
/// </summary>
public sealed record RestaurantPublicIdentityScope(
  long PublicSiteId,
  string PublicSiteKey,
  long CompanyId,
  string CompanyRfc,
  long SiteId,
  string SiteKey);

public interface IRestaurantPublicIdentityScopeAccessor
{
  RestaurantPublicIdentityScope Current { get; }
}

public static class RestaurantPublicIdentityClaimTypes
{
  public const string PublicSiteId = "orion:restaurant:public-site-id";
  public const string PublicSiteKey = "orion:restaurant:public-site-key";
  public const string CompanyId = "orion:restaurant:company-id";
  public const string CompanyRfc = "orion:restaurant:company-rfc";
  public const string SiteId = "orion:restaurant:site-id";
  public const string SiteKey = "orion:restaurant:site-key";
}

public static class RestaurantPublicIdentityScopePolicy
{
  public static bool Matches(ClaimsPrincipal? principal, RestaurantPublicIdentityScope scope)
  {
    ArgumentNullException.ThrowIfNull(scope);
    if (principal?.Identity?.IsAuthenticated != true) return false;

    return HasExactClaim(principal, RestaurantPublicIdentityClaimTypes.PublicSiteId, scope.PublicSiteId.ToString(System.Globalization.CultureInfo.InvariantCulture))
      && HasExactClaim(principal, RestaurantPublicIdentityClaimTypes.PublicSiteKey, scope.PublicSiteKey)
      && HasExactClaim(principal, RestaurantPublicIdentityClaimTypes.CompanyId, scope.CompanyId.ToString(System.Globalization.CultureInfo.InvariantCulture))
      && HasExactClaim(principal, RestaurantPublicIdentityClaimTypes.CompanyRfc, scope.CompanyRfc)
      && HasExactClaim(principal, RestaurantPublicIdentityClaimTypes.SiteId, scope.SiteId.ToString(System.Globalization.CultureInfo.InvariantCulture))
      && HasExactClaim(principal, RestaurantPublicIdentityClaimTypes.SiteKey, scope.SiteKey);
  }

  private static bool HasExactClaim(ClaimsPrincipal principal, string type, string value)
  {
    var claims = principal.FindAll(type).ToArray();
    return claims.Length == 1
      && string.Equals(claims[0].Value, value, StringComparison.Ordinal);
  }
}
