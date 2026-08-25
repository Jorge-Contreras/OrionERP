using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrionERP.Application.Features.Auth.Companies;
using OrionERP.Infrastructure.Auth;
using OrionERP.Web.Identity;

namespace OrionERP.UnitTests.Auth;

public sealed class CompanyAuthenticationCoordinatorTests
{
  [Theory]
  [InlineData(0, CompanyAuthenticationStatus.NoCompany)]
  [InlineData(1, CompanyAuthenticationStatus.SignedIn)]
  [InlineData(2, CompanyAuthenticationStatus.SelectionRequired)]
  public async Task Begin_handles_zero_one_and_multiple_companies(
    int companyCount,
    CompanyAuthenticationStatus expectedStatus)
  {
    await using var fixture = await Fixture.CreateAsync(companyCount);

    var result = await fixture.Coordinator.BeginAsync(fixture.HttpContext, fixture.User, true, "/target");

    Assert.Equal(expectedStatus, result.Status);
    Assert.Equal(companyCount > 1 ? companyCount : 0, result.CompanyOptions.Count);
    if (companyCount == 1)
      Assert.Equal(fixture.Access.Options[0].Rfc, fixture.Authentication.ApplicationPrincipal?.FindFirstValue(CompanyClaimTypes.Rfc));
    if (companyCount > 1)
      Assert.True(fixture.Authentication.PendingResult.Succeeded);
  }

  [Fact]
  public async Task Pending_selection_is_normalized_revalidated_and_signed_in()
  {
    await using var fixture = await Fixture.CreateAsync(2);
    await fixture.Coordinator.BeginAsync(fixture.HttpContext, fixture.User, false, "/target");

    var result = await fixture.Coordinator.CompletePendingAsync(fixture.HttpContext, " ohm191112q26 ");

    Assert.Equal(CompanyAuthenticationStatus.SignedIn, result.Status);
    Assert.Equal("/target", result.ReturnUrl);
    Assert.Equal("OHM191112Q26", fixture.Authentication.ApplicationPrincipal?.FindFirstValue(CompanyClaimTypes.Rfc));
    Assert.True(fixture.Authentication.PendingResult.None);
  }

  [Fact]
  public async Task Membership_removal_before_final_selection_is_rejected()
  {
    await using var fixture = await Fixture.CreateAsync(2);
    await fixture.Coordinator.BeginAsync(fixture.HttpContext, fixture.User, false, "/target");
    fixture.Access.ActiveMemberships.Clear();

    var result = await fixture.Coordinator.CompletePendingAsync(fixture.HttpContext, "OHM191112Q26");

    Assert.Equal(CompanyAuthenticationStatus.InvalidPendingSelection, result.Status);
    Assert.Null(fixture.Authentication.ApplicationPrincipal);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task Expired_or_tampered_pending_selection_is_rejected(bool tampered)
  {
    await using var fixture = await Fixture.CreateAsync(2);
    fixture.Authentication.PendingResult = tampered
      ? AuthenticateResult.Fail("tampered")
      : AuthenticateResult.NoResult();

    var result = await fixture.Coordinator.CompletePendingAsync(fixture.HttpContext, "OHM191112Q26");

    Assert.Equal(CompanyAuthenticationStatus.InvalidPendingSelection, result.Status);
    Assert.Null(fixture.Authentication.ApplicationPrincipal);
  }

  [Fact]
  public void Password_2fa_and_recovery_flows_share_the_same_coordinator()
  {
    foreach (var file in new[] { "Login.cshtml.cs", "LoginWith2fa.cshtml.cs", "LoginWithRecoveryCode.cshtml.cs" })
    {
      var source = ReadRepoFile($"src/OrionERP.Web/Areas/Identity/Pages/Account/{file}");
      Assert.Contains("ICompanyAuthenticationCoordinator", source, StringComparison.Ordinal);
      Assert.Contains("_companyAuthentication.BeginAsync", source, StringComparison.Ordinal);
    }
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath)));

  private sealed class Fixture : IAsyncDisposable
  {
    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _scope;

    private Fixture(
      ServiceProvider provider,
      AsyncServiceScope scope,
      ApplicationUser user,
      FakeCompanyAccessService access,
      FakeAuthenticationService authentication,
      DefaultHttpContext httpContext,
      ICompanyAuthenticationCoordinator coordinator)
      => (_provider, _scope, User, Access, Authentication, HttpContext, Coordinator)
        = (provider, scope, user, access, authentication, httpContext, coordinator);

    public ApplicationUser User { get; }
    public FakeCompanyAccessService Access { get; }
    public FakeAuthenticationService Authentication { get; }
    public DefaultHttpContext HttpContext { get; }
    public ICompanyAuthenticationCoordinator Coordinator { get; }

    public static async Task<Fixture> CreateAsync(int companyCount)
    {
      var access = new FakeCompanyAccessService();
      if (companyCount >= 1) access.Add(Option("OHM191112Q26", "Orion"));
      if (companyCount >= 2) access.Add(Option("BRUNOS260707L26", "Bruno's"));
      var authentication = new FakeAuthenticationService();

      var services = new ServiceCollection();
      services.AddLogging();
      services.AddOptions();
      services.AddAuthentication();
      services.AddHttpContextAccessor();
      services.AddDbContext<OrionIdentityDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
      services.AddIdentityCore<ApplicationUser>()
        .AddEntityFrameworkStores<OrionIdentityDbContext>()
        .AddSignInManager();
      services.AddScoped<ICompanySignInContext, CompanySignInContext>();
      services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, TestClaimsPrincipalFactory>();
      services.AddSingleton<ICompanyAccessService>(access);
      services.AddSingleton<IAuthenticationService>(authentication);
      services.AddScoped<ICompanyAuthenticationCoordinator, CompanyAuthenticationCoordinator>();
      var provider = services.BuildServiceProvider();
      var scope = provider.CreateAsyncScope();
      var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
      var user = new ApplicationUser { UserName = "user@orion.local", Email = "user@orion.local" };
      Assert.True((await userManager.CreateAsync(user)).Succeeded);

      var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
      scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = httpContext;
      return new Fixture(
        provider,
        scope,
        user,
        access,
        authentication,
        httpContext,
        scope.ServiceProvider.GetRequiredService<ICompanyAuthenticationCoordinator>());
    }

    public async ValueTask DisposeAsync()
    {
      await _scope.DisposeAsync();
      await _provider.DisposeAsync();
    }

    private static CompanyLoginOption Option(string rfc, string displayName)
      => new(rfc, displayName, displayName, null, null, null, 1, false);
  }

  private sealed class TestClaimsPrincipalFactory : IUserClaimsPrincipalFactory<ApplicationUser>
  {
    private readonly ICompanySignInContext _companyContext;

    public TestClaimsPrincipalFactory(ICompanySignInContext companyContext) => _companyContext = companyContext;

    public Task<ClaimsPrincipal> CreateAsync(ApplicationUser user)
    {
      var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id) };
      if (!string.IsNullOrWhiteSpace(_companyContext.SelectedRfc))
        claims.Add(new Claim(CompanyClaimTypes.Rfc, _companyContext.SelectedRfc));
      return Task.FromResult(new ClaimsPrincipal(new ClaimsIdentity(claims, IdentityConstants.ApplicationScheme)));
    }
  }

  private sealed class FakeAuthenticationService : IAuthenticationService
  {
    public AuthenticateResult PendingResult { get; set; } = AuthenticateResult.NoResult();
    public ClaimsPrincipal? ApplicationPrincipal { get; private set; }

    public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
      => Task.FromResult(scheme == CompanyAuthenticationSchemes.PendingCompanySelection
        ? PendingResult
        : AuthenticateResult.NoResult());

    public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

    public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
    {
      if (scheme == CompanyAuthenticationSchemes.PendingCompanySelection)
        PendingResult = AuthenticateResult.Success(new AuthenticationTicket(principal, properties, scheme));
      else
        ApplicationPrincipal = principal;
      return Task.CompletedTask;
    }

    public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
    {
      if (scheme == CompanyAuthenticationSchemes.PendingCompanySelection)
        PendingResult = AuthenticateResult.NoResult();
      return Task.CompletedTask;
    }
  }

  private sealed class FakeCompanyAccessService : ICompanyAccessService
  {
    public List<CompanyLoginOption> Options { get; } = [];
    public HashSet<string> ActiveMemberships { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Add(CompanyLoginOption option)
    {
      Options.Add(option);
      ActiveMemberships.Add(option.Rfc);
    }

    public Task<IReadOnlyList<CompanyLoginOption>> GetLoginOptionsAsync(string userId, CancellationToken ct = default)
      => Task.FromResult<IReadOnlyList<CompanyLoginOption>>(Options);
    public Task<bool> HasActiveMembershipAsync(string userId, string rfc, CancellationToken ct = default)
      => Task.FromResult(ActiveMemberships.Contains(rfc));
    public Task<IReadOnlyList<CompanySummary>> GetCompaniesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<CompanyEditor?> GetCompanyAsync(string rfc, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<CompanyCommandResult> SaveCompanyAsync(CompanySaveRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<CompanyCommandResult> SaveLogoAsync(string rfc, byte[] bytes, string contentType, string actorUserId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<CompanyLogo?> GetLogoAsync(string rfc, CancellationToken ct = default) => throw new NotSupportedException();
  }
}
