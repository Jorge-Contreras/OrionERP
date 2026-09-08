using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Reservaciones;
using OrionERP.Infrastructure.Auth;
using OrionERP.Infrastructure.Features.Reservaciones;

namespace OrionERP.UnitTests.Reservaciones;

public sealed class HospitalityAdministrationAuthorizationTests
{
  private const string Rfc = "TEST010101AAA";

  [Theory]
  [InlineData("Administrador")]
  [InlineData("SatOperator")]
  public async Task GuardAcceptsBothOperationalRolesAndRevalidatesEachOperation(string role)
  {
    var validator = new MutableAccessValidator();
    var guard = new HospitalityAdministrationSessionGuard(new TestAuthentication(User(role)), new TestCompany(), validator);
    Assert.Equal(Rfc, await guard.RequireCompanyRfcAsync());
    validator.Revoked = true;
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => guard.RequireCompanyRfcAsync());
    Assert.Equal(2, validator.Calls);
  }

  [Theory]
  [InlineData("Lectura")]
  [InlineData("Arrendadores")]
  [InlineData("OrdenTrabajoOperador")]
  public async Task GuardRejectsOtherRolesBeforeAuthoritativeLookup(string role)
  {
    var validator = new MutableAccessValidator();
    var guard = new HospitalityAdministrationSessionGuard(new TestAuthentication(User(role)), new TestCompany(), validator);
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => guard.RequireCompanyRfcAsync());
    Assert.Equal(0, validator.Calls);
  }

  [Theory]
  [InlineData("duplicate")]
  [InlineData("foreign")]
  [InlineData("anonymous")]
  [InlineData("missing_actor")]
  public async Task GuardRejectsInvalidSessionBeforeAuthoritativeLookup(string kind)
  {
    var user = User("SatOperator");
    var identity = (ClaimsIdentity)user.Identity!;
    if (kind == "duplicate") identity.AddClaim(new Claim(CompanyClaimTypes.Rfc, Rfc));
    if (kind == "foreign")
    {
      identity.RemoveClaim(identity.FindFirst(CompanyClaimTypes.Rfc)!);
      identity.AddClaim(new Claim(CompanyClaimTypes.Rfc, "OTHER010101AAA"));
    }
    if (kind == "anonymous") user = new ClaimsPrincipal(new ClaimsIdentity());
    if (kind == "missing_actor") identity.RemoveClaim(identity.FindFirst(ClaimTypes.NameIdentifier)!);
    var validator = new MutableAccessValidator();
    var guard = new HospitalityAdministrationSessionGuard(new TestAuthentication(user), new TestCompany(), validator);
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => guard.RequireCompanyRfcAsync());
    Assert.Equal(0, validator.Calls);
  }

  [Theory]
  [InlineData("Administrador", true)]
  [InlineData("Administrador", false)]
  [InlineData("SatOperator", true)]
  [InlineData("SatOperator", false)]
  public async Task ValidatorAcceptsAuthorizedGlobalOrSameCompanyRole(string role, bool global)
  {
    await using var db = CreateContext();
    await SeedAsync(db, role, global);
    await new HospitalityAdministrationAccessValidator(db, TimeProvider.System).EnsureAuthorizedAsync("actor", Rfc);
  }

  [Theory]
  [InlineData("membership")]
  [InlineData("role")]
  [InlineData("company")]
  [InlineData("lockout")]
  public async Task ValidatorRejectsRevocationWithoutRefreshingClaims(string revokedFact)
  {
    await using var db = CreateContext();
    await SeedAsync(db, "SatOperator", false);
    var validator = new HospitalityAdministrationAccessValidator(db, TimeProvider.System);
    await validator.EnsureAuthorizedAsync("actor", Rfc);
    switch (revokedFact)
    {
      case "membership": (await db.UserCompanies.SingleAsync()).IsActive = false; break;
      case "role": db.UserCompanyRoles.Remove(await db.UserCompanyRoles.SingleAsync()); break;
      case "company": (await db.Companies.SingleAsync()).IsActive = false; break;
      case "lockout":
        var user = await db.Users.SingleAsync();
        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.UtcNow.AddHours(1);
        break;
    }
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => validator.EnsureAuthorizedAsync("actor", Rfc));
  }

  [Fact]
  public async Task ValidatorRejectsRoleAssignedOnlyInAnotherCompany()
  {
    await using var db = CreateContext();
    await SeedAsync(db, "SatOperator", false);
    var role = await db.UserCompanyRoles.SingleAsync();
    db.UserCompanyRoles.Remove(role);
    await db.SaveChangesAsync();
    db.UserCompanyRoles.Add(new UserCompanyRole { UserId = "actor", Rfc = "OTHER010101AAA", RoleId = "role" });
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
      new HospitalityAdministrationAccessValidator(db, TimeProvider.System).EnsureAuthorizedAsync("actor", Rfc));
  }

  private static OrionIdentityDbContext CreateContext() => new(new DbContextOptionsBuilder<OrionIdentityDbContext>()
    .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

  private static async Task SeedAsync(OrionIdentityDbContext db, string roleName, bool global)
  {
    var user = new ApplicationUser { Id = "actor", UserName = "test@example.test" };
    var company = new OrionCompany { Rfc = Rfc, DisplayName = "Test company", IsActive = true };
    db.Users.Add(user);
    db.Companies.Add(company);
    db.UserCompanies.Add(new UserCompany { UserId = user.Id, Rfc = Rfc, User = user, Company = company, IsActive = true });
    var role = new IdentityRole { Id = "role", Name = roleName, NormalizedName = roleName.ToUpperInvariant() };
    db.Roles.Add(role);
    db.Entry(role).Property<string>("Scope").CurrentValue = global ? IdentityRoleScopes.Global : IdentityRoleScopes.Company;
    if (global) db.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = role.Id });
    else db.UserCompanyRoles.Add(new UserCompanyRole { UserId = user.Id, Rfc = Rfc, RoleId = role.Id });
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
  }

  private static ClaimsPrincipal User(string role) => new(new ClaimsIdentity([
    new Claim(ClaimTypes.NameIdentifier, "actor"), new Claim(ClaimTypes.Role, role),
    new Claim(CompanyClaimTypes.Rfc, Rfc)], "Test"));

  private sealed class TestAuthentication(ClaimsPrincipal user) : AuthenticationStateProvider
  {
    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
  }

  private sealed class TestCompany : ICurrentCompanyContext
  {
    public string? CurrentRfc => Rfc;
    public string? DisplayName => "Test";
    public int? EmployeeId => null;
    public string RequireRfc() => Rfc;
    public void EnsureRfc(string rfc)
    {
      if (!string.Equals(rfc, Rfc, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException();
    }
  }

  private sealed class MutableAccessValidator : IHospitalityAdministrationAccessValidator
  {
    public bool Revoked { get; set; }
    public int Calls { get; private set; }
    public Task EnsureAuthorizedAsync(string actorUserId, string companyRfc, CancellationToken ct = default)
    {
      Assert.Equal("actor", actorUserId);
      Assert.Equal(Rfc, companyRfc);
      Calls++;
      if (Revoked) throw new UnauthorizedAccessException();
      return Task.CompletedTask;
    }
  }
}
