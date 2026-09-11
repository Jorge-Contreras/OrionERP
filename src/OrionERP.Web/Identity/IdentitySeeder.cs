using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Web.Identity
{
    public static class IdentitySeeder
    {
        private const string DevelopmentCompanyRfc = "TST260910DUAL01";

        public static async Task RunAsync(IServiceProvider sp)
        {
            var hostEnvironment = sp.GetRequiredService<IHostEnvironment>();
            var roleMgr = sp.GetRequiredService<RoleManager<IdentityRole>>();
            var userMgr = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var db = sp.GetRequiredService<OrionIdentityDbContext>();
            string[] roles =
            [
                "Administrador",
                "Operador",
                "Lectura",
                "SatOperator",
                "Conteo",
                "Arrendadores",
                "OrdenTrabajoAdmin",
                "OrdenTrabajoSupervisor",
                "OrdenTrabajoOperador",
                "APAdmin",
                "APOperator",
                "APReadOnly"
                ,"FinanzasLectura"
                ,"FinanzasManager"
                ,"Logistica"
                ,"RestauranteAdmin"
                ,"RestauranteSupervisor"
                ,"RestauranteCaja"
                ,"RestauranteCocina"
                ,"RestaurantePantalla"
                ,"CapitalHumanoAdmin"
                ,"CapitalHumanoSupervisor"
                ,"CapitalHumanoNomina"
                ,"CapacitacionAdmin"
                ,"CapacitacionInstructor"
                ,"CapacitacionAuditor"
            ];

            foreach (var r in roles)
            {
                if (!await roleMgr.RoleExistsAsync(r))
                {
                    _ = await roleMgr.CreateAsync(new IdentityRole(r));
                }

                var role = await roleMgr.FindByNameAsync(r);
                if (role is not null)
                {
                    db.Entry(role).Property<string>("Scope").CurrentValue = IdentityRoleScopes.IsGlobalRole(r)
                        ? IdentityRoleScopes.Global
                        : IdentityRoleScopes.Company;
                }
            }
            await db.SaveChangesAsync();

            if (hostEnvironment.IsDevelopment())
            {
                const string adminEmail = "admin@orionerp.local";
                const string adminPass = "Orion2021";

                var admin = await userMgr.Users.SingleOrDefaultAsync(u => u.Email == adminEmail);
                if (admin is null)
                {
                    admin = new ApplicationUser { UserName = adminEmail, Email = adminEmail };
                    var created = await userMgr.CreateAsync(admin, adminPass);
                    if (created.Succeeded)
                    {
                        await userMgr.AddToRoleAsync(admin, "Administrador");
                        await userMgr.AddClaimAsync(
                            admin,
                            new System.Security.Claims.Claim("rfc", "XAXX010101000"));
                    }
                }
                else if (!await userMgr.CheckPasswordAsync(admin, adminPass))
                {
                    var resetToken = await userMgr.GeneratePasswordResetTokenAsync(admin);
                    _ = await userMgr.ResetPasswordAsync(admin, resetToken, adminPass);
                }

                await EnsureCompanyAsync(db, DevelopmentCompanyRfc, "Empresa dual de prueba", "Empresa sintética de validación");
                await EnsureMembershipAsync(db, admin.Id, DevelopmentCompanyRfc);

                await EnsureDevelopmentTestUserAsync(userMgr, roleMgr, db, "supervisor-test@orionerp.local", adminPass, "CapitalHumanoSupervisor");
                await EnsureDevelopmentTestUserAsync(userMgr, roleMgr, db, "nomina-test@orionerp.local", adminPass, "CapitalHumanoNomina");

            }
        }

        private static async Task EnsureDevelopmentTestUserAsync(
            UserManager<ApplicationUser> userManager,
            RoleManager<IdentityRole> roleManager,
            OrionIdentityDbContext db,
            string email,
            string password,
            string role)
        {
            var user = await userManager.FindByEmailAsync(email);
            if (user is null)
            {
                user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
                var created = await userManager.CreateAsync(user, password);
                if (!created.Succeeded) return;
            }

            if (!await userManager.IsInRoleAsync(user, role)) await userManager.AddToRoleAsync(user, role);
            var claims = await userManager.GetClaimsAsync(user);
            if (!claims.Any(claim => claim.Type == "rfc" && claim.Value == DevelopmentCompanyRfc))
                await userManager.AddClaimAsync(user, new System.Security.Claims.Claim("rfc", DevelopmentCompanyRfc));

            await EnsureMembershipAsync(db, user.Id, DevelopmentCompanyRfc);
            var companyRole = await roleManager.FindByNameAsync(role);
            if (companyRole is not null && !await db.UserCompanyRoles.AnyAsync(link => link.UserId == user.Id && link.Rfc == DevelopmentCompanyRfc && link.RoleId == companyRole.Id))
            {
                db.UserCompanyRoles.Add(new UserCompanyRole { UserId = user.Id, Rfc = DevelopmentCompanyRfc, RoleId = companyRole.Id });
                await db.SaveChangesAsync();
            }
        }

        private static async Task EnsureCompanyAsync(OrionIdentityDbContext db, string rfc, string displayName, string legalName)
        {
            if (await db.Companies.AnyAsync(company => company.Rfc == rfc)) return;
            db.Companies.Add(new OrionCompany { Rfc = rfc, DisplayName = displayName, LegalName = legalName, IsActive = true, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow, UpdatedBy = "IdentitySeeder" });
            await db.SaveChangesAsync();
        }

        private static async Task EnsureMembershipAsync(OrionIdentityDbContext db, string userId, string rfc)
        {
            if (await db.UserCompanies.AnyAsync(link => link.UserId == userId && link.Rfc == rfc)) return;
            db.UserCompanies.Add(new UserCompany { UserId = userId, Rfc = rfc, IsActive = true, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow, UpdatedBy = "IdentitySeeder" });
            await db.SaveChangesAsync();
        }

    }
}
