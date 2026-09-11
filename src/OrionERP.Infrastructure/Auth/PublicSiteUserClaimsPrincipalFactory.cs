using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace OrionERP.Infrastructure.Auth;

public sealed class PublicSiteUserClaimsPrincipalFactory(
  UserManager<PublicSiteUser> userManager,
  RoleManager<PublicSiteRole> roleManager,
  IOptions<IdentityOptions> options,
  IPublicIdentityScopeAccessor scopeAccessor)
  : UserClaimsPrincipalFactory<PublicSiteUser,PublicSiteRole>(userManager,roleManager,options)
{
  protected override async Task<System.Security.Claims.ClaimsIdentity> GenerateClaimsAsync(PublicSiteUser user)
  {
    var scope=scopeAccessor.Current;
    if (user.PublicSiteId!=scope.PublicSiteId)
      throw new InvalidOperationException("The member does not belong to the configured public website.");
    var identity=await base.GenerateClaimsAsync(user);
    identity.AddClaim(new(PublicIdentityClaimTypes.PublicSiteId,scope.PublicSiteId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    identity.AddClaim(new(PublicIdentityClaimTypes.PublicSiteKey,scope.PublicSiteKey));
    identity.AddClaim(new(PublicIdentityClaimTypes.CompanyId,scope.CompanyId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    identity.AddClaim(new(PublicIdentityClaimTypes.CompanyRfc,scope.CompanyRfc));
    identity.AddClaim(new(PublicIdentityClaimTypes.SiteId,scope.SiteId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    identity.AddClaim(new(PublicIdentityClaimTypes.SiteKey,scope.SiteKey));
    identity.AddClaim(new(PublicIdentityClaimTypes.ModuleCode,scope.ModuleCode));
    return identity;
  }
}
