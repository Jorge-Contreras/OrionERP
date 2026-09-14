using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using OrionERP.Application.Features.Platform;
using OrionERP.Infrastructure.Auth;
using OrionERP.Web.Identity;

namespace OrionERP.UnitTests.Platform;

public sealed class CompanyModuleAuthorizationTests
{
  private const string Brunos = "BRUNOS260707L26";

  [Fact]
  public async Task RestaurantOnlyCompany_OpensRestaurantButNotHospitality()
  {
    var access = new FakeModuleAccess(Brunos, PlatformModuleCodes.AccountingCore, PlatformModuleCodes.Restaurant);
    var user = Principal(Brunos);

    Assert.True(await AuthorizeAsync(user, PlatformModuleCodes.Restaurant, access));
    Assert.False(await AuthorizeAsync(user, PlatformModuleCodes.Hospitality, access));
  }

  [Fact]
  public async Task ReadsTheCompanyFromTheClaims()
  {
    var access = new FakeModuleAccess(Brunos, PlatformModuleCodes.Restaurant);

    Assert.True(await AuthorizeAsync(Principal(" brunos260707l26 "), "restaurant", access));
    Assert.Equal([Brunos], access.RequestedRfcs);
  }

  [Fact]
  public async Task PrincipalWithoutExactlyOneCompany_NeverPasses()
  {
    var access = new FakeModuleAccess(Brunos, PlatformModuleCodes.Restaurant);

    Assert.False(await AuthorizeAsync(Principal(), PlatformModuleCodes.Restaurant, access));
    Assert.False(await AuthorizeAsync(Principal(Brunos, "OHM191112Q26"), PlatformModuleCodes.Restaurant, access));
    Assert.Empty(access.RequestedRfcs);
  }

  [Fact]
  public void Requirement_RejectsABlankModule()
    => Assert.Throws<ArgumentException>(() => new CompanyModuleRequirement(" "));

  private static async Task<bool> AuthorizeAsync(ClaimsPrincipal user, string moduleCode, ICompanyModuleAccess access)
  {
    var context = new AuthorizationHandlerContext([new CompanyModuleRequirement(moduleCode)], user, resource: null);
    await new CompanyModuleAuthorizationHandler(access).HandleAsync(context);
    return context.HasSucceeded;
  }

  private static ClaimsPrincipal Principal(params string[] rfcs)
    => new(new ClaimsIdentity(
      rfcs.Select(rfc => new Claim(CompanyClaimTypes.Rfc, rfc)).Append(new Claim(ClaimTypes.Name, "test@orionerp.local")),
      "Test"));

  private sealed class FakeModuleAccess(string rfc, params string[] modules) : ICompanyModuleAccess
  {
    public List<string> RequestedRfcs { get; } = [];

    public Task<IReadOnlySet<string>> GetEnabledModulesForRfcAsync(string requestedRfc, CancellationToken ct = default)
    {
      RequestedRfcs.Add(requestedRfc);
      IReadOnlySet<string> result = string.Equals(requestedRfc, rfc, StringComparison.Ordinal)
        ? new HashSet<string>(modules, StringComparer.OrdinalIgnoreCase)
        : new HashSet<string>();
      return Task.FromResult(result);
    }

    public Task<IReadOnlySet<string>> GetEnabledModulesAsync(CancellationToken ct = default)
      => throw new InvalidOperationException("La autorización no debe depender del circuito.");

    public Task<bool> IsEnabledAsync(string moduleCode, CancellationToken ct = default)
      => throw new InvalidOperationException("La autorización no debe depender del circuito.");
  }
}
