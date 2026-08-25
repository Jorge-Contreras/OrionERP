using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using OrionERP.Infrastructure.Auth;
using OrionERP.Web.Identity;
using OrionERP.Web.State;

namespace OrionERP.UnitTests.Auth;

public sealed class CurrentCompanyContextTests
{
  [Fact]
  public void Claims_are_normalized_and_company_metadata_is_hydrated()
  {
    var context = new CurrentCompanyContext();

    context.InitializeFromClaims(Principal(
      new Claim(CompanyClaimTypes.Rfc, " ohm191112q26 "),
      new Claim(CompanyClaimTypes.CompanyName, "  Orion Hospitality  "),
      new Claim(CompanyClaimTypes.EmployeeId, "42")));

    Assert.Equal("OHM191112Q26", context.CurrentRfc);
    Assert.Equal("Orion Hospitality", context.DisplayName);
    Assert.Equal(42, context.EmployeeId);
    Assert.Equal("OHM191112Q26", context.RequireRfc());
  }

  [Fact]
  public void Anonymous_principal_clears_the_context()
  {
    var context = HydratedContext();

    context.InitializeFromClaims(new ClaimsPrincipal(new ClaimsIdentity()));

    Assert.Null(context.CurrentRfc);
    Assert.Null(context.DisplayName);
    Assert.Null(context.EmployeeId);
    Assert.Throws<UnauthorizedAccessException>(() => context.RequireRfc());
  }

  [Fact]
  public void Zero_rfc_claims_clear_the_context()
  {
    var context = HydratedContext();

    context.InitializeFromClaims(Principal(new Claim(CompanyClaimTypes.CompanyName, "Empresa")));

    Assert.Null(context.CurrentRfc);
  }

  [Fact]
  public void Multiple_rfc_claims_clear_the_context_even_when_values_match()
  {
    var context = HydratedContext();

    context.InitializeFromClaims(Principal(
      new Claim(CompanyClaimTypes.Rfc, "OHM191112Q26"),
      new Claim(CompanyClaimTypes.Rfc, "OHM191112Q26")));

    Assert.Null(context.CurrentRfc);
  }

  [Fact]
  public void EnsureRfc_accepts_normalized_match_and_rejects_other_company()
  {
    var context = HydratedContext();

    context.EnsureRfc(" ohm191112q26 ");
    Assert.Throws<UnauthorizedAccessException>(() => context.EnsureRfc("BRUNOS260707L26"));
  }

  [Fact]
  public void Same_company_can_refresh_metadata()
  {
    var context = HydratedContext();

    context.InitializeFromClaims(Principal(
      new Claim(CompanyClaimTypes.Rfc, "OHM191112Q26"),
      new Claim(CompanyClaimTypes.CompanyName, "Nombre actualizado"),
      new Claim(CompanyClaimTypes.EmployeeId, "99")));

    Assert.Equal("OHM191112Q26", context.CurrentRfc);
    Assert.Equal("Nombre actualizado", context.DisplayName);
    Assert.Equal(99, context.EmployeeId);
  }

  [Fact]
  public void Direct_company_switch_is_rejected_and_original_scope_is_retained()
  {
    var context = HydratedContext();

    Assert.Throws<UnauthorizedAccessException>(() => context.InitializeFromClaims(Principal(
      new Claim(CompanyClaimTypes.Rfc, "BRUNOS260707L26"))));

    Assert.Equal("OHM191112Q26", context.CurrentRfc);
  }

  [Theory]
  [InlineData("FinanzasManager")]
  [InlineData("Administrador")]
  public async Task Company_policy_accepts_scoped_role_or_administrator(string role)
  {
    var policy = new AuthorizationPolicyBuilder()
      .RequireCompanyRoles("FinanzasManager")
      .Build();
    var principal = Principal(
      new Claim(CompanyClaimTypes.Rfc, "OHM191112Q26"),
      new Claim(ClaimTypes.Role, role));

    Assert.True(await AuthorizeAsync(policy, principal));
  }

  [Fact]
  public async Task Company_policy_rejects_missing_or_multiple_rfc_claims()
  {
    var policy = new AuthorizationPolicyBuilder()
      .RequireCompanyRoles("FinanzasManager")
      .Build();
    var role = new Claim(ClaimTypes.Role, "FinanzasManager");

    Assert.False(await AuthorizeAsync(policy, Principal(role)));
    Assert.False(await AuthorizeAsync(policy, Principal(
      role,
      new Claim(CompanyClaimTypes.Rfc, "OHM191112Q26"),
      new Claim(CompanyClaimTypes.Rfc, "BRUNOS260707L26"))));
  }

  private static CurrentCompanyContext HydratedContext()
  {
    var context = new CurrentCompanyContext();
    context.InitializeFromClaims(Principal(
      new Claim(CompanyClaimTypes.Rfc, "OHM191112Q26"),
      new Claim(CompanyClaimTypes.CompanyName, "Orion"),
      new Claim(CompanyClaimTypes.EmployeeId, "7")));
    return context;
  }

  private static ClaimsPrincipal Principal(params Claim[] claims)
    => new(new ClaimsIdentity(claims, "test", ClaimTypes.Name, ClaimTypes.Role));

  private static async Task<bool> AuthorizeAsync(AuthorizationPolicy policy, ClaimsPrincipal principal)
  {
    var context = new AuthorizationHandlerContext(policy.Requirements, principal, resource: null);
    foreach (var handler in policy.Requirements.OfType<IAuthorizationHandler>())
      await handler.HandleAsync(context);
    return context.HasSucceeded;
  }
}
