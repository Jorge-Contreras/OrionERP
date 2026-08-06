using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Web.Identity
{
    public static class IdentitySeeder
    {
        public static async Task RunAsync(IServiceProvider sp)
        {
            var roleMgr = sp.GetRequiredService<RoleManager<IdentityRole>>();
            var userMgr = sp.GetRequiredService<UserManager<ApplicationUser>>();
            string[] roles =
            [
                "Administrador",
                "Operador",
                "Lectura",
                "Arrendadores",
                "OrdenTrabajoAdmin",
                "OrdenTrabajoSupervisor",
                "OrdenTrabajoOperador",
                "APAdmin",
                "APOperator",
                "APReadOnly"
                ,"Logistica"
                ,"RestauranteAdmin"
                ,"RestauranteSupervisor"
                ,"RestauranteCaja"
                ,"RestauranteCocina"
                ,"RestaurantePantalla"
                ,"CapitalHumanoAdmin"
                ,"CapitalHumanoSupervisor"
                ,"CapitalHumanoNomina"
            ];

            foreach (var r in roles)
            {
                if (!await roleMgr.RoleExistsAsync(r))
                {
                    _ = await roleMgr.CreateAsync(new IdentityRole(r));
                }
            }

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
            else
            {
                var environment = sp.GetService<IHostEnvironment>();
                if (environment?.IsDevelopment() == true && !await userMgr.CheckPasswordAsync(admin, adminPass))
                {
                    var resetToken = await userMgr.GeneratePasswordResetTokenAsync(admin);
                    _ = await userMgr.ResetPasswordAsync(admin, resetToken, adminPass);
                }
            }

            var hostEnvironment = sp.GetService<IHostEnvironment>();
            if (hostEnvironment?.IsDevelopment() == true)
            {
                await EnsureDevelopmentTestUserAsync(userMgr, "supervisor-test@orionerp.local", adminPass, "CapitalHumanoSupervisor");
                await EnsureDevelopmentTestUserAsync(userMgr, "nomina-test@orionerp.local", adminPass, "CapitalHumanoNomina");
            }
        }

        private static async Task EnsureDevelopmentTestUserAsync(
            UserManager<ApplicationUser> userManager,
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
            if (!claims.Any(claim => claim.Type == "rfc" && claim.Value == "OHM191112Q26"))
                await userManager.AddClaimAsync(user, new System.Security.Claims.Claim("rfc", "OHM191112Q26"));
        }
    }
}
