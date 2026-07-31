using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using OfficeOpenXml;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Bonhomia.PublicBooking;
using OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts;
using OrionERP.Infrastructure.Auth;
using OrionERP.Infrastructure.Features.Cfdi.CargarXmlSat.Services;
using OrionERP.Infrastructure.Features.Mail;
using OrionERP.Infrastructure.Features.Reservaciones.CalendarSync;
using OrionERP.Infrastructure.Features.Reservaciones.ListaReservaciones.Pdf;
using OrionERP.Web.Configuration;
using OrionERP.Web.Features.Cfdi.DescargaMasiva;
using OrionERP.Web.Features.Reservaciones.OpenClaw;
using OrionERP.Web.Features.Restaurante;
using OrionERP.Web.Identity;
using OrionERP.Web.State;
using OrionERP.Web.Services;

// using Microsoft.AspNetCore.Identity.UI; // <- not required unless you explicitly call AddDefaultUI()

var builder = WebApplication.CreateBuilder(args);
ExcelPackage.License.SetNonCommercialOrganization("Orion Habitat de Mexico S.A. de C.V.");

var appDataDirectory = builder.Environment.IsDevelopment()
  ? Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
      "Grupo Orion",
      "OrionERP",
      "DataProtectionKeys")
  : Path.Combine(AppContext.BaseDirectory, "App_Data", "keys");
Directory.CreateDirectory(appDataDirectory);

builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(appDataDirectory))
    .SetApplicationName("OrionERP");

builder.Services.AddAntiforgery(options =>
{
  // Cookies are scoped by host rather than port. A product-specific name keeps
  // other ASP.NET apps running on localhost from replacing the login token.
  options.Cookie.Name = ".OrionERP.Management.Antiforgery";
});

// --- CONFIG: JSON is source of truth; ignore arbitrary env vars -----------------
builder.Configuration.Sources.Clear();

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);

// Allow explicit machine-level overrides only via ASPNETCORE_* / DOTNET_*.
// Examples:
// - ASPNETCORE_ConnectionStrings__OrionDb
// - ASPNETCORE_OpenClawApi__ApiKey
// - ASPNETCORE_GraphMail__ClientSecret
builder.Configuration
    .AddEnvironmentVariables(prefix: "ASPNETCORE_")
    .AddEnvironmentVariables(prefix: "DOTNET_");

if (builder.Environment.IsDevelopment())
{
  // Load after prefixed env vars so local sandbox secrets can safely override
  // machine-scoped production settings on shared developer machines.
  builder.Configuration.AddUserSecrets<Program>(optional: true);
}

// -------------------------------------------------------------------------------

// Resolve and validate the connection string from the configured sources.
var conn = builder.Configuration.GetConnectionString("OrionDb");
var allowProductionDbInDevelopment = builder.Configuration.GetValue<bool>("AllowProductionDbInDevelopment");

if (!string.IsNullOrWhiteSpace(conn))
{
  try
  {
    var devConnectionBuilder = new SqlConnectionStringBuilder(conn);
    if (builder.Environment.IsDevelopment() &&
        string.Equals(devConnectionBuilder.InitialCatalog, "grupocarpio", StringComparison.OrdinalIgnoreCase) &&
        !allowProductionDbInDevelopment)
    {
      devConnectionBuilder.InitialCatalog = "Orion_Sandbox";
      conn = devConnectionBuilder.ConnectionString;
      Console.WriteLine("[BOOT] Development connection retargeted from 'grupocarpio' to 'Orion_Sandbox'.");
    }
  }
  catch
  {
    // Leave validation/error reporting to the existing connection-string checks below.
  }
}

if (!string.IsNullOrWhiteSpace(conn))
{
  // Keep IConfiguration aligned with the effective connection string so Dapper and EF use the same database.
  builder.Configuration["ConnectionStrings:OrionDb"] = conn;
}

string connSummary;
string? connDatabaseName = null;
if (string.IsNullOrWhiteSpace(conn))
{
  connSummary = "<missing>";
}
else
{
  try
  {
    var builderSummary = new SqlConnectionStringBuilder(conn);
    connDatabaseName = builderSummary.InitialCatalog;
    var authSummary = string.IsNullOrWhiteSpace(builderSummary.UserID)
        ? "IntegratedSecurity"
        : $"SqlAuth:{builderSummary.UserID}";
    connSummary = $"Server={builderSummary.DataSource};Database={builderSummary.InitialCatalog};Auth={authSummary}";
  }
  catch
  {
    connSummary = "<present>";
  }
}

Console.WriteLine($"[BOOT] ENV={builder.Environment.EnvironmentName}  OrionDb={connSummary}");
if (string.IsNullOrWhiteSpace(conn))
  throw new InvalidOperationException(
      "Missing/empty ConnectionStrings:OrionDb. In Development, set it with User Secrets, " +
      "a local appsettings.Development.json, or ConnectionStrings__OrionDb. In Production, " +
      "use ASPNETCORE_ConnectionStrings__OrionDb.");

var userSessionTimeout = TimeSpan.FromHours(8);
var disconnectedCircuitRetentionPeriod = TimeSpan.FromHours(2);

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
builder.Services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, BrunoAdminClaimsPrincipalFactory>();

builder.Services.ConfigureApplicationCookie(options =>
{
  options.ExpireTimeSpan = userSessionTimeout;
  options.SlidingExpiration = true;

  options.Events ??= new CookieAuthenticationEvents();

  options.Events.OnRedirectToLogin = context =>
  {
    if (IsApiOrBlazorCircuitRequest(context.Request))
    {
      context.Response.StatusCode = StatusCodes.Status401Unauthorized;
      return Task.CompletedTask;
    }

    context.Response.Redirect(context.RedirectUri);
    return Task.CompletedTask;
  };

  options.Events.OnRedirectToAccessDenied = context =>
  {
    if (IsApiOrBlazorCircuitRequest(context.Request))
    {
      context.Response.StatusCode = StatusCodes.Status403Forbidden;
      return Task.CompletedTask;
    }

    context.Response.Redirect(context.RedirectUri);
    return Task.CompletedTask;
  };
});

static bool IsApiOrBlazorCircuitRequest(HttpRequest request)
{
  if (request.Path.StartsWithSegments("/_blazor"))
  {
    return true;
  }

  if (string.Equals(request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase))
  {
    return true;
  }

  var acceptHeaders = request.GetTypedHeaders().Accept;
  if (acceptHeaders is not null)
  {
    foreach (var mediaType in acceptHeaders)
    {
      if (mediaType.MediaType.HasValue &&
          mediaType.MediaType.Value.Equals("application/json", StringComparison.OrdinalIgnoreCase))
      {
        return true;
      }
    }
  }

  return false;
}

builder.Services.AddScoped<IUserRfcState, UserRfcState>();
builder.Services.AddScoped<ICurrentRfcAccessor, UserRfcStateAccessor>();
builder.Services.AddScoped<ProtectedLocalStorage>();
builder.Services.AddScoped<ProtectedSessionStorage>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<RestaurantRealtimeClient>();
builder.Services.AddScoped<IRfcContext, RfcContext>();
builder.Services.AddScoped<IAuthorizationHandler, RoleForRfcHandler>();
builder.Services.AddAuthorization(options =>
{
  options.AddPolicy(
      "RoleForSelectedRfc",
      policy => policy.Requirements.Add(new RoleForRfcRequirement("Administrador")));
  options.AddPolicy(
      "RestaurantAdmin",
      policy => policy.Requirements.Add(new RoleForRfcRequirement("RestauranteAdmin", "RestauranteSupervisor")));
  options.AddPolicy(
      "RestaurantAdminOnly",
      policy => policy.Requirements.Add(new RoleForRfcRequirement("RestauranteAdmin")));
  options.AddPolicy(
      "RestaurantPos",
      policy => policy.Requirements.Add(new RoleForRfcRequirement("RestauranteCaja", "RestauranteSupervisor", "RestauranteAdmin")));
  options.AddPolicy(
      "RestaurantKitchen",
      policy => policy.Requirements.Add(new RoleForRfcRequirement("RestauranteCocina", "RestauranteSupervisor", "RestauranteAdmin")));
  options.AddPolicy(
      "RestaurantDisplay",
      policy => policy.Requirements.Add(new RoleForRfcRequirement("RestaurantePantalla", "RestauranteSupervisor", "RestauranteAdmin")));
  options.AddPolicy(
      "RestaurantCash",
      policy => policy.Requirements.Add(new RoleForRfcRequirement("RestauranteCaja", "RestauranteSupervisor", "RestauranteAdmin")));
  // QZ calls these endpoints through a regular browser fetch, outside the
  // Blazor circuit that owns the selected-RFC state. Keep the bridge limited
  // to restaurant cash roles without relying on circuit-scoped state.
  options.AddPolicy(
      "RestaurantQzBridge",
      policy => policy.RequireRole("Administrador", "RestauranteCaja", "RestauranteSupervisor", "RestauranteAdmin"));
});

builder.Services.AddRazorPages();      // Identity UI depends on Razor Pages
builder.Services.AddServerSideBlazor(options =>
{
  options.DisconnectedCircuitRetentionPeriod = disconnectedCircuitRetentionPeriod;
  // BrowserFileStream comparte el tiempo límite de JS interop. Las fotografías se
  // redimensionan en el navegador y pueden requerir más que el valor predeterminado
  // en conexiones lentas, especialmente durante depuración.
  options.JSInteropDefaultCallTimeout = TimeSpan.FromMinutes(2);
});
builder.Services.Configure<OpenClawApiOptions>(builder.Configuration.GetSection(OpenClawApiOptions.SectionName));
builder.Services.Configure<GraphMailOptions>(builder.Configuration.GetSection(GraphMailOptions.SectionName));
builder.Services.Configure<BonhomiaGraphMailOptions>(builder.Configuration.GetSection(BonhomiaGraphMailOptions.SectionName));
builder.Services.Configure<BonhomiaGraphCalendarSyncOptions>(builder.Configuration.GetSection(BonhomiaGraphCalendarSyncOptions.SectionName));
builder.Services.PostConfigure<BonhomiaGraphCalendarSyncOptions>(options =>
{
  var sharedCredentials = builder.Configuration
    .GetSection(BonhomiaGraphMailOptions.SectionName)
    .Get<BonhomiaGraphMailOptions>();

  if (sharedCredentials is not null)
  {
    options.ApplySharedGraphCredentials(
      sharedCredentials.TenantId,
      sharedCredentials.ClientId,
      sharedCredentials.ClientSecret);
  }
});
builder.Services.Configure<BonhomiaCheckoutOptions>(builder.Configuration.GetSection(BonhomiaCheckoutOptions.SectionName));
builder.Services.Configure<RestaurantQzTraySigningOptions>(
  builder.Configuration.GetSection(RestaurantQzTraySigningOptions.SectionName));
builder.Services.Configure<ReservacionPdfOptions>(options =>
{
  var webRootPath = builder.Environment.WebRootPath ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
  options.LogoPath = Path.Combine(webRootPath, "Images", "BonhomiaSuitesLetterheadLogo.svg");
});

builder.Services.AddCfdiCargarXmlSat();
builder.Services.AddOrionServices();
builder.Services.AddScoped<IUiMessageService, UiMessageService>();
builder.Services.AddHostedService<RestaurantEventBroadcaster>();

builder.Host.UseWindowsService();

// Only force a specific URL when actually running as a Windows Service
if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
{
  builder.WebHost.UseUrls("http://localhost:5000");
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
  // Debug endpoint to inspect configuration sources/values during local development only.
  app.MapGet("/__config", (IConfiguration cfg, IHostEnvironment env) =>
  {
    var root = (IConfigurationRoot)cfg;
    var effectiveConn = cfg.GetConnectionString("OrionDb");
    string effectiveConnSummary;

    try
    {
      var builderSummary = new SqlConnectionStringBuilder(effectiveConn);
      var authSummary = string.IsNullOrWhiteSpace(builderSummary.UserID)
        ? "IntegratedSecurity"
        : $"SqlAuth:{builderSummary.UserID}";
      effectiveConnSummary = $"EffectiveOrionDb=Server={builderSummary.DataSource};Database={builderSummary.InitialCatalog};Auth={authSummary}";
    }
    catch
    {
      effectiveConnSummary = $"EffectiveOrionDb={(string.IsNullOrWhiteSpace(effectiveConn) ? "<missing>" : "<present>")}";
    }

    return Results.Text(
        $"ENV={env.EnvironmentName}\n" +
        $"{effectiveConnSummary}\n\n" +
        root.GetDebugView());
  });
}

if (!app.Environment.IsDevelopment())
{
  app.UseExceptionHandler("/Error");
  app.UseHsts();
}

app.UseStaticFiles();

app.UseRouting();

app.UseMiddleware<LoginAntiforgeryRecoveryMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapBlazorHub();
app.MapHub<RestaurantEventsHub>("/hubs/restaurante");
app.MapRestaurantQzTraySigningApi();
app.MapOpenClawReservationsApi();
app.MapGet("/bonhomia", (IOptions<BonhomiaCheckoutOptions> options) =>
{
  var publicBaseUrl = options.Value.PublicBaseUrl?.Trim();
  if (!string.IsNullOrWhiteSpace(publicBaseUrl)
      && Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var baseUri))
  {
    return Results.Redirect(new Uri(baseUri, "/bonhomia").ToString());
  }

  return Results.Redirect("/");
});
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
