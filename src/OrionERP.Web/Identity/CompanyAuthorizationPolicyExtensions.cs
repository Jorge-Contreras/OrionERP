using Microsoft.AspNetCore.Authorization;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Web.Identity;

public static class CompanyAuthorizationPolicyExtensions
{
  public static AuthorizationPolicyBuilder RequireCompanySession(this AuthorizationPolicyBuilder policy)
  {
    policy.RequireAuthenticatedUser();
    policy.RequireAssertion(context => context.User.FindAll(CompanyClaimTypes.Rfc)
      .Select(claim => claim.Value.Trim().ToUpperInvariant())
      .Count(value => value.Length > 0) == 1);
    return policy;
  }

  public static AuthorizationPolicyBuilder RequireCompanyRoles(
    this AuthorizationPolicyBuilder policy,
    params string[] roles)
  {
    var allowedRoles = new[] { "Administrador" }
      .Concat(roles)
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToArray();
    return policy.RequireCompanySession().RequireRole(allowedRoles);
  }
}
