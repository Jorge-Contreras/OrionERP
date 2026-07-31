using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Restaurante;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Web.Identity;

public sealed class BrunoAdminClaimsPrincipalFactory
  : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>
{
  private readonly UserManager<ApplicationUser> _userManager;

  public BrunoAdminClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> optionsAccessor)
    : base(userManager, roleManager, optionsAccessor)
  {
    _userManager = userManager;
  }

  protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
  {
    var identity = await base.GenerateClaimsAsync(user);
    var canAdministerBruno =
      await _userManager.IsInRoleAsync(user, "Administrador") ||
      await _userManager.IsInRoleAsync(user, "RestauranteAdmin");

    if (canAdministerBruno &&
        !identity.HasClaim("rfc", BrunoRestaurantConstants.Rfc))
    {
      identity.AddClaim(new Claim("rfc", BrunoRestaurantConstants.Rfc));
    }

    return identity;
  }
}
