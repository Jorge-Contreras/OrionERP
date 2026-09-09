using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OrionERP.Infrastructure.Auth;
using OrionERP.Infrastructure.Features.Platform;

namespace OrionERP.UnitTests.Platform;

public sealed class PlatformAdministrationAccessValidatorTests
{
  private static readonly DateTimeOffset Now =
    new(2026, 9, 2, 14, 0, 0, TimeSpan.Zero);

  [Fact]
  public async Task EnsureAuthorized_AllowsActiveMemberWithGlobalAdministratorRole()
  {
    var options = CreateOptions();
    await using var db = new OrionIdentityDbContext(options);
    await SeedAuthorizedAdministratorAsync(db);
    var validator = CreateValidator(options);

    await validator.EnsureAuthorizedAsync("user-17", "ohm191112q26");
  }

  [Fact]
  public async Task EnsureAuthorized_RejectsRevokedMembership()
  {
    var options = CreateOptions();
    await using var db = new OrionIdentityDbContext(options);
    await SeedAuthorizedAdministratorAsync(db);
    var membership = await db.UserCompanies.SingleAsync();
    membership.IsActive = false;
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
    var validator = CreateValidator(options);

    await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
      validator.EnsureAuthorizedAsync("user-17", "OHM191112Q26"));
  }

  [Fact]
  public async Task EnsureAuthorized_RejectsRevokedAdministratorRole()
  {
    var options = CreateOptions();
    await using var db = new OrionIdentityDbContext(options);
    await SeedAuthorizedAdministratorAsync(db);
    db.UserRoles.Remove(await db.UserRoles.SingleAsync());
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
    var validator = CreateValidator(options);

    await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
      validator.EnsureAuthorizedAsync("user-17", "OHM191112Q26"));
  }

  [Fact]
  public async Task EnsureAuthorized_RejectsCurrentlyLockedUser()
  {
    var options = CreateOptions();
    await using var db = new OrionIdentityDbContext(options);
    await SeedAuthorizedAdministratorAsync(db);
    var user = await db.Users.SingleAsync();
    user.LockoutEnabled = true;
    user.LockoutEnd = Now.AddMinutes(15);
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
    var validator = CreateValidator(options);

    await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
      validator.EnsureAuthorizedAsync("user-17", "OHM191112Q26"));
  }

  /// <summary>
  /// El porton corre en cada operacion y una pagina puede lanzar varias a la vez.
  /// Un DbContext prestado por el circuito reventaba ahi con "A second operation
  /// was started on this context instance", y quedaba inservible al cerrarse.
  /// </summary>
  [Fact]
  public async Task EnsureAuthorized_RevalidatesConcurrentOperationsWithItsOwnContext()
  {
    var options = CreateOptions();
    await using (var seed = new OrionIdentityDbContext(options))
      await SeedAuthorizedAdministratorAsync(seed);

    var validator = CreateValidator(options);
    await Task.WhenAll(Enumerable.Range(0, 8)
      .Select(_ => validator.EnsureAuthorizedAsync("user-17", "OHM191112Q26")));
  }

  private static DbContextOptions<OrionIdentityDbContext> CreateOptions()
    => new DbContextOptionsBuilder<OrionIdentityDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;

  private static PlatformAdministrationAccessValidator CreateValidator(
    DbContextOptions<OrionIdentityDbContext> options)
    => new(options, new FixedTimeProvider());

  private static async Task SeedAuthorizedAdministratorAsync(
    OrionIdentityDbContext db)
  {
    var user = new ApplicationUser
    {
      Id = "user-17",
      UserName = "admin@orionerp.local",
      NormalizedUserName = "ADMIN@ORIONERP.LOCAL",
      LockoutEnabled = true
    };
    var company = new OrionCompany
    {
      Rfc = "OHM191112Q26",
      DisplayName = "Orion",
      IsActive = true
    };
    var membership = new UserCompany
    {
      UserId = user.Id,
      Rfc = company.Rfc,
      User = user,
      Company = company,
      IsActive = true
    };
    var administratorRole = new IdentityRole
    {
      Id = "role-admin",
      Name = "Administrador",
      NormalizedName = "ADMINISTRADOR"
    };

    db.Users.Add(user);
    db.Companies.Add(company);
    db.UserCompanies.Add(membership);
    db.Roles.Add(administratorRole);
    db.Entry(administratorRole).Property<string>("Scope").CurrentValue = IdentityRoleScopes.Global;
    db.UserRoles.Add(new IdentityUserRole<string>
    {
      UserId = user.Id,
      RoleId = administratorRole.Id
    });
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
  }

  private sealed class FixedTimeProvider : TimeProvider
  {
    public override DateTimeOffset GetUtcNow() => Now;
  }
}
