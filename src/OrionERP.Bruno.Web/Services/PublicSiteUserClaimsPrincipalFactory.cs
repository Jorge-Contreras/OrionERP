using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Bruno.Web.Services;

public sealed class RestaurantMemberClaimsPrincipalFactory
  : UserClaimsPrincipalFactory<BrunoMemberUser, IdentityRole>
{
  private readonly IRestaurantPublicIdentityScopeAccessor _scopeAccessor;

  public RestaurantMemberClaimsPrincipalFactory(
    UserManager<BrunoMemberUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> options,
    IRestaurantPublicIdentityScopeAccessor scopeAccessor)
    : base(userManager, roleManager, options)
  {
    _scopeAccessor = scopeAccessor ?? throw new ArgumentNullException(nameof(scopeAccessor));
  }

  protected override async Task<System.Security.Claims.ClaimsIdentity> GenerateClaimsAsync(
    BrunoMemberUser user)
  {
    var scope = _scopeAccessor.Current;
    if (user.PublicSiteId != scope.PublicSiteId)
    {
      throw new InvalidOperationException("The member does not belong to the configured restaurant website.");
    }

    var identity = await base.GenerateClaimsAsync(user);
    identity.AddClaim(new(RestaurantPublicIdentityClaimTypes.PublicSiteId, scope.PublicSiteId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    identity.AddClaim(new(RestaurantPublicIdentityClaimTypes.PublicSiteKey, scope.PublicSiteKey));
    identity.AddClaim(new(RestaurantPublicIdentityClaimTypes.CompanyId, scope.CompanyId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    identity.AddClaim(new(RestaurantPublicIdentityClaimTypes.CompanyRfc, scope.CompanyRfc));
    identity.AddClaim(new(RestaurantPublicIdentityClaimTypes.SiteId, scope.SiteId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    identity.AddClaim(new(RestaurantPublicIdentityClaimTypes.SiteKey, scope.SiteKey));
    return identity;
  }
}
