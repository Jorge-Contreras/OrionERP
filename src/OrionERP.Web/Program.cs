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
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using OfficeOpenXml;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Bonhomia.PublicBooking;
using OrionERP.Application.Features.CapitalHumano.Workforce;
using OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts;
using OrionERP.Application.Features.Auth.Companies;
using OrionERP.Infrastructure.Auth;
using OrionERP.Infrastructure.Features.Auth;
using OrionERP.Infrastructure.Features.CapitalHumano.Workforce;
using OrionERP.Infrastructure.Features.Cfdi.CargarXmlSat.Services;
using OrionERP.Infrastructure.Features.Mail;
using OrionERP.Infrastructure.Features.Reservaciones.CalendarSync;
using OrionERP.Infrastructure.Features.Reservaciones.ListaReservaciones.Pdf;
using OrionERP.Web.Configuration;
using OrionERP.Web.Features.Cfdi.DescargaMasiva;
using OrionERP.Web.Features.Reservaciones.OpenClaw;
using OrionERP.Web.Features.Restaurante;
using OrionERP.Web.Features.TrainingSafety;
using OrionERP.Web.Identity;
using OrionERP.Web.State;
using OrionERP.Web.Services;
using System.Net;
using System.Threading.RateLimiting;

// using Microsoft.AspNetCore.Identity.UI; // <- not required unless you explicitly call AddDefaultUI()

var builder = WebApplication.CreateBuilder(args);
WorkforceDapperTypeHandlers.Register();
ExcelPackage.License.SetNonCommercialOrganization("Orion Habitat de Mexico S.A. de C.V.");

// A service-scoped marker makes environment precedence fail closed. If a
// machine-wide DOTNET_ENVIRONMENT ever overrides the service's intended
// ASPNETCORE_ENVIRONMENT, the Training process must stop before reading any
// production configuration or registering external integrations.
var isMarkedTrainingService = string.Equals(
  Environment.GetEnvironmentVariable(TrainingEnvironment.ServiceMarkerVariable),
  "1",
  StringComparison.Ordinal);
if (isMarkedTrainingService && !builder.Environment.IsEnvironment(TrainingEnvironment.Name))
{
  throw new InvalidOperationException(
    $"Training startup blocked: {TrainingEnvironment.ServiceMarkerVariable}=1 requires " +
    $"the host environment to be exactly '{TrainingEnvironment.Name}', but it resolved to " +
    $"'{builder.Environment.EnvironmentName}'.");
}

// --- CONFIG: JSON is source of truth; ignore arbitrary env vars -----------------
builder.Configuration.Sources.Clear();

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);

if (builder.Environment.IsEnvironment(TrainingEnvironment.Name))
{
  // Training intentionally ignores the normal ASPNETCORE_* and DOTNET_* value
  // providers after the host environment has been selected. This prevents a
  // machine-wide production connection string or integration secret from being
  // inherited by the disposable training service. Only explicitly training-
  // scoped overrides are accepted.
  builder.Configuration.AddEnvironmentVariables(prefix: TrainingEnvironment.ConfigurationPrefix);
}
else
{
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
}

// -------------------------------------------------------------------------------

var platformIsolation = PlatformIsolationOptions.FromConfiguration(builder.Configuration);

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

TrainingSafetyValidator.ValidateStartup(
  builder.Environment.EnvironmentName,
  conn,
  platformIsolation,
  builder.Configuration["Hosting:WindowsServiceUrl"],
  isMarkedTrainingService,
  builder.Configuration["AllowedHosts"],
  builder.Configuration["Capacitacion:SandboxBaseUrl"]);

var dataProtectionKeyDirectory = platformIsolation.ResolveDataProtectionKeyDirectory(AppContext.BaseDirectory);
Directory.CreateDirectory(dataProtectionKeyDirectory);

var dataProtectionBuilder = builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyDirectory))
    .SetApplicationName(platformIsolation.DataProtectionApplicationName);
if (OperatingSystem.IsWindows() && builder.Environment.IsEnvironment(TrainingEnvironment.Name))
{
  // The Training key ring lives outside the Dropbox-synced publish tree and
  // is encrypted for the dedicated Windows service identity at rest.
  dataProtectionBuilder.ProtectKeysWithDpapi();
}

builder.Services.AddAntiforgery(options =>
{
  // Cookies are scoped by host rather than port. The Training environment uses
  // its own configured name so it can never replace a production login token.
  options.Cookie.Name = platformIsolation.AntiforgeryCookieName;
  options.Cookie.HttpOnly = true;
  options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
    ? CookieSecurePolicy.SameAsRequest
    : CookieSecurePolicy.Always;
});

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

var trainingDatabaseSafety = TrainingDatabaseSafetyAttestation.NotApplicable;
if (builder.Environment.IsEnvironment(TrainingEnvironment.Name))
{
  trainingDatabaseSafety = await TrainingDatabaseSafetyVerifier.VerifyOrThrowAsync(conn);
  Console.WriteLine(
    $"[BOOT] Training database safety verified; schema v{trainingDatabaseSafety.SchemaVersion}, " +
    "sanitized synthetic data, isolated least-privilege login.");
}

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
builder.Services.AddScoped<ICompanySignInContext, CompanySignInContext>();
builder.Services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, CompanyClaimsPrincipalFactory>();
builder.Services.AddScoped<ICompanyAccessService, CompanyAccessService>();
builder.Services.AddScoped<OrionERP.Application.Features.Auth.AdminPortal.ICompanyMembershipAdminService, OrionERP.Infrastructure.Features.Auth.AdminPortal.CompanyMembershipAdminService>();
builder.Services.AddAuthentication()
  .AddCookie(CompanyAuthenticationSchemes.PendingCompanySelection, options =>
  {
    options.Cookie.Name = $"{platformIsolation.IdentityCookieName}.PendingCompany";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
      ? CookieSecurePolicy.SameAsRequest
      : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
    options.SlidingExpiration = false;
  });

builder.Services.ConfigureApplicationCookie(options =>
{
  // Rotated for the company-bound claims contract. Legacy cookies can contain
  // multiple RFC claims and must never enter the new application session.
  options.Cookie.Name = $"{platformIsolation.IdentityCookieName}.CompanyV1";
  options.Cookie.HttpOnly = true;
  options.Cookie.SameSite = SameSiteMode.Lax;
  options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
    ? CookieSecurePolicy.SameAsRequest
    : CookieSecurePolicy.Always;
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

  options.Events.OnValidatePrincipal = async context =>
  {
    var principal = context.Principal;
    if (principal?.Identity?.IsAuthenticated != true) return;

    var sessionVersion = principal.FindFirst(CompanyClaimTypes.SessionVersion)?.Value;
    var rfcs = principal.FindAll(CompanyClaimTypes.Rfc)
      .Select(claim => claim.Value.Trim().ToUpperInvariant())
      .Where(value => value.Length > 0)
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToArray();
    var userId = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (!string.Equals(sessionVersion, CompanyClaimTypes.CurrentSessionVersion, StringComparison.Ordinal)
        || rfcs.Length != 1
        || string.IsNullOrWhiteSpace(userId))
    {
      context.RejectPrincipal();
      await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
      return;
    }

    // Validate the stamp without asking SecurityStampValidator to rebuild the
    // principal. Its refresh path runs the claims factory outside the original
    // company-selection operation, which can lose the selected RFC and produce
    // a cookie that is immediately rejected on the first protected request.
    // Access mutations rotate the stamp, so a direct comparison still revokes
    // affected sessions immediately while preserving the company-bound claims.
    var signInManager = context.HttpContext.RequestServices
      .GetRequiredService<SignInManager<ApplicationUser>>();
    var stampUser = await signInManager.ValidateSecurityStampAsync(principal);
    if (stampUser is null || !string.Equals(stampUser.Id, userId, StringComparison.Ordinal))
    {
      context.RejectPrincipal();
      await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
      return;
    }

    var companyAccess = context.HttpContext.RequestServices.GetRequiredService<ICompanyAccessService>();
    if (!await companyAccess.HasActiveMembershipAsync(userId, rfcs[0], context.HttpContext.RequestAborted))
    {
      context.RejectPrincipal();
      await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
    }
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

builder.Services.AddScoped<UserRfcState>();
builder.Services.AddScoped<IUserRfcState>(services => services.GetRequiredService<UserRfcState>());
builder.Services.AddScoped<ICurrentCompanyContext>(services => services.GetRequiredService<UserRfcState>());
builder.Services.AddScoped<ICurrentRfcAccessor, UserRfcStateAccessor>();
builder.Services.AddScoped<ProtectedLocalStorage>();
builder.Services.AddScoped<ProtectedSessionStorage>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<RestaurantRealtimeClient>();
builder.Services.AddScoped<IRfcContext, RfcContext>();
builder.Services.AddScoped<IAuthorizationHandler, RoleForRfcHandler>();
builder.Services.AddRateLimiter(options =>
{
  options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
  options.AddFixedWindowLimiter("workforce-kiosk", limiter =>
  {
    limiter.PermitLimit = 20;
    limiter.Window = TimeSpan.FromMinutes(1);
    limiter.QueueLimit = 0;
  });
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
  options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
  options.ForwardLimit = 1;
  options.KnownProxies.Add(IPAddress.Loopback);
  options.KnownProxies.Add(IPAddress.IPv6Loopback);
});
builder.Services.Configure<WorkforceRetentionOptions>(builder.Configuration.GetSection("CapitalHumano"));
builder.Services.AddAuthorization(options =>
{
  static bool AttendanceIsEnabled(AuthorizationHandlerContext context, IConfiguration configuration)
    => configuration.GetValue<bool>("CapitalHumano:AttendanceEnabled");
  options.AddPolicy("CapitalHumanoEmployee", policy => policy
    .RequireAuthenticatedUser()
    .RequireClaim("employee_id")
    .RequireClaim("rfc")
    .RequireAssertion(context => AttendanceIsEnabled(context, builder.Configuration)));
  options.AddPolicy("CapitalHumanoAdmin", policy => policy
    .RequireRole("Administrador", "CapitalHumanoAdmin")
    .RequireAssertion(context => AttendanceIsEnabled(context, builder.Configuration)));
  options.AddPolicy("CapitalHumanoSupervisor", policy => policy
    .RequireRole("Administrador", "CapitalHumanoAdmin", "CapitalHumanoSupervisor")
    .RequireAssertion(context => AttendanceIsEnabled(context, builder.Configuration)));
  options.AddPolicy("CapitalHumanoNomina", policy => policy
    .RequireRole("Administrador", "CapitalHumanoAdmin", "CapitalHumanoNomina")
    .RequireAssertion(context => AttendanceIsEnabled(context, builder.Configuration)));
  options.AddPolicy("CapitalHumanoManagement", policy => policy
    .RequireRole("Administrador", "CapitalHumanoAdmin", "CapitalHumanoSupervisor", "CapitalHumanoNomina")
    .RequireAssertion(context => AttendanceIsEnabled(context, builder.Configuration)));
  options.AddPolicy("CapacitacionEmployee", policy => policy
    .RequireAuthenticatedUser()
    .RequireClaim("employee_id")
    .RequireClaim("rfc"));
  options.AddPolicy("CapacitacionInstructor", policy => policy
    .RequireAuthenticatedUser()
    .RequireClaim("employee_id")
    .RequireClaim("rfc")
    .RequireRole("Administrador", "CapacitacionAdmin", "CapacitacionInstructor"));
  options.AddPolicy("CapacitacionAdmin", policy => policy
    .RequireAuthenticatedUser()
    .RequireClaim("employee_id")
    .RequireClaim("rfc")
    .RequireRole("Administrador", "CapacitacionAdmin"));
  options.AddPolicy("CapacitacionAuditor", policy => policy
    .RequireAuthenticatedUser()
    .RequireClaim("employee_id")
    .RequireClaim("rfc")
    .RequireRole("Administrador", "CapacitacionAdmin", "CapacitacionInstructor", "CapacitacionAuditor"));
  options.AddPolicy(
      "RoleForSelectedRfc",
      policy => policy.Requirements.Add(new RoleForRfcRequirement("Administrador")));
  options.AddPolicy(
      "FinanzasLectura",
      policy => policy.Requirements.Add(new RoleForRfcRequirement("FinanzasLectura", "FinanzasManager")));
  options.AddPolicy(
      "FinanzasManager",
      policy => policy.Requirements.Add(new RoleForRfcRequirement("FinanzasManager")));
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
builder.Services.AddTrainingSafety(
  builder.Environment.EnvironmentName,
  conn,
  platformIsolation,
  builder.Configuration["Hosting:WindowsServiceUrl"],
  isMarkedTrainingService,
  trainingDatabaseSafety,
  builder.Configuration["AllowedHosts"]);
builder.Services.AddScoped<IUiMessageService, UiMessageService>();
builder.Services.AddHostedService<RestaurantEventBroadcaster>();

builder.Host.UseWindowsService();

// A marked Training process always binds its validated loopback URL, including
// command-line smoke tests. Production keeps its historical Windows-service
// behavior.
if (isMarkedTrainingService || (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService()))
{
  var windowsServiceUrl = builder.Configuration["Hosting:WindowsServiceUrl"];
  if (string.IsNullOrWhiteSpace(windowsServiceUrl))
  {
    windowsServiceUrl = "http://localhost:5000";
  }

  builder.WebHost.UseUrls(windowsServiceUrl);
}

var app = builder.Build();

app.UseForwardedHeaders();

if (app.Environment.IsEnvironment(TrainingEnvironment.Name))
{
  app.Use(async (context, next) =>
  {
    context.Response.Headers.ContentSecurityPolicy =
      "default-src 'self'; base-uri 'self'; object-src 'none'; frame-src 'none'; " +
      "form-action 'self'; connect-src 'self'; img-src 'self' data: blob:; " +
      "font-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'";
    context.Response.Headers["Permissions-Policy"] =
      "camera=(), geolocation=(), microphone=(), payment=(), usb=(), serial=(), bluetooth=()";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (context.Request.Path.StartsWithSegments("/Identity/Account/Register"))
    {
      context.Response.StatusCode = StatusCodes.Status404NotFound;
      return;
    }
    await next();
  });
}

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
app.UseMiddleware<CompanyScopeGuardMiddleware>();
app.UseAuthorization();
app.UseRateLimiter();

app.MapRazorPages();
app.MapBlazorHub();
app.MapHub<RestaurantEventsHub>("/hubs/restaurante");
app.MapTrainingReadiness();
if (app.Environment.IsEnvironment(TrainingEnvironment.Name))
{
  app.MapTrainingBlockedExternalEffectEndpoints();
}
else
{
  app.MapRestaurantQzTraySigningApi();
  app.MapOpenClawReservationsApi();
}
app.MapPost("/api/workforce/kiosk/pair", async (
  KioskPairApiRequest request,
  IKioskAttendanceService service,
  HttpContext context,
  IConfiguration configuration,
  IHostEnvironment hostEnvironment,
  CancellationToken ct) =>
{
  if (!configuration.GetValue<bool>("CapitalHumano:AttendanceEnabled")) return Results.NotFound();
  var result = await service.PairAsync(request.PairingCode, ct);
  if (!result.Success || string.IsNullOrWhiteSpace(result.DeviceToken))
    return Results.BadRequest(new { result.Message });
  context.Response.Cookies.Append(platformIsolation.KioskDeviceCookieName, result.DeviceToken, new CookieOptions
  {
    HttpOnly = true,
    Secure = !hostEnvironment.IsDevelopment() || context.Request.IsHttps,
    SameSite = SameSiteMode.Strict,
    Path = "/api/workforce/kiosk",
    MaxAge = TimeSpan.FromDays(90),
    IsEssential = true
  });
  return Results.Ok(new { result.Message, result.DeviceName });
}).RequireRateLimiting("workforce-kiosk");
app.MapPost("/api/workforce/kiosk/punch", async (
  KioskPunchRequest request,
  IKioskAttendanceService service,
  HttpContext context,
  IConfiguration configuration,
  CancellationToken ct) =>
{
  if (!configuration.GetValue<bool>("CapitalHumano:AttendanceEnabled")) return Results.NotFound();
  var token = context.Request.Cookies[platformIsolation.KioskDeviceCookieName];
  if (string.IsNullOrWhiteSpace(token)) return Results.Unauthorized();
  var result = await service.PunchAsync(token, request, ct);
  return result.Success ? Results.Ok(result) : Results.BadRequest(result);
}).RequireRateLimiting("workforce-kiosk");
app.MapGet("/api/workforce/prenomina/exports/{exportId:long}/{format}", async (
  long exportId,
  string format,
  string rfc,
  ICurrentCompanyContext companyContext,
  IPrenominaExportService service,
  CancellationToken ct) =>
{
  if (!string.Equals(companyContext.CurrentRfc, rfc, StringComparison.OrdinalIgnoreCase)) return Results.Forbid();
  var bundle = await service.GetAsync(exportId, rfc, ct);
  if (bundle is null) return Results.NotFound();
  return format.Equals("xlsx", StringComparison.OrdinalIgnoreCase)
    ? Results.File(bundle.XlsxBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", bundle.XlsxFileName)
    : format.Equals("csv", StringComparison.OrdinalIgnoreCase) || format.Equals("zip", StringComparison.OrdinalIgnoreCase)
      ? Results.File(bundle.ZipBytes, "application/zip", bundle.ZipFileName)
      : Results.NotFound();
}).RequireAuthorization("CapitalHumanoNomina");
app.MapGet("/bonhomia", (IOptions<BonhomiaCheckoutOptions> options, ITrainingEnvironmentState trainingState) =>
{
  if (trainingState.IsTraining)
  {
    return Results.Problem(
      title: "Acción externa bloqueada",
      detail: TrainingExternalEffectsPolicy.BlockedMessage("reservaciones públicas y PayPal"),
      statusCode: StatusCodes.Status409Conflict);
  }

  var publicBaseUrl = options.Value.PublicBaseUrl?.Trim();
  if (!string.IsNullOrWhiteSpace(publicBaseUrl)
      && Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var baseUri))
  {
    return Results.Redirect(new Uri(baseUri, "/bonhomia").ToString());
  }

  return Results.Redirect("/");
});
app.MapGet("/company-branding/{rfc}/logo", async (
  string rfc,
  ICompanyAccessService companyAccess,
  HttpContext context) =>
{
  var logo = await companyAccess.GetLogoAsync(rfc, context.RequestAborted);
  return logo is null
    ? Results.NotFound()
    : Results.File(
      logo.Bytes,
      logo.ContentType,
      lastModified: null,
      entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{logo.BrandingVersion}\""),
      enableRangeProcessing: false);
}).AllowAnonymous();
app.MapFallbackToPage("/_Host");

// Seed Identity
using (var scope = app.Services.CreateScope())
{
  await IdentitySeeder.RunAsync(scope.ServiceProvider);
}

app.Run();

// Needed only if you enable AddUserSecrets<Program> above (partial to link with implicit Program class)
public partial class Program { }

public sealed record KioskPairApiRequest(string PairingCode);
