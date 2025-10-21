using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OfficeOpenXml;
using OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts;
using OrionERP.Infrastructure.Auth;
using OrionERP.Infrastructure.Features.Cfdi.CargarXmlSat.Services;
using OrionERP.Web.Configuration;
using OrionERP.Web.Data;
using OrionERP.Web.Features.Cfdi.DescargaMasiva;
using OrionERP.Web.Identity;
// using Microsoft.AspNetCore.Identity.UI; // <- not required unless you explicitly call AddDefaultUI()

var builder = WebApplication.CreateBuilder(args);
ExcelPackage.License.SetNonCommercialOrganization("Orion Habitat de Mexico S.A. de C.V.");

// Use ONE connection string name consistently.
// Option A: keep "OrionDb". Make sure it exists in appsettings.json.
// Option B: switch to "DefaultConnection".
var connectionString = builder.Configuration.GetConnectionString("OrionDb")
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Server=bonhomia.ddns.net,1433;Database=grupocarpio;User Id=orion;Password=Orion82;TrustServerCertificate=True;MultipleActiveResultSets=True;";

builder.Services.AddDbContext<OrionIdentityDbContext>(opt =>
    opt.UseSqlServer(connectionString,
        sql => sql.MigrationsAssembly("OrionERP.Infrastructure"))); // migrations live in Infrastructure

// Identity with cookie auth + default UI pages (AddDefaultIdentity includes UI)
builder.Services
    .AddDefaultIdentity<ApplicationUser>(o =>
    {
      o.SignIn.RequireConfirmedAccount = false;
      o.Password.RequiredLength = 8;
      o.Password.RequireDigit = true;
      o.Password.RequireLowercase = true;
      o.Password.RequireUppercase = false;
      o.Password.RequireNonAlphanumeric = false;
      o.Lockout.MaxFailedAccessAttempts = 5;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<OrionIdentityDbContext>()
    .AddDefaultTokenProviders(); // keep tokens (reset, etc.)

builder.Services.AddScoped<IRfcContext, RfcContext>();
builder.Services.AddScoped<IAuthorizationHandler, RoleForRfcHandler>();
builder.Services.AddAuthorization(options =>
{
  options.AddPolicy(
      "RoleForSelectedRfc",
      policy => policy.Requirements.Add(new RoleForRfcRequirement("Administrador"))); // NOTE: ensure this role exists (see note below)
});

// builder.Services.AddScoped<AuthenticationStateProvider, RevalidatingIdentityAuthenticationStateProvider<ApplicationUser>>();

builder.Services.AddRazorPages();      // Identity UI depends on Razor Pages
builder.Services.AddServerSideBlazor();

builder.Services.AddSingleton<WeatherForecastService>();
builder.Services.AddCfdiCargarXmlSat();
builder.Services.AddOrionServices();
builder.Services.Configure<SatIntegrationOptions>(builder.Configuration.GetSection("SatIntegration"));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
  app.UseExceptionHandler("/Error");
  app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();   // <- keep this pair once, before mapping endpoints
app.UseAuthorization();

app.MapRazorPages();       // <- required for /Identity/Account/* pages
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");
// REMOVE the second pair (you had duplicates):
// app.UseAuthentication();
// app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
  await IdentitySeeder.RunAsync(scope.ServiceProvider); // your existing static seeder
                                                        // await CapitalHumanoImporter.ImportAsync(scope.ServiceProvider);
}

app.Run();
