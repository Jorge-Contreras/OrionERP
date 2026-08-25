using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OrionERP.Infrastructure.Auth;
using OrionERP.Web.Identity;

namespace OrionERP.UnitTests.Auth;

public sealed class CompanyClaimsPrincipalFactoryTests
{
  [Fact]
  public async Task CreateAsync_ComposesOnlyGlobalAndSelectedCompanyRoles()
  {
    using var provider = CreateProvider();
    using var scope = provider.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<OrionIdentityDbContext>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    var selection = scope.ServiceProvider.GetRequiredService<ICompanySignInContext>();
    var factory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();

    var user = new ApplicationUser { UserName = "chayo@orion.land", Email = "chayo@orion.land" };
    Assert.True((await userManager.CreateAsync(user)).Succeeded);
    var admin = await CreateRoleAsync(db, roleManager, "Administrador", IdentityRoleScopes.Global);
    var kitchen = await CreateRoleAsync(db, roleManager, "RestauranteCocina", IdentityRoleScopes.Company);
    var logistics = await CreateRoleAsync(db, roleManager, "Logistica", IdentityRoleScopes.Company);
    await userManager.AddToRolesAsync(user, [admin.Name!, kitchen.Name!, logistics.Name!]);

    db.Companies.AddRange(
      new OrionCompany { Rfc = "OHM191112Q26", DisplayName = "Orion", IsActive = true },
      new OrionCompany { Rfc = "BRUNOS260707L26", DisplayName = "Bruno's", IsActive = true });
    db.UserCompanies.AddRange(
      new UserCompany { UserId = user.Id, Rfc = "OHM191112Q26", EmployeeId = 90, IsActive = true },
      new UserCompany { UserId = user.Id, Rfc = "BRUNOS260707L26", EmployeeId = 92, IsActive = true });
    db.UserCompanyRoles.AddRange(
      new UserCompanyRole { UserId = user.Id, Rfc = "OHM191112Q26", RoleId = logistics.Id },
      new UserCompanyRole { UserId = user.Id, Rfc = "BRUNOS260707L26", RoleId = kitchen.Id });
    await db.SaveChangesAsync();

    selection.SelectedRfc = "BRUNOS260707L26";
    var principal = await factory.CreateAsync(user);

    Assert.Equal("BRUNOS260707L26", Assert.Single(principal.FindAll(CompanyClaimTypes.Rfc)).Value);
    Assert.Equal("92", principal.FindFirstValue(CompanyClaimTypes.EmployeeId));
    Assert.Equal("BRUNOS260707L26", principal.FindFirstValue(CompanyClaimTypes.EmployeeRfc));
    Assert.True(principal.IsInRole("Administrador"));
    Assert.True(principal.IsInRole("RestauranteCocina"));
    Assert.False(principal.IsInRole("Logistica"));
  }

  [Fact]
  public async Task CreateAsync_DoesNotIssueCompanyClaimsForDisabledMembership()
  {
    using var provider = CreateProvider();
    using var scope = provider.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<OrionIdentityDbContext>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var selection = scope.ServiceProvider.GetRequiredService<ICompanySignInContext>();
    var factory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
    var user = new ApplicationUser { UserName = "disabled@orion.land" };
    await userManager.CreateAsync(user);
    db.Companies.Add(new OrionCompany { Rfc = "OHM191112Q26", DisplayName = "Orion", IsActive = true });
    db.UserCompanies.Add(new UserCompany { UserId = user.Id, Rfc = "OHM191112Q26", IsActive = false });
    await db.SaveChangesAsync();

    selection.SelectedRfc = "OHM191112Q26";
    var principal = await factory.CreateAsync(user);

    Assert.Empty(principal.FindAll(CompanyClaimTypes.Rfc));
    Assert.Null(principal.FindFirst(CompanyClaimTypes.SessionVersion));
  }

  [Fact]
  public async Task CreateAsync_RemovesReservedLegacyUserClaims()
  {
    using var provider = CreateProvider();
    using var scope = provider.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<OrionIdentityDbContext>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var selection = scope.ServiceProvider.GetRequiredService<ICompanySignInContext>();
    var factory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
    var user = new ApplicationUser { UserName = "legacy@orion.land" };
    await userManager.CreateAsync(user);
    await userManager.AddClaimsAsync(user, [new Claim("rfc", "OLD000000000"), new Claim("employee_id", "123"), new Claim("custom", "kept")]);
    db.Companies.Add(new OrionCompany { Rfc = "OHM191112Q26", DisplayName = "Orion", IsActive = true });
    db.UserCompanies.Add(new UserCompany { UserId = user.Id, Rfc = "OHM191112Q26", EmployeeId = 90, IsActive = true });
    await db.SaveChangesAsync();

    selection.SelectedRfc = "OHM191112Q26";
    var principal = await factory.CreateAsync(user);

    Assert.Equal("OHM191112Q26", Assert.Single(principal.FindAll("rfc")).Value);
    Assert.Equal("90", Assert.Single(principal.FindAll("employee_id")).Value);
    Assert.Equal("kept", principal.FindFirstValue("custom"));
  }

  private static async Task<IdentityRole> CreateRoleAsync(OrionIdentityDbContext db, RoleManager<IdentityRole> manager, string name, string scope)
  {
    var role = new IdentityRole(name);
    Assert.True((await manager.CreateAsync(role)).Succeeded);
    db.Entry(role).Property<string>("Scope").CurrentValue = scope;
    await db.SaveChangesAsync();
    return role;
  }

  private static ServiceProvider CreateProvider()
  {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddOptions();
    services.AddHttpContextAccessor();
    services.AddDbContext<OrionIdentityDbContext>(options => options
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
    services.AddIdentityCore<ApplicationUser>().AddRoles<IdentityRole>().AddEntityFrameworkStores<OrionIdentityDbContext>();
    services.AddScoped<ICompanySignInContext, CompanySignInContext>();
    services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, CompanyClaimsPrincipalFactory>();
    return services.BuildServiceProvider();
  }
}
