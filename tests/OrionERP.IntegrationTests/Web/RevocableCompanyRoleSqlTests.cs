using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrionERP.Infrastructure.Auth;
using OrionERP.Web.Identity;

namespace OrionERP.IntegrationTests.Web;

public sealed class RevocableCompanyRoleSqlTests
{
  public static IEnumerable<object[]> RestrictedRoleCases()
  {
    yield return ["SatOperator", CompanyOperationPolicies.HospitalitySite];
    yield return ["OrdenTrabajoOperador", CompanyOperationPolicies.WorkOrders];
    yield return ["Logistica", CompanyOperationPolicies.Logistics + "," + CompanyOperationPolicies.PhysicalCounts];
    yield return ["Conteo", CompanyOperationPolicies.PhysicalCounts];
    yield return ["RestauranteCaja", "RestaurantPos"];
  }

  [Theory, Trait("Category", "SqlIntegration")]
  [MemberData(nameof(RestrictedRoleCases))]
  public async Task RestrictedRole_RevalidatesCompanySwitchAndRevocationInTheSamePrincipal(
    string roleName,
    string expectedPolicyNames)
  {
    if (Environment.GetEnvironmentVariable("ORION_RUN_SQL_INTEGRATION") != "1") return;
    var source = Environment.GetEnvironmentVariable("ASPNETCORE_ConnectionStrings__OrionDb")
      ?? throw new InvalidOperationException("Missing Sandbox connection.");
    var builder = new SqlConnectionStringBuilder(source) { InitialCatalog = "Orion_Sandbox" };
    Assert.Equal("Orion_Sandbox", builder.InitialCatalog, ignoreCase: true);
    var dbOptions = new DbContextOptionsBuilder<OrionIdentityDbContext>()
      .UseSqlServer(builder.ConnectionString)
      .Options;
    await using var db = new OrionIdentityDbContext(dbOptions);
    await db.Database.OpenConnectionAsync();
    Assert.Equal("Orion_Sandbox",
      await db.Database.GetDbConnection().ExecuteScalarAsync<string>("SELECT DB_NAME();"),
      ignoreCase: true);

    var sites = (await db.Database.GetDbConnection().QueryAsync<ScopeRow>("""
      SELECT publicSite.PublicSiteKey, company.Rfc
      FROM orion.PublicSite publicSite
      INNER JOIN orion.Company company ON company.CompanyId = publicSite.CompanyId
      WHERE publicSite.PublicSiteKey IN ('bonhomia-main', 'brunos-main');
      """)).ToDictionary(row => row.PublicSiteKey, StringComparer.OrdinalIgnoreCase);
    Assert.Equal(2, sites.Count);
    var originalRfc = roleName == "RestauranteCaja"
      ? sites["brunos-main"].Rfc
      : sites["bonhomia-main"].Rfc;
    var otherRfc = string.Equals(originalRfc, sites["bonhomia-main"].Rfc, StringComparison.OrdinalIgnoreCase)
      ? sites["brunos-main"].Rfc
      : sites["bonhomia-main"].Rfc;
    var normalizedRole = roleName.ToUpperInvariant();
    var role = await db.Roles.SingleAsync(row => row.NormalizedName == normalizedRole);
    var userId = "role-matrix-" + Guid.NewGuid().ToString("N");
    var email = userId + "@integration.invalid";
    var expected = expectedPolicyNames.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    var policies = CreatePolicies();

    try
    {
      db.Users.Add(new ApplicationUser
      {
        Id = userId,
        UserName = email,
        NormalizedUserName = email.ToUpperInvariant(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        EmailConfirmed = true,
        SecurityStamp = Guid.NewGuid().ToString("N"),
        ConcurrencyStamp = Guid.NewGuid().ToString("N")
      });
      var now = DateTime.UtcNow;
      db.UserCompanies.AddRange(
        new UserCompany { UserId = userId, Rfc = originalRfc, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now, UpdatedBy = "role-matrix" },
        new UserCompany { UserId = userId, Rfc = otherRfc, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now, UpdatedBy = "role-matrix" });
      db.UserCompanyRoles.Add(new UserCompanyRole { UserId = userId, Rfc = originalRfc, RoleId = role.Id });
      await db.SaveChangesAsync();
      db.ChangeTracker.Clear();

      var originalPrincipal = Principal(userId, originalRfc, roleName);
      foreach (var (policyName, policy) in policies)
        Assert.Equal(expected.Contains(policyName), await AuthorizeAsync(db, originalPrincipal, policy));

      // A role from one company must not authorize the same stale claim after selecting another company.
      var switchedPrincipal = Principal(userId, otherRfc, roleName);
      foreach (var policy in policies.Values)
        Assert.False(await AuthorizeAsync(db, switchedPrincipal, policy));

      db.UserCompanyRoles.Add(new UserCompanyRole { UserId = userId, Rfc = otherRfc, RoleId = role.Id });
      await db.SaveChangesAsync();
      db.ChangeTracker.Clear();
      foreach (var (policyName, policy) in policies)
        Assert.Equal(expected.Contains(policyName), await AuthorizeAsync(db, switchedPrincipal, policy));

      // Keep the already-issued principal unchanged: the fresh requirement must observe the revocation.
      var originalLink = await db.UserCompanyRoles.SingleAsync(link =>
        link.UserId == userId && link.Rfc == originalRfc && link.RoleId == role.Id);
      db.UserCompanyRoles.Remove(originalLink);
      await db.SaveChangesAsync();
      db.ChangeTracker.Clear();
      foreach (var policy in policies.Values)
        Assert.False(await AuthorizeAsync(db, originalPrincipal, policy));

      var switchedMembership = await db.UserCompanies.SingleAsync(link =>
        link.UserId == userId && link.Rfc == otherRfc);
      switchedMembership.IsActive = false;
      switchedMembership.UpdatedAtUtc = DateTime.UtcNow;
      switchedMembership.UpdatedBy = "role-matrix-revocation";
      await db.SaveChangesAsync();
      db.ChangeTracker.Clear();
      foreach (var policy in policies.Values)
        Assert.False(await AuthorizeAsync(db, switchedPrincipal, policy));
    }
    finally
    {
      db.ChangeTracker.Clear();
      var user = await db.Users.SingleOrDefaultAsync(row => row.Id == userId);
      if (user is not null)
      {
        db.Users.Remove(user);
        await db.SaveChangesAsync();
      }
      db.ChangeTracker.Clear();
      Assert.False(await db.Users.AnyAsync(row => row.Id == userId));
      Assert.False(await db.UserCompanies.AnyAsync(row => row.UserId == userId));
      Assert.False(await db.UserCompanyRoles.AnyAsync(row => row.UserId == userId));
    }
  }

  private static IReadOnlyDictionary<string, AuthorizationPolicy> CreatePolicies()
  {
    var options = new AuthorizationOptions();
    options.AddRevocableCompanyOperationPolicies();
    options.AddPolicy("RestaurantPos", policy =>
      policy.RequireRevocableCompanyRoles("RestauranteCaja", "RestauranteSupervisor", "RestauranteAdmin"));
    return new[]
    {
      CompanyOperationPolicies.HospitalitySite,
      CompanyOperationPolicies.WorkOrders,
      CompanyOperationPolicies.Logistics,
      CompanyOperationPolicies.PhysicalCounts,
      "RestaurantPos"
    }.ToDictionary(name => name, name => options.GetPolicy(name)!);
  }

  private static ClaimsPrincipal Principal(string userId, string rfc, string roleName)
    => new(new ClaimsIdentity([
      new Claim(ClaimTypes.NameIdentifier, userId),
      new Claim(ClaimTypes.Role, roleName),
      new Claim(CompanyClaimTypes.Rfc, rfc)
    ], "role-matrix", ClaimTypes.Name, ClaimTypes.Role));

  private static async Task<bool> AuthorizeAsync(
    OrionIdentityDbContext db,
    ClaimsPrincipal principal,
    AuthorizationPolicy policy)
  {
    var context = new AuthorizationHandlerContext(policy.Requirements, principal, resource: null);
    foreach (var handler in policy.Requirements.OfType<IAuthorizationHandler>())
      await handler.HandleAsync(context);
    await new RevocableCompanyRoleAuthorizationHandler(db, TimeProvider.System).HandleAsync(context);
    return context.HasSucceeded;
  }

  private sealed record ScopeRow(string PublicSiteKey, string Rfc);
}
