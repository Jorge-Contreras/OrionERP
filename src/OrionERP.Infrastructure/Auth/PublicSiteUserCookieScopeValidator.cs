using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace OrionERP.Infrastructure.Auth;

public static class PublicSiteUserCookieScopeValidator
{
  public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
  {
    ArgumentNullException.ThrowIfNull(context);
    if (context.HttpContext.Request.Path.Equals("/healthz",StringComparison.OrdinalIgnoreCase)) return;
    await SecurityStampValidator.ValidatePrincipalAsync(context);
    if (context.Principal?.Identity?.IsAuthenticated!=true) return;
    var scope=context.HttpContext.RequestServices.GetRequiredService<IPublicIdentityScopeAccessor>().Current;
    if (PublicIdentityScopePolicy.Matches(context.Principal,scope)) return;
    context.RejectPrincipal();
    await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
  }
}
