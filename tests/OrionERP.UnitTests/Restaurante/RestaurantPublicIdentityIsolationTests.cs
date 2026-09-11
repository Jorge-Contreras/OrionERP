using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrionERP.Application.Features.Platform;
using OrionERP.Bruno.Web.Pages.Account;
using OrionERP.Bruno.Web.Services;
using OrionERP.Infrastructure.Auth;
using OrionERP.Infrastructure.Features.Platform;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantPublicIdentityIsolationTests
{
  [Fact]
  public async Task Same_email_is_allowed_per_site_but_every_identity_lookup_stays_in_scope()
  {
    var root = new InMemoryDatabaseRoot();
    var databaseName = Guid.NewGuid().ToString("N");
    var scopeA = Scope(101, "restaurant-a", 1, "RFC-A", 11, "site-a");
    var scopeB = Scope(202, "restaurant-b", 2, "RFC-B", 22, "site-b");
    await using var providerA = CreateIdentityProvider(root, databaseName, scopeA);
    await using var providerB = CreateIdentityProvider(root, databaseName, scopeB);

    await using var serviceScopeA = providerA.CreateAsyncScope();
    await using var serviceScopeB = providerB.CreateAsyncScope();
    var managerA = serviceScopeA.ServiceProvider.GetRequiredService<UserManager<PublicSiteUser>>();
    var managerB = serviceScopeB.ServiceProvider.GetRequiredService<UserManager<PublicSiteUser>>();

    var userA = NewUser("member@example.test");
    var userB = NewUser("member@example.test");
    Assert.True((await managerA.CreateAsync(userA, "Password1!")).Succeeded);
    Assert.True((await managerB.CreateAsync(userB, "Password1!")).Succeeded);

    Assert.Equal(scopeA.PublicSiteId, userA.PublicSiteId);
    Assert.Equal(scopeB.PublicSiteId, userB.PublicSiteId);
    Assert.Equal(userA.Id, (await managerA.FindByEmailAsync("member@example.test"))?.Id);
    Assert.Equal(userB.Id, (await managerB.FindByEmailAsync("member@example.test"))?.Id);
    Assert.Equal(userA.Id, (await managerA.FindByNameAsync("member@example.test"))?.Id);
    Assert.Equal(userB.Id, (await managerB.FindByNameAsync("member@example.test"))?.Id);
    Assert.Null(await managerA.FindByIdAsync(userB.Id));
    Assert.Null(await managerB.FindByIdAsync(userA.Id));
    Assert.DoesNotContain(await managerA.Users.Select(user => user.Id).ToListAsync(), id => id == userB.Id);
    Assert.DoesNotContain(await managerB.Users.Select(user => user.Id).ToListAsync(), id => id == userA.Id);
  }

  [Fact]
  public async Task Cross_site_update_and_delete_are_rejected()
  {
    var root = new InMemoryDatabaseRoot();
    var databaseName = Guid.NewGuid().ToString("N");
    var scopeA = Scope(101, "restaurant-a", 1, "RFC-A", 11, "site-a");
    var scopeB = Scope(202, "restaurant-b", 2, "RFC-B", 22, "site-b");
    await using var providerA = CreateIdentityProvider(root, databaseName, scopeA);
    await using var providerB = CreateIdentityProvider(root, databaseName, scopeB);
    await using var serviceScopeA = providerA.CreateAsyncScope();
    await using var serviceScopeB = providerB.CreateAsyncScope();
    var managerA = serviceScopeA.ServiceProvider.GetRequiredService<UserManager<PublicSiteUser>>();
    var storeB = Assert.IsType<PublicSiteUserStore>(
      serviceScopeB.ServiceProvider.GetRequiredService<IUserStore<PublicSiteUser>>());

    var userA = NewUser("member-a@example.test");
    Assert.True((await managerA.CreateAsync(userA, "Password1!")).Succeeded);

    var update = await storeB.UpdateAsync(userA);
    var delete = await storeB.DeleteAsync(userA);
    Assert.False(update.Succeeded);
    Assert.False(delete.Succeeded);
    Assert.All(update.Errors.Concat(delete.Errors), error => Assert.Equal("PublicSiteMismatch", error.Code));
  }

  [Fact]
  public async Task Forgot_and_resend_flows_with_same_email_target_only_the_current_site_user()
  {
    var root = new InMemoryDatabaseRoot();
    var databaseName = Guid.NewGuid().ToString("N");
    var dataProtection = new EphemeralDataProtectionProvider();
    var scopeA = Scope(101, "restaurant-a", 1, "RFC-A", 11, "site-a");
    var scopeB = Scope(202, "restaurant-b", 2, "RFC-B", 22, "site-b");
    await using var providerA = CreateIdentityProvider(root, databaseName, scopeA, dataProtection);
    await using var providerB = CreateIdentityProvider(root, databaseName, scopeB, dataProtection);
    await using var serviceScopeA = providerA.CreateAsyncScope();
    await using var serviceScopeB = providerB.CreateAsyncScope();
    var managerA = serviceScopeA.ServiceProvider.GetRequiredService<UserManager<PublicSiteUser>>();
    var managerB = serviceScopeB.ServiceProvider.GetRequiredService<UserManager<PublicSiteUser>>();
    var userA = NewUser("shared@example.test");
    var userB = NewUser("shared@example.test");
    Assert.True((await managerA.CreateAsync(userA, "Password1!")).Succeeded);
    Assert.True((await managerB.CreateAsync(userB, "Password1!")).Succeeded);

    userB.EmailConfirmed = true;
    Assert.True((await managerB.UpdateAsync(userB)).Succeeded);
    var forgotSender = new RecordingEmailSender();
    var forgot = new ForgotPasswordModel(managerB, forgotSender)
    {
      Email = "shared@example.test",
      Url = new FixedUrlHelper()
    };
    SetPageContext(forgot, providerB);
    await forgot.OnPostAsync();

    Assert.Equal(userB.Id, Assert.Single(forgotSender.PasswordResetUsers));
    Assert.DoesNotContain(userA.Id, forgotSender.PasswordResetUsers);

    userB.EmailConfirmed = false;
    Assert.True((await managerB.UpdateAsync(userB)).Succeeded);
    var resendSender = new RecordingEmailSender();
    var resend = new ResendConfirmationModel(
      managerB,
      resendSender,
      new AcceptingTurnstile(),
      NullLogger<ResendConfirmationModel>.Instance)
    {
      Email = "shared@example.test",
      Url = new FixedUrlHelper()
    };
    SetPageContext(resend, providerB, includeForm: true);
    await resend.OnPostAsync();

    Assert.Equal(userB.Id, Assert.Single(resendSender.ConfirmationUsers));
    Assert.DoesNotContain(userA.Id, resendSender.ConfirmationUsers);
  }

  [Fact]
  public async Task Reset_and_confirmation_artifacts_from_site_A_cannot_modify_site_B()
  {
    var root = new InMemoryDatabaseRoot();
    var databaseName = Guid.NewGuid().ToString("N");
    var dataProtection = new EphemeralDataProtectionProvider();
    var scopeA = Scope(101, "restaurant-a", 1, "RFC-A", 11, "site-a");
    var scopeB = Scope(202, "restaurant-b", 2, "RFC-B", 22, "site-b");
    await using var providerA = CreateIdentityProvider(root, databaseName, scopeA, dataProtection);
    await using var providerB = CreateIdentityProvider(root, databaseName, scopeB, dataProtection);
    await using var serviceScopeA = providerA.CreateAsyncScope();
    await using var serviceScopeB = providerB.CreateAsyncScope();
    var managerA = serviceScopeA.ServiceProvider.GetRequiredService<UserManager<PublicSiteUser>>();
    var managerB = serviceScopeB.ServiceProvider.GetRequiredService<UserManager<PublicSiteUser>>();
    var userA = NewUser("shared@example.test");
    var userB = NewUser("shared@example.test");
    Assert.True((await managerA.CreateAsync(userA, "Password1!")).Succeeded);
    Assert.True((await managerB.CreateAsync(userB, "Password1!")).Succeeded);

    var resetTokenA = await managerA.GeneratePasswordResetTokenAsync(userA);
    var reset = new ResetPasswordModel(managerB)
    {
      Input = new ResetPasswordModel.InputModel
      {
        Email = "shared@example.test",
        Code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(resetTokenA)),
        Password = "Changed2!",
        ConfirmPassword = "Changed2!"
      }
    };
    SetPageContext(reset, providerB);
    await reset.OnPostAsync();

    Assert.False(reset.Completed);
    Assert.True(await managerA.CheckPasswordAsync(userA, "Password1!"));
    Assert.True(await managerB.CheckPasswordAsync(userB, "Password1!"));
    Assert.False(await managerB.CheckPasswordAsync(userB, "Changed2!"));

    var confirmationTokenA = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(
      await managerA.GenerateEmailConfirmationTokenAsync(userA)));
    var confirm = new ConfirmEmailModel(
      managerB,
      null!,
      null!,
      null!,
      new FixedScopeAccessor(scopeB));
    SetPageContext(confirm, providerB);
    await confirm.OnGetAsync(userA.Id, confirmationTokenA, CancellationToken.None);

    Assert.False(userA.EmailConfirmed);
    Assert.False(userB.EmailConfirmed);
    Assert.False(confirm.Success);

    var foreignPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
      [new Claim(ClaimTypes.NameIdentifier, userA.Id)],
      IdentityConstants.ApplicationScheme));
    Assert.Null(await managerB.GetUserAsync(foreignPrincipal));
  }

  [Fact]
  public void Principal_scope_requires_every_exact_company_and_site_claim()
  {
    var scopeA = Scope(101, "restaurant-a", 1, "RFC-A", 11, "site-a");
    var scopeB = Scope(202, "restaurant-b", 2, "RFC-B", 22, "site-b");
    var principal = Principal(scopeA);

    Assert.True(PublicIdentityScopePolicy.Matches(principal, scopeA));
    Assert.False(PublicIdentityScopePolicy.Matches(principal, scopeB));

    ((ClaimsIdentity)principal.Identity!).AddClaim(
      new Claim(PublicIdentityClaimTypes.PublicSiteId, scopeA.PublicSiteId.ToString()));
    Assert.False(PublicIdentityScopePolicy.Matches(principal, scopeA));
  }

  [Theory]
  [InlineData("foreign")]
  [InlineData("missing")]
  [InlineData("duplicate")]
  public async Task Cookie_validation_preserves_security_stamp_and_rejects_invalid_scope(string mutation)
  {
    var current = Scope(101, "restaurant-a", 1, "RFC-A", 11, "site-a");
    var principal = Principal(current);
    var identity = (ClaimsIdentity)principal.Identity!;
    if (mutation == "foreign")
    {
      var claim = identity.FindFirst(PublicIdentityClaimTypes.PublicSiteId)!;
      identity.RemoveClaim(claim);
      identity.AddClaim(new Claim(PublicIdentityClaimTypes.PublicSiteId, "202"));
    }
    else if (mutation == "missing")
    {
      identity.RemoveClaim(identity.FindFirst(PublicIdentityClaimTypes.SiteKey)!);
    }
    else
    {
      identity.AddClaim(new Claim(PublicIdentityClaimTypes.CompanyRfc, current.CompanyRfc));
    }

    var stampValidator = new RecordingSecurityStampValidator();
    var authentication = new RecordingAuthenticationService();
    var services = new ServiceCollection()
      .AddSingleton<IPublicIdentityScopeAccessor>(new FixedScopeAccessor(current))
      .AddSingleton<ISecurityStampValidator>(stampValidator)
      .AddSingleton<IAuthenticationService>(authentication)
      .BuildServiceProvider();
    var httpContext = new DefaultHttpContext { RequestServices = services };
    var context = CookieContext(httpContext, principal);

    await PublicSiteUserCookieScopeValidator.ValidatePrincipalAsync(context);

    Assert.True(stampValidator.Called);
    Assert.Null(context.Principal);
    Assert.True(authentication.SignedOut);
  }

  [Fact]
  public async Task Health_check_ignores_member_cookie_before_binding_and_security_stamp_work()
  {
    var services = new ServiceCollection().BuildServiceProvider();
    var httpContext = new DefaultHttpContext { RequestServices = services };
    httpContext.Request.Path = "/healthz";
    var context = CookieContext(httpContext, Principal(Scope(101, "restaurant-a", 1, "RFC-A", 11, "site-a")));

    await PublicSiteUserCookieScopeValidator.ValidatePrincipalAsync(context);

    Assert.NotNull(context.Principal);
  }

  [Fact]
  public void Scope_accessor_uses_verified_binding_and_ignores_request_tenant_values()
  {
    var definition = new PublicWebsiteInstanceDefinition(
      "restaurant-a", "RFC-A", "site-a", PlatformModuleCodes.Restaurant, "a.example.test", 5020);
    var httpContext = new DefaultHttpContext();
    httpContext.Request.QueryString = new QueryString("?rfc=RFC-B&publicSiteId=202&siteKey=site-b");
    httpContext.Request.Headers["X-Orion-Rfc"] = "RFC-B";
    httpContext.Items[PublicWebsiteBindingGateMiddleware.BindingItemKey] = Binding(definition, 101, 1, 11);
    var accessor = new VerifiedPublicIdentityScopeAccessor(
      new HttpContextAccessor { HttpContext = httpContext },
      new StubWebsiteContext(definition));

    var actual = accessor.Current;

    Assert.Equal(101, actual.PublicSiteId);
    Assert.Equal("RFC-A", actual.CompanyRfc);
    Assert.Equal("site-a", actual.SiteKey);
  }

  [Fact]
  public void Scope_accessor_fails_closed_without_the_verified_gate_binding()
  {
    var definition = new PublicWebsiteInstanceDefinition(
      "restaurant-a", "RFC-A", "site-a", PlatformModuleCodes.Restaurant, "a.example.test", 5020);
    var accessor = new VerifiedPublicIdentityScopeAccessor(
      new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
      new StubWebsiteContext(definition));

    Assert.Throws<InvalidOperationException>(() => accessor.Current);
  }

  [Fact]
  public void Sandbox_migration_enforces_identity_and_membership_scope_at_the_database_boundary()
  {
    var sql = ReadRepoFile(
      "src/OrionERP.Infrastructure/Features/Restaurante/Sql/20260903_restaurant_public_identity_scope_sandbox.sql");

    Assert.Contains("@ExpectedDatabase <> N'Orion_Sandbox'", sql, StringComparison.Ordinal);
    Assert.Contains("DB_NAME() <> N'Orion_Sandbox'", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("N'grupocarpio'", sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("UserNameIndex_Bruno", sql, StringComparison.Ordinal);
    Assert.Contains("(PublicSiteId,NormalizedUserName)", sql, StringComparison.Ordinal);
    Assert.Contains("(PublicSiteId,NormalizedEmail)", sql, StringComparison.Ordinal);
    Assert.Contains("FK_BrunoAspNetUsers_PublicSite", sql, StringComparison.Ordinal);
    Assert.Contains("FK_LoyaltyMember_PublicSite", sql, StringComparison.Ordinal);
    Assert.Contains("FK_LoyaltyMember_IdentityScope", sql, StringComparison.Ordinal);
    Assert.Contains("FOREIGN KEY(IdentityUserId,PublicSiteId)", sql, StringComparison.Ordinal);
    Assert.Contains("TR_AspNetUsers_RestaurantIdentityScope", sql, StringComparison.Ordinal);
    Assert.Contains("TR_MemberAccount_PublicIdentityScope", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Readiness_requires_the_applied_neutral_identity_migration_with_its_frozen_checksum()
  {
    var sql = ReadReadinessConstant("ReadinessSql");
    var migrationId = ReadReadinessConstant("RequiredMigrationId");
    var configuredChecksum = ReadReadinessConstant("RequiredMigrationChecksum");
    var migrationBytes = File.ReadAllBytes(RepoPath(
      "src/OrionERP.Infrastructure/Features/Platform/Sql/20260911_public_identity.sql"));

    Assert.Equal("20260911_public_identity", migrationId);
    Assert.Equal(Convert.ToHexString(SHA256.HashData(migrationBytes)), configuredChecksum);
    Assert.Contains("MigrationId=@RequiredMigrationId", sql, StringComparison.Ordinal);
    Assert.Contains("UPPER(Checksum)=@RequiredMigrationChecksum", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Readiness_requires_the_neutral_policy_and_all_twenty_one_predicates()
  {
    var sql = ReadReadinessConstant("ReadinessSql");

    Assert.Contains("OBJECT_ID(N'orion.PublicIdentityScopePolicy')", sql, StringComparison.Ordinal);
    Assert.Contains("is_enabled=1 AND is_schema_bound=1", sql, StringComparison.Ordinal);
    Assert.Contains("=21", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Readiness_requires_not_null_scope_columns_on_all_identity_tables()
  {
    var sql = ReadReadinessConstant("ReadinessSql");

    foreach (var table in new[]
    {
      "AspNetUsers", "AspNetRoles", "AspNetUserClaims", "AspNetRoleClaims",
      "AspNetUserLogins", "AspNetUserRoles", "AspNetUserTokens"
    })
      Assert.Contains($"(N'{table}')", sql, StringComparison.Ordinal);
    Assert.Contains("siteColumn.name=N'PublicSiteId'", sql, StringComparison.Ordinal);
    Assert.Contains("siteColumn.is_nullable<>0", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Readiness_rejects_forward_bridge_mode_for_the_new_writer()
  {
    var sql = ReadReadinessConstant("ReadinessSql");

    Assert.Contains("BridgeMode IN('Reverse','Off')", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("BridgeMode IN('Forward','Reverse','Off')", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Loyalty_membership_commands_require_the_same_public_site_and_company()
  {
    var source = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/LoyaltyService.cs");

    Assert.Contains("identityUser.PublicSiteId=@PublicSiteId", source, StringComparison.Ordinal);
    Assert.Contains("company.Rfc=@Rfc", source, StringComparison.Ordinal);
    Assert.Contains("publicSite.ModuleCode='RESTAURANT'", source, StringComparison.Ordinal);
    Assert.Contains("member.PublicSiteId=@PublicSiteId", source, StringComparison.Ordinal);
    Assert.Contains("userInfo.PublicSiteId=member.PublicSiteId", source, StringComparison.Ordinal);
  }

  private static ServiceProvider CreateIdentityProvider(
    InMemoryDatabaseRoot root,
    string databaseName,
    PublicIdentityScope identityScope,
    IDataProtectionProvider? dataProtection = null)
  {
    var services = new ServiceCollection();
    services.AddLogging();
    if (dataProtection is null) services.AddDataProtection();
    else services.AddSingleton(dataProtection);
    services.AddSingleton<IPublicIdentityScopeAccessor>(new FixedScopeAccessor(identityScope));
    services.AddDbContext<PublicIdentityDbContext>(options =>
      options.UseInMemoryDatabase(databaseName, root));
    services
      .AddIdentityCore<PublicSiteUser>(options =>
      {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 8;
      })
      .AddRoles<PublicSiteRole>()
      .AddEntityFrameworkStores<PublicIdentityDbContext>()
      .AddUserStore<PublicSiteUserStore>()
      .AddDefaultTokenProviders();
    return services.BuildServiceProvider(validateScopes: true);
  }

  private static PublicSiteUser NewUser(string email) => new()
  {
    UserName = email,
    Email = email,
    FirstName = "Member",
    LastName = "Test"
  };

  private static PublicIdentityScope Scope(
    long publicSiteId,
    string publicSiteKey,
    long companyId,
    string companyRfc,
    long siteId,
    string siteKey)
    => new(publicSiteId, publicSiteKey, companyId, companyRfc, siteId, siteKey, PlatformModuleCodes.Restaurant);

  private static ClaimsPrincipal Principal(PublicIdentityScope scope)
  {
    var claims = new[]
    {
      new Claim(ClaimTypes.NameIdentifier, "user-1"),
      new Claim(PublicIdentityClaimTypes.PublicSiteId, scope.PublicSiteId.ToString()),
      new Claim(PublicIdentityClaimTypes.PublicSiteKey, scope.PublicSiteKey),
      new Claim(PublicIdentityClaimTypes.CompanyId, scope.CompanyId.ToString()),
      new Claim(PublicIdentityClaimTypes.CompanyRfc, scope.CompanyRfc),
      new Claim(PublicIdentityClaimTypes.SiteId, scope.SiteId.ToString()),
      new Claim(PublicIdentityClaimTypes.SiteKey, scope.SiteKey),
      new Claim(PublicIdentityClaimTypes.ModuleCode, scope.ModuleCode)
    };
    return new ClaimsPrincipal(new ClaimsIdentity(claims, "restaurant-cookie"));
  }

  private static CookieValidatePrincipalContext CookieContext(
    HttpContext httpContext,
    ClaimsPrincipal principal)
  {
    var scheme = new AuthenticationScheme(
      IdentityConstants.ApplicationScheme,
      IdentityConstants.ApplicationScheme,
      typeof(CookieAuthenticationHandler));
    var ticket = new AuthenticationTicket(principal, IdentityConstants.ApplicationScheme);
    return new CookieValidatePrincipalContext(
      httpContext,
      scheme,
      new CookieAuthenticationOptions(),
      ticket);
  }

  private static void SetPageContext(
    PageModel model,
    IServiceProvider services,
    bool includeForm = false)
  {
    var httpContext = new DefaultHttpContext { RequestServices = services };
    httpContext.Request.Scheme = "https";
    if (includeForm)
    {
      httpContext.Features.Set<IFormFeature>(new FormFeature(new FormCollection(
        new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
          ["cf-turnstile-response"] = "accepted"
        })));
    }
    model.PageContext = new PageContext { HttpContext = httpContext };
  }

  private static PublicSiteBinding Binding(
    PublicWebsiteInstanceDefinition definition,
    long publicSiteId,
    long companyId,
    long siteId)
    => new(
      publicSiteId,
      definition.PublicSiteKey,
      companyId,
      definition.ExpectedCompanyRfc,
      null,
      definition.ExpectedCompanyRfc,
      siteId,
      definition.SiteKey,
      definition.SiteKey,
      "America/Mexico_City",
      definition.ModuleCode,
      1,
      definition.CanonicalHost,
      1,
      1,
      1);

  private static string ReadReadinessConstant(string fieldName)
  {
    var field = typeof(PublicIdentityReadiness).GetField(
      fieldName,
      BindingFlags.NonPublic | BindingFlags.Static);
    return Assert.IsType<string>(field?.GetRawConstantValue());
  }

  private static string RepoPath(string relativePath)
  {
    var root = AppContext.BaseDirectory;
    while (!File.Exists(Path.Combine(root, "OrionERP.sln")))
    {
      root = Directory.GetParent(root)?.FullName
        ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
    return Path.Combine(root, relativePath);
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(RepoPath(relativePath));

  private sealed class FixedScopeAccessor(PublicIdentityScope scope)
    : IPublicIdentityScopeAccessor
  {
    public PublicIdentityScope Current { get; } = scope;
  }

  private sealed class StubWebsiteContext(PublicWebsiteInstanceDefinition definition)
    : IPublicWebsiteInstanceContext
  {
    public PublicWebsiteInstanceDefinition Instance { get; } = definition;
    public string CurrentRfc => Instance.ExpectedCompanyRfc;
    public Task<PublicSiteBinding> ResolveRequiredAsync(CancellationToken ct = default)
      => throw new NotSupportedException();
  }

  private sealed class RecordingSecurityStampValidator : ISecurityStampValidator
  {
    public bool Called { get; private set; }
    public Task ValidateAsync(CookieValidatePrincipalContext context)
    {
      Called = true;
      return Task.CompletedTask;
    }
  }

  private sealed class RecordingAuthenticationService : IAuthenticationService
  {
    public bool SignedOut { get; private set; }

    public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
      => Task.FromResult(AuthenticateResult.NoResult());

    public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
      => Task.CompletedTask;

    public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
      => Task.CompletedTask;

    public Task SignInAsync(
      HttpContext context,
      string? scheme,
      ClaimsPrincipal principal,
      AuthenticationProperties? properties)
      => Task.CompletedTask;

    public Task SignOutAsync(
      HttpContext context,
      string? scheme,
      AuthenticationProperties? properties)
    {
      SignedOut = true;
      return Task.CompletedTask;
    }
  }

  private sealed class RecordingEmailSender : IEmailSender<PublicSiteUser>
  {
    public List<string> ConfirmationUsers { get; } = [];
    public List<string> PasswordResetUsers { get; } = [];

    public Task SendConfirmationLinkAsync(PublicSiteUser user, string email, string confirmationLink)
    {
      ConfirmationUsers.Add(user.Id);
      return Task.CompletedTask;
    }

    public Task SendPasswordResetLinkAsync(PublicSiteUser user, string email, string resetLink)
    {
      PasswordResetUsers.Add(user.Id);
      return Task.CompletedTask;
    }

    public Task SendPasswordResetCodeAsync(PublicSiteUser user, string email, string resetCode)
      => Task.CompletedTask;
  }

  private sealed class AcceptingTurnstile : IBrunoTurnstileService
  {
    public Task<bool> ValidateAsync(
      string? token,
      string? remoteIp,
      string expectedAction,
      CancellationToken ct = default)
      => Task.FromResult(true);
  }

  private sealed class FixedUrlHelper : IUrlHelper
  {
    public ActionContext ActionContext { get; } = new(
      new DefaultHttpContext(),
      new RouteData(),
      new ActionDescriptor());
    public string? Action(UrlActionContext actionContext) => "https://restaurant.example.test/action";
    public string? Content(string? contentPath) => contentPath;
    public bool IsLocalUrl(string? url) => url?.StartsWith('/') == true;
    public string? Link(string? routeName, object? values) => "https://restaurant.example.test/link";
    public string? RouteUrl(UrlRouteContext routeContext) => "https://restaurant.example.test/account-action";
  }
}
