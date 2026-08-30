using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using OrionERP.Application.Features.Auth.Companies;
using OrionERP.Infrastructure.Auth;
using SkiaSharp;

namespace OrionERP.Web.Identity
{
    public static class IdentitySeeder
    {
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

                await EnsureCompanyAsync(db, "OHM191112Q26", "Orion", "Orion Habitat de México, S.A. de C.V.");
                await EnsureMembershipAsync(db, admin.Id, "OHM191112Q26");

                await EnsureDevelopmentTestUserAsync(userMgr, roleMgr, db, "supervisor-test@orionerp.local", adminPass, "CapitalHumanoSupervisor");
                await EnsureDevelopmentTestUserAsync(userMgr, roleMgr, db, "nomina-test@orionerp.local", adminPass, "CapitalHumanoNomina");

            }

            var seedActor = await (
                from link in db.Set<IdentityUserRole<string>>().AsNoTracking()
                join role in db.Roles.AsNoTracking() on link.RoleId equals role.Id
                where role.NormalizedName == "ADMINISTRADOR"
                select link.UserId).FirstOrDefaultAsync() ?? "IdentitySeeder";
            var webEnvironment = sp.GetRequiredService<IWebHostEnvironment>();
            var companyAccess = sp.GetRequiredService<ICompanyAccessService>();
            await SeedLogoIfMissingAsync(companyAccess, Path.Combine(webEnvironment.WebRootPath, "Images", "OrionERP_Logo.png"), "OHM191112Q26", seedActor);
            await SeedLogoIfMissingAsync(companyAccess, Path.Combine(webEnvironment.WebRootPath, "Images", "CompanySeed", "brunos-logo.png"), "BRUNOS260707L26", seedActor);
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
            if (!claims.Any(claim => claim.Type == "rfc" && claim.Value == "OHM191112Q26"))
                await userManager.AddClaimAsync(user, new System.Security.Claims.Claim("rfc", "OHM191112Q26"));

            await EnsureMembershipAsync(db, user.Id, "OHM191112Q26");
            var companyRole = await roleManager.FindByNameAsync(role);
            if (companyRole is not null && !await db.UserCompanyRoles.AnyAsync(link => link.UserId == user.Id && link.Rfc == "OHM191112Q26" && link.RoleId == companyRole.Id))
            {
                db.UserCompanyRoles.Add(new UserCompanyRole { UserId = user.Id, Rfc = "OHM191112Q26", RoleId = companyRole.Id });
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

        private static async Task SeedLogoIfMissingAsync(ICompanyAccessService companyAccess, string path, string rfc, string actorUserId)
        {
            var company = await companyAccess.GetCompanyAsync(rfc);
            if (company is null || company.HasLogo || !File.Exists(path)) return;
            var bytes = await File.ReadAllBytesAsync(path);
            var contentType = "image/png";

            // The original repository artwork is intentionally high resolution
            // and can exceed the administrator upload limit. Normalize trusted
            // seed artwork before passing it through the same validation/storage
            // path used by uploaded logos.
            if (bytes.Length > 2 * 1024 * 1024)
            {
                using var source = SKBitmap.Decode(bytes)
                    ?? throw new InvalidOperationException($"The seed logo for {rfc} could not be decoded.");
                var scale = Math.Min(1d, 1024d / Math.Max(source.Width, source.Height));
                var width = Math.Max(1, (int)Math.Round(source.Width * scale));
                var height = Math.Max(1, (int)Math.Round(source.Height * scale));
                using var resized = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
                using (var canvas = new SKCanvas(resized))
                {
                    canvas.Clear(SKColors.Transparent);
                    canvas.DrawBitmap(source, new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
                }

                using var image = SKImage.FromBitmap(resized);
                using var encoded = image.Encode(SKEncodedImageFormat.Webp, 88)
                    ?? throw new InvalidOperationException($"The seed logo for {rfc} could not be encoded.");
                bytes = encoded.ToArray();
                contentType = "image/webp";
            }

            var result = await companyAccess.SaveLogoAsync(rfc, bytes, contentType, actorUserId);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException($"The seed logo for {rfc} was rejected: {result.Message}");
            }
        }
    }
}
