using System;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Web.Identity
{
    public static class CapitalHumanoImporter
    {
        public static async Task ImportAsync(IServiceProvider sp)
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var userMgr = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var roleMgr = sp.GetRequiredService<RoleManager<IdentityRole>>();
            var db = sp.GetRequiredService<OrionIdentityDbContext>();

            using var conn = new SqlConnection(cfg.GetConnectionString("OrionDb"));
            var empleados = await conn.QueryAsync<(int ID, string Rfc, string? Email)>(
                @"SELECT TOP (10000) ID,
                       UPPER(LTRIM(RTRIM(RFC))) AS Rfc,
                       NULLIF(LTRIM(RTRIM(CorreoElectronico)), '') AS Email
                  FROM dbo.Capital_Humano WITH (NOLOCK)
                  WHERE NULLIF(LTRIM(RTRIM(RFC)), '') IS NOT NULL
                  ORDER BY ID");

            foreach (var e in empleados)
            {
                var email = e.Email ?? $"emp{e.ID}@noemail.local";
                var existing = await userMgr.FindByEmailAsync(email);
                var u = existing ?? new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmployeeId = e.ID
                };
                if (existing is null)
                {
                    var tempPwd = $"Emp!{e.ID}_" + Guid.NewGuid().ToString("n")[..6];
                    var res = await userMgr.CreateAsync(u, tempPwd);
                    if (!res.Succeeded) continue;
                }

                var rfc = e.Rfc.Trim().ToUpperInvariant();
                var company = await db.Companies.FindAsync([rfc]);
                if (company is null)
                {
                    company = new OrionCompany { Rfc = rfc, DisplayName = rfc, IsActive = true, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow, UpdatedBy = "CapitalHumanoImporter" };
                    db.Companies.Add(company);
                    await db.SaveChangesAsync();
                }

                var membership = await db.UserCompanies.SingleOrDefaultAsync(link => link.UserId == u.Id && link.Rfc == rfc);
                if (membership is null)
                {
                    var employeeAvailable = !await db.UserCompanies.AnyAsync(link => link.EmployeeId == e.ID);
                    membership = new UserCompany
                    {
                        UserId = u.Id,
                        Rfc = rfc,
                        EmployeeId = employeeAvailable ? e.ID : null,
                        IsActive = true,
                        AccessReviewRequired = !employeeAvailable,
                        CreatedAtUtc = DateTime.UtcNow,
                        UpdatedAtUtc = DateTime.UtcNow,
                        UpdatedBy = "CapitalHumanoImporter"
                    };
                    db.UserCompanies.Add(membership);
                    await db.SaveChangesAsync();
                }

                var lectura = await roleMgr.FindByNameAsync("Lectura");
                if (lectura is not null && !await db.UserCompanyRoles.AnyAsync(link => link.UserId == u.Id && link.Rfc == rfc && link.RoleId == lectura.Id))
                {
                    db.UserCompanyRoles.Add(new UserCompanyRole { UserId = u.Id, Rfc = rfc, RoleId = lectura.Id });
                    await db.SaveChangesAsync();
                }
            }
        }
    }
}
