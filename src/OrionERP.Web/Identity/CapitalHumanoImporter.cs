using System;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Web.Identity
{
    public static class CapitalHumanoImporter
    {
        public static async Task ImportAsync(IServiceProvider sp)
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var userMgr = sp.GetRequiredService<UserManager<ApplicationUser>>();

            using var conn = new SqlConnection(cfg.GetConnectionString("OrionDb"));
            var empleados = await conn.QueryAsync<(int ID, string? Email)>(
                @"SELECT TOP (10000) ID,
                       NULLIF(LTRIM(RTRIM(CorreoElectronico)), '') AS Email
                  FROM dbo.Capital_Humano WITH (NOLOCK)
                  ORDER BY ID");

            foreach (var e in empleados)
            {
                var email = e.Email ?? $"emp{e.ID}@noemail.local";
                var existing = await userMgr.FindByEmailAsync(email);
                if (existing != null)
                {
                    continue;
                }

                var u = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmployeeId = e.ID
                };

                var tempPwd = $"Emp!{e.ID}_" + Guid.NewGuid().ToString("n")[..6];
                var res = await userMgr.CreateAsync(u, tempPwd);
                if (!res.Succeeded)
                {
                    continue;
                }

                await userMgr.AddToRoleAsync(u, "Lectura");
            }
        }
    }
}
