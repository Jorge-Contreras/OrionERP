using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using OfficeOpenXml;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts;
using OrionERP.Infrastructure.Auth;
using OrionERP.Infrastructure.Features.Cfdi.CargarXmlSat.Services;
using OrionERP.Web.Configuration;
using OrionERP.Web.Data;
using OrionERP.Web.Features.Cfdi.DescargaMasiva;
using OrionERP.Web.Identity;
using OrionERP.Web.State;
using OrionERP.Web.Services;

// using Microsoft.AspNetCore.Identity.UI; // <- not required unless you explicitly call AddDefaultUI()

var builder = WebApplication.CreateBuilder(args);
ExcelPackage.License.SetNonCommercialOrganization("Orion Habitat de Mexico S.A. de C.V.");

// --- CONFIG: JSON is source of truth; ignore arbitrary env vars -----------------
builder.Configuration.Sources.Clear();

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);

// (Optional) In Development you can enable User Secrets by uncommenting the next two lines
// if (builder.Environment.IsDevelopment())
//     builder.Configuration.AddUserSecrets<Program>(optional: true);

// Only allow platform env vars if needed; these WILL NOT include ConnectionStrings__*
// Remove these two lines if you want to block env vars entirely.
// (They’re safe to keep; they don’t affect your connection strings.)
builder.Configuration
    .AddEnvironmentVariables(prefix: "ASPNETCORE_")
    .AddEnvironmentVariables(prefix: "DOTNET_");

// -------------------------------------------------------------------------------

// Resolve and validate the connection string strictly from JSON
var conn = builder.Configuration.GetConnectionString("OrionDb");
Console.WriteLine($"[BOOT] ENV={builder.Environment.EnvironmentName}  OrionDb='{conn}'");
if (string.IsNullOrWhiteSpace(conn))
  throw new InvalidOperationException("Missing/empty ConnectionStrings:OrionDb");

builder.Services.AddDbContext<OrionIdentityDbContext>(opt =>
    opt.UseSqlServer(conn,
        sql => sql.MigrationsAssembly("OrionERP.Infrastructure"))); // migrations live in Infrastructure

// Identity with cookie auth + default token providers
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
    .AddDefaultTokenProviders();

builder.Services.AddScoped<IUserRfcState, UserRfcState>();
builder.Services.AddScoped<ICurrentRfcAccessor, UserRfcStateAccessor>();
builder.Services.AddScoped<ProtectedSessionStorage>();
builder.Services.AddScoped<IRfcContext, RfcContext>();
builder.Services.AddScoped<IAuthorizationHandler, RoleForRfcHandler>();
builder.Services.AddAuthorization(options =>
{
  options.AddPolicy(
      "RoleForSelectedRfc",
      policy => policy.Requirements.Add(new RoleForRfcRequirement("Administrador")));
});

builder.Services.AddRazorPages();      // Identity UI depends on Razor Pages
builder.Services.AddServerSideBlazor();

builder.Services.AddSingleton<WeatherForecastService>();
builder.Services.AddCfdiCargarXmlSat();
builder.Services.AddOrionServices();
builder.Services.AddScoped<IUiMessageService, UiMessageService>();
builder.Services.AddScoped<IBreadcrumbService, BreadcrumbService>();
builder.Services.AddScoped<ITransaccionDetailService, FakeTransaccionDetailService>();

builder.Host.UseWindowsService();

// Only force a specific URL when actually running as a Windows Service
if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
{
  builder.WebHost.UseUrls("http://localhost:5000");
}

var app = builder.Build();

// Debug endpoint to inspect configuration sources/values
app.MapGet("/__config", (IConfiguration cfg, IHostEnvironment env) =>
{
  var root = (IConfigurationRoot)cfg;
  return Results.Text(
      $"ENV={env.EnvironmentName}\n\n" +
      root.GetDebugView());
});

if (!app.Environment.IsDevelopment())
{
  app.UseExceptionHandler("/Error");
  app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.MapGet("/auth/logout", async (HttpContext ctx) =>
{
  await ctx.SignOutAsync(IdentityConstants.ApplicationScheme);
  ctx.Response.Redirect("/");
});

// Seed Identity
using (var scope = app.Services.CreateScope())
{
  await IdentitySeeder.RunAsync(scope.ServiceProvider);
}

app.Run();

// Needed only if you enable AddUserSecrets<Program> above (partial to link with implicit Program class)
public partial class Program { }
