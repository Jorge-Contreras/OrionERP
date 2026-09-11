using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Bruno.Web.Services;

public static class RestaurantMemberCookieScopeValidator
{
  public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
  {
    ArgumentNullException.ThrowIfNull(context);

    // /healthz intentionally runs before database binding and is anonymous.
    // Do not let an irrelevant stale member cookie turn liveness into a 500.
    if (context.HttpContext.Request.Path.Equals("/healthz", StringComparison.OrdinalIgnoreCase))
      return;

    // Preserve Identity's security-stamp revocation behavior.
    await SecurityStampValidator.ValidatePrincipalAsync(context);
    if (context.Principal?.Identity?.IsAuthenticated != true) return;

    var scope = context.HttpContext.RequestServices
      .GetRequiredService<IRestaurantPublicIdentityScopeAccessor>()
      .Current;
    if (RestaurantPublicIdentityScopePolicy.Matches(context.Principal, scope)) return;

    context.RejectPrincipal();
    await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
  }
}
