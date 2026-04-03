using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OrionERP.Application.Features.Auth.AdminPortal;
using OrionERP.Infrastructure.Auth;
using OrionERP.Infrastructure.Features.Auth.AdminPortal;

namespace OrionERP.UnitTests.Auth;

public class IdentityAdminServiceTests
{
    [Fact]
    public async Task ResetUserPasswordAsync_UpdatesPasswordAndSecurityStamp()
    {
        using var provider = CreateServiceProvider();
        using var scope = provider.CreateScope();

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var service = scope.ServiceProvider.GetRequiredService<IIdentityAdminService>();

        var user = new ApplicationUser
        {
            UserName = "password-reset@orionerp.local",
            Email = "password-reset@orionerp.local"
        };

        await userManager.CreateAsync(user, "secret1");

        var originalSecurityStamp = user.SecurityStamp;

        var result = await service.ResetUserPasswordAsync(new IdentityAdminPasswordResetRequest(user.Id, "nuevo123"));

        Assert.True(result.Succeeded);

        var refreshedUser = await userManager.FindByIdAsync(user.Id);
        Assert.NotNull(refreshedUser);
        Assert.True(await userManager.CheckPasswordAsync(refreshedUser!, "nuevo123"));
        Assert.False(await userManager.CheckPasswordAsync(refreshedUser!, "secret1"));
        Assert.NotEqual(originalSecurityStamp, refreshedUser!.SecurityStamp);
    }

    [Fact]
    public async Task ResetUserPasswordAsync_ReturnsFailureWhenUserDoesNotExist()
    {
        using var provider = CreateServiceProvider();
        using var scope = provider.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService<IIdentityAdminService>();

        var result = await service.ResetUserPasswordAsync(new IdentityAdminPasswordResetRequest("missing-user", "nuevo123"));

        Assert.False(result.Succeeded);
        Assert.Contains("No se encontró el usuario", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteUserAsync_RejectsDeletingLastAdministrator()
    {
        using var provider = CreateServiceProvider();
        using var scope = provider.CreateScope();

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var service = scope.ServiceProvider.GetRequiredService<IIdentityAdminService>();

        await roleManager.CreateAsync(new IdentityRole("Administrador"));

        var admin = new ApplicationUser
        {
            UserName = "admin@orionerp.local",
            Email = "admin@orionerp.local"
        };

        await userManager.CreateAsync(admin, "secret1");
        await userManager.AddToRoleAsync(admin, "Administrador");

        var result = await service.DeleteUserAsync(admin.Id, "another-admin");

        Assert.False(result.Succeeded);
        Assert.Contains("último administrador", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveUserAsync_RejectsRemovingOwnAdministradorRole()
    {
        using var provider = CreateServiceProvider();
        using var scope = provider.CreateScope();

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var service = scope.ServiceProvider.GetRequiredService<IIdentityAdminService>();

        await roleManager.CreateAsync(new IdentityRole("Administrador"));
        await roleManager.CreateAsync(new IdentityRole("Operador"));

        var admin = new ApplicationUser
        {
            UserName = "self-admin@orionerp.local",
            Email = "self-admin@orionerp.local",
            LockoutEnabled = true
        };

        var secondAdmin = new ApplicationUser
        {
            UserName = "backup-admin@orionerp.local",
            Email = "backup-admin@orionerp.local",
            LockoutEnabled = true
        };

        await userManager.CreateAsync(admin, "secret1");
        await userManager.CreateAsync(secondAdmin, "secret1");
        await userManager.AddToRoleAsync(admin, "Administrador");
        await userManager.AddToRoleAsync(secondAdmin, "Administrador");

        var result = await service.SaveUserAsync(new IdentityUserUpsertRequest(
            admin.Id,
            admin.Id,
            admin.UserName!,
            admin.Email,
            null,
            null,
            false,
            false,
            false,
            true,
            null,
            null,
            ["Operador"],
            Array.Empty<IdentityClaimInput>()));

        Assert.False(result.Succeeded);
        Assert.Contains("quitarte a ti mismo", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetPortalSnapshotAsync_ReturnsAggregatedIdentityCounts()
    {
        using var provider = CreateServiceProvider();
        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<OrionIdentityDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var service = scope.ServiceProvider.GetRequiredService<IIdentityAdminService>();

        var role = new IdentityRole("Administrador");
        await roleManager.CreateAsync(role);

        var user = new ApplicationUser
        {
            UserName = "metrics@orionerp.local",
            Email = "metrics@orionerp.local",
            LockoutEnabled = true,
            LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(15)
        };

        await userManager.CreateAsync(user, "secret1");
        await userManager.AddToRoleAsync(user, "Administrador");
        await userManager.AddClaimAsync(user, new System.Security.Claims.Claim("rfc", "XAXX010101000"));
        await roleManager.AddClaimAsync(role, new System.Security.Claims.Claim("module", "security"));

        db.Set<IdentityUserLogin<string>>().Add(new IdentityUserLogin<string>
        {
            UserId = user.Id,
            LoginProvider = "Google",
            ProviderKey = "google-123",
            ProviderDisplayName = "Google"
        });

        db.Set<IdentityUserToken<string>>().Add(new IdentityUserToken<string>
        {
            UserId = user.Id,
            LoginProvider = "App",
            Name = "refresh",
            Value = "token-secret"
        });

        await db.SaveChangesAsync();

        var snapshot = await service.GetPortalSnapshotAsync();

        Assert.Equal(1, snapshot.Metrics.UserCount);
        Assert.Equal(1, snapshot.Metrics.RoleCount);
        Assert.Equal(1, snapshot.Metrics.RoleClaimCount);
        Assert.Equal(1, snapshot.Metrics.UserClaimCount);
        Assert.Equal(1, snapshot.Metrics.UserRoleCount);
        Assert.Equal(1, snapshot.Metrics.LoginCount);
        Assert.Equal(1, snapshot.Metrics.TokenCount);
        Assert.Equal(1, snapshot.Metrics.LockedUserCount);

        var userSummary = Assert.Single(snapshot.Users);
        Assert.Contains("Administrador", userSummary.Roles);
        Assert.Equal(1, userSummary.ClaimCount);
        Assert.Equal(1, userSummary.LoginCount);
        Assert.Equal(1, userSummary.TokenCount);
    }

    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddDataProtection();
        services.AddDbContext<OrionIdentityDbContext>(options =>
            options
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

        services
            .AddIdentityCore<ApplicationUser>(options =>
            {
                options.Password.RequiredLength = 6;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<OrionIdentityDbContext>()
            .AddDefaultTokenProviders();

        services.AddScoped<IIdentityAdminService, IdentityAdminService>();

        return services.BuildServiceProvider();
    }
}
