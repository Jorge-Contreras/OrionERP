using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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
                "OrdenTrabajoAdmin",
                "OrdenTrabajoSupervisor",
                "OrdenTrabajoOperador"
            ];

            foreach (var r in roles)
            {
                if (!await roleMgr.RoleExistsAsync(r))
                {
                    _ = await roleMgr.CreateAsync(new IdentityRole(r));
                }
            }

            const string adminEmail = "admin@orionerp.local";
            const string adminPass = "Admin!23456";

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
        }
    }
}
