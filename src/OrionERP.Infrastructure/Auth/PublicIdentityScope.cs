using System.Security.Claims;

namespace OrionERP.Infrastructure.Auth;

/// <summary>
/// The verified, immutable platform binding used by one public website
/// process. Implementations must obtain it from trusted process configuration
/// and the corresponding database binding, never from request input.
/// </summary>
public sealed record PublicIdentityScope(
  long PublicSiteId,
  string PublicSiteKey,
  long CompanyId,
  string CompanyRfc,
  long SiteId,
  string SiteKey,
  string ModuleCode);

public interface IPublicIdentityScopeAccessor
{
  PublicIdentityScope Current { get; }
}

public static class PublicIdentityClaimTypes
{
  public const string PublicSiteId = "orion:public-site-id";
  public const string PublicSiteKey = "orion:public-site-key";
  public const string CompanyId = "orion:company-id";
  public const string CompanyRfc = "orion:company-rfc";
  public const string SiteId = "orion:site-id";
  public const string SiteKey = "orion:site-key";
  public const string ModuleCode = "orion:module-code";
}

public static class PublicIdentityScopePolicy
{
  public static bool Matches(ClaimsPrincipal? principal, PublicIdentityScope scope)
  {
    ArgumentNullException.ThrowIfNull(scope);
    if (principal?.Identity?.IsAuthenticated != true) return false;

    return HasExactClaim(principal, PublicIdentityClaimTypes.PublicSiteId, scope.PublicSiteId.ToString(System.Globalization.CultureInfo.InvariantCulture))
      && HasExactClaim(principal, PublicIdentityClaimTypes.PublicSiteKey, scope.PublicSiteKey)
      && HasExactClaim(principal, PublicIdentityClaimTypes.CompanyId, scope.CompanyId.ToString(System.Globalization.CultureInfo.InvariantCulture))
      && HasExactClaim(principal, PublicIdentityClaimTypes.CompanyRfc, scope.CompanyRfc)
      && HasExactClaim(principal, PublicIdentityClaimTypes.SiteId, scope.SiteId.ToString(System.Globalization.CultureInfo.InvariantCulture))
      && HasExactClaim(principal, PublicIdentityClaimTypes.SiteKey, scope.SiteKey)
      && HasExactClaim(principal, PublicIdentityClaimTypes.ModuleCode, scope.ModuleCode);
  }

  private static bool HasExactClaim(ClaimsPrincipal principal, string type, string value)
  {
    var claims = principal.FindAll(type).ToArray();
    return claims.Length == 1
      && string.Equals(claims[0].Value, value, StringComparison.Ordinal);
  }
}
