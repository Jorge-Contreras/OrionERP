using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Platform;
using OrionERP.Infrastructure.Auth;
using OrionERP.Infrastructure.Features.Platform;

namespace OrionERP.UnitTests.Platform;

public sealed class PlatformAdministrationScopeAccessorTests
{
  [Fact]
  public async Task Scope_RequiresAnAuthenticatedAdministrator()
  {
    var accessor = CreateAccessor(new ClaimsPrincipal(new ClaimsIdentity()), "OHM191112Q26");

    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => accessor.GetRequiredScopeAsync());
  }

  [Fact]
  public async Task Scope_RejectsACompanyDifferentFromTheBoundContext()
  {
    var accessor = CreateAccessor(User("OHM191112Q26", includeAdministrator: true), "OTHER010101AAA");

    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => accessor.GetRequiredScopeAsync());
  }

  [Fact]
  public async Task Scope_RejectsMultipleCompanyClaims()
  {
    var user = User("OHM191112Q26", includeAdministrator: true);
    ((ClaimsIdentity)user.Identity!).AddClaim(new Claim(CompanyClaimTypes.Rfc, "OTHER010101AAA"));
    var accessor = CreateAccessor(user, "OHM191112Q26");

    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => accessor.GetRequiredScopeAsync());
  }

  [Fact]
  public async Task Scope_UsesTrustedClaimsAndTheBoundCompanyContext()
  {
    var accessor = CreateAccessor(User("ohm191112q26", includeAdministrator: true), "OHM191112Q26");

    var scope = await accessor.GetRequiredScopeAsync();

    Assert.Equal("user-17", scope.ActorUserId);
    Assert.Equal("OHM191112Q26", scope.CompanyRfc);
  }

  [Fact]
  public async Task Scope_RejectsStaleClaimsWhenAuthoritativeAccessWasRevoked()
  {
    var accessor = CreateAccessor(
      User("OHM191112Q26", includeAdministrator: true),
      "OHM191112Q26",
      new DeniedAccessValidator());

    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => accessor.GetRequiredScopeAsync());
  }

  private static PlatformAdministrationScopeAccessor CreateAccessor(
    ClaimsPrincipal user,
    string currentRfc,
    IPlatformAdministrationAccessValidator? accessValidator = null)
    => new(
      new FixedAuthenticationStateProvider(user),
      new FixedCompanyContext(currentRfc),
      accessValidator ?? new AllowedAccessValidator());

  private static ClaimsPrincipal User(string rfc, bool includeAdministrator)
  {
    var claims = new List<Claim>
    {
      new(ClaimTypes.NameIdentifier, "user-17"),
      new(CompanyClaimTypes.Rfc, rfc)
    };
    if (includeAdministrator) claims.Add(new Claim(ClaimTypes.Role, "Administrador"));
    return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
  }

  private sealed class FixedAuthenticationStateProvider(ClaimsPrincipal user) : AuthenticationStateProvider
  {
    public override Task<AuthenticationState> GetAuthenticationStateAsync()
      => Task.FromResult(new AuthenticationState(user));
  }

  private sealed class FixedCompanyContext(string rfc) : ICurrentCompanyContext
  {
    public string? CurrentRfc => rfc;
    public string? DisplayName => null;
    public int? EmployeeId => null;
    public string RequireRfc() => rfc;
    public void EnsureRfc(string expectedRfc)
    {
      if (!string.Equals(rfc, expectedRfc, StringComparison.OrdinalIgnoreCase))
        throw new UnauthorizedAccessException();
    }
    public Task<long> RequireCompanyIdAsync(CancellationToken ct = default) => Task.FromResult(1L);
  }

  private sealed class AllowedAccessValidator : IPlatformAdministrationAccessValidator
  {
    public Task EnsureAuthorizedAsync(
      string actorUserId,
      string companyRfc,
      CancellationToken ct = default)
      => Task.CompletedTask;
  }

  private sealed class DeniedAccessValidator : IPlatformAdministrationAccessValidator
  {
    public Task EnsureAuthorizedAsync(
      string actorUserId,
      string companyRfc,
      CancellationToken ct = default)
      => Task.FromException(new UnauthorizedAccessException());
  }
}
