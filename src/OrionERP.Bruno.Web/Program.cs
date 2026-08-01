using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Restaurante;
using OrionERP.Bruno.Web;
using OrionERP.Bruno.Web.Configuration;
using OrionERP.Bruno.Web.Services;
using OrionERP.Infrastructure.Auth;
using OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Dapper;
using OrionERP.Infrastructure.Features.Mail;
using OrionERP.Infrastructure.Features.Restaurante;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

if (!builder.Environment.IsDevelopment())
{
  builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 5020));
}

builder.Configuration
  .AddEnvironmentVariables(prefix: "ASPNETCORE_")
  .AddEnvironmentVariables(prefix: "DOTNET_")
  .AddCommandLine(args);

var connectionString = builder.Configuration.GetConnectionString("OrionDb");
if (string.IsNullOrWhiteSpace(connectionString))
{
  throw new InvalidOperationException("Missing ConnectionStrings:OrionDb for OrionERP.Bruno.Web.");
}
var connectionBuilder = new SqlConnectionStringBuilder(connectionString);
if (builder.Environment.IsDevelopment() &&
    string.Equals(connectionBuilder.InitialCatalog, "grupocarpio", StringComparison.OrdinalIgnoreCase) &&
    !builder.Configuration.GetValue<bool>("AllowProductionDbInDevelopment"))
{
  connectionBuilder.InitialCatalog = "Orion_Sandbox";
  connectionString = connectionBuilder.ConnectionString;
  builder.Configuration["ConnectionStrings:OrionDb"] = connectionString;
  Console.WriteLine("[BRUNO BOOT] Development database retargeted to Orion_Sandbox.");
}

var keyDirectory = Path.Combine(AppContext.BaseDirectory, "App_Data", "bruno-keys");
Directory.CreateDirectory(keyDirectory);
builder.Services.AddDataProtection()
  .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory))
  .SetApplicationName("OrionERP.Bruno.Web");

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
  options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
  options.ForwardLimit = 1;
  options.KnownProxies.Clear();
  options.KnownIPNetworks.Clear();
  options.KnownProxies.Add(IPAddress.Loopback);
  options.KnownProxies.Add(IPAddress.IPv6Loopback);
});

builder.Services.AddDbContext<BrunoIdentityDbContext>(options => options.UseSqlServer(connectionString));
builder.Services
  .AddIdentity<BrunoMemberUser, IdentityRole>(options =>
  {
    options.SignIn.RequireConfirmedEmail = true;
    options.Password.RequiredLength = 10;
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = false;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    options.User.RequireUniqueEmail = true;
    options.Tokens.EmailConfirmationTokenProvider = TokenOptions.DefaultEmailProvider;
  })
  .AddEntityFrameworkStores<BrunoIdentityDbContext>()
  .AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(options =>
{
  options.Cookie.Name = "__Host-BrunosGarden.Member";
  options.Cookie.HttpOnly = true;
  options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
  options.Cookie.SameSite = SameSiteMode.Lax;
  options.LoginPath = "/cuenta/acceso";
  options.LogoutPath = "/cuenta/salir";
  options.AccessDeniedPath = "/cuenta/acceso";
  options.ExpireTimeSpan = TimeSpan.FromHours(8);
  options.SlidingExpiration = true;
});

builder.Services.AddAuthorization();
builder.Services.AddRazorPages(options =>
{
  options.Conventions.AuthorizeFolder("/Account/Member");
});
builder.Services.AddServerSideBlazor();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddSingleton<ICurrentRfcAccessor, BrunoRfcAccessor>();
builder.Services.AddScoped<SqlConnectionFactory>();
builder.Services.AddScoped<IDbConnectionFactory>(sp => sp.GetRequiredService<SqlConnectionFactory>());
builder.Services.AddScoped<IRestaurantCatalogService, RestaurantCatalogService>();
builder.Services.AddScoped<IRestaurantPromotionService, RestaurantPromotionService>();
builder.Services.AddScoped<ILoyaltyService, LoyaltyService>();
builder.Services.AddScoped<IBrunoMemberService>(sp => sp.GetRequiredService<ILoyaltyService>());
builder.Services.AddScoped<IBrunoPublicCatalogService, BrunoPublicCatalogService>();

builder.Services
  .AddOptions<BrunoGraphMailOptions>()
  .Bind(builder.Configuration.GetSection(BrunoGraphMailOptions.SectionName))
  .Validate(
    options =>
      !string.IsNullOrWhiteSpace(options.TenantId) &&
      !string.IsNullOrWhiteSpace(options.ClientId) &&
      !string.IsNullOrWhiteSpace(options.ClientSecret) &&
      !string.IsNullOrWhiteSpace(options.SenderAddress),
    "BrunoGraphMail requiere TenantId, ClientId, ClientSecret y SenderAddress.")
  .Validate(
    options => string.Equals(
      options.SenderAddress,
      "info@brunosgarden.com",
      StringComparison.OrdinalIgnoreCase),
    "BrunoGraphMail:SenderAddress debe ser info@brunosgarden.com.")
  .ValidateOnStart();
builder.Services.Configure<BrunoSiteOptions>(builder.Configuration.GetSection(BrunoSiteOptions.SectionName));
builder.Services.Configure<BrunoTurnstileOptions>(builder.Configuration.GetSection(BrunoTurnstileOptions.SectionName));
builder.Services.AddHttpClient<IMicrosoftGraphMailClient<BrunoGraphMailOptions>, MicrosoftGraphMailClient<BrunoGraphMailOptions>>();
builder.Services.AddScoped<IEmailSender<BrunoMemberUser>, BrunoEmailSender>();
builder.Services.AddHttpClient<IBrunoTurnstileService, BrunoTurnstileService>();

builder.Services.AddRateLimiter(options =>
{
  options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
  options.AddFixedWindowLimiter("account", limiter =>
  {
    limiter.PermitLimit = 10;
    limiter.Window = TimeSpan.FromMinutes(1);
    limiter.QueueLimit = 0;
  });
  options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    RateLimitPartition.GetFixedWindowLimiter(
      context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
      _ => new FixedWindowRateLimiterOptions
      {
        PermitLimit = 240,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0
      }));
});

var app = builder.Build();
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
  app.UseExceptionHandler("/Error");
  app.UseHsts();
  app.UseHttpsRedirection();
}
app.Use(async (context, next) =>
{
  if (string.Equals(context.Request.Host.Host, "www.brunosgarden.com", StringComparison.OrdinalIgnoreCase))
  {
    context.Response.Redirect($"{BrunoSiteConstants.CanonicalBaseUrl}{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}", permanent: true);
    return;
  }
  context.Response.Headers["X-Content-Type-Options"] = "nosniff";
  context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
  context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(self)";
  context.Response.Headers["Content-Security-Policy"] =
    "default-src 'self'; img-src 'self' data: https:; style-src 'self' 'unsafe-inline'; " +
    "script-src 'self' 'unsafe-inline' https://challenges.cloudflare.com https://static.cloudflareinsights.com; " +
    "frame-src https://challenges.cloudflare.com; connect-src 'self' https://cloudflareinsights.com; base-uri 'self'; frame-ancestors 'none';";
  await next();
});
app.UseStaticFiles();
if (!app.Environment.IsDevelopment())
{
  app.Use(async (context, next) =>
  {
    if (context.Request.Path.Equals("/healthz", StringComparison.OrdinalIgnoreCase) ||
        context.Request.Path.Equals("/readyz", StringComparison.OrdinalIgnoreCase))
    {
      await next();
      return;
    }

    BrunoPublicSiteSettingsDto? settings = null;
    try
    {
      var publicCatalog = context.RequestServices.GetRequiredService<IBrunoPublicCatalogService>();
      settings = await publicCatalog.GetSettingsAsync(
        BrunoSiteConstants.Rfc,
        ct: context.RequestAborted);
    }
    catch (Exception ex)
    {
      var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("BrunoWebsiteGate");
      logger.LogError(ex, "Could not evaluate the Bruno public website feature flag.");
    }

    if (settings?.IsWebsiteEnabled != true)
    {
      context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
      context.Response.ContentType = "text/html; charset=utf-8";
      context.Response.Headers.RetryAfter = "300";
      context.Response.Headers.CacheControl = "no-store";
      await context.Response.WriteAsync(
        """
        <!doctype html>
        <html lang="es">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width,initial-scale=1">
          <meta name="robots" content="noindex,nofollow">
          <title>Bruno's Garden &amp; Snacks</title>
          <style>
            body{margin:0;min-height:100vh;display:grid;place-items:center;background:#111;color:#f7f0df;font:18px/1.5 system-ui,sans-serif;text-align:center}
            main{max-width:40rem;padding:3rem 1.5rem}img{width:min(16rem,70vw);height:auto}h1{font-size:clamp(2rem,7vw,4rem);margin:.6rem 0}p{color:#d9cdb8}
          </style>
        </head>
        <body><main><img src="/Images/Brunos/brunos-logo.png" alt="Bruno's Garden &amp; Snacks"><h1>Estamos preparando el jardín.</h1><p>Muy pronto nos encontraremos aquí.</p></main></body>
        </html>
        """,
        context.RequestAborted);
      return;
    }

    if (context.Request.Path.StartsWithSegments("/cuenta") &&
        !settings.IsMembershipEnabled)
    {
      context.Response.Redirect("/membresia?club=no-disponible");
      return;
    }

    await next();
  });
}
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Text("OK", "text/plain"));
app.MapGet("/readyz", async (
  IBrunoPublicCatalogService publicCatalog,
  CancellationToken ct) =>
{
  try
  {
    var settings = await publicCatalog.GetSettingsAsync(BrunoSiteConstants.Rfc, ct: ct);
    return settings is null
      ? Results.Text("NOT READY", "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable)
      : Results.Text("OK", "text/plain");
  }
  catch
  {
    return Results.Text("NOT READY", "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);
  }
});
app.MapGet("/robots.txt", () => Results.Text(
  "User-agent: *\nAllow: /\nDisallow: /cuenta/\nSitemap: https://brunosgarden.com/sitemap.xml\n",
  "text/plain"));
app.MapGet("/sitemap.xml", () => Results.Text(
  """
  <?xml version="1.0" encoding="UTF-8"?>
  <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
    <url><loc>https://brunosgarden.com/</loc></url>
    <url><loc>https://brunosgarden.com/menu</loc></url>
    <url><loc>https://brunosgarden.com/promociones</loc></url>
    <url><loc>https://brunosgarden.com/membresia</loc></url>
    <url><loc>https://brunosgarden.com/visitanos</loc></url>
    <url><loc>https://brunosgarden.com/privacidad</loc></url>
    <url><loc>https://brunosgarden.com/terminos</loc></url>
  </urlset>
  """,
  "application/xml"));
app.MapGet("/media/productos/{productId:long}", async (
  long productId,
  bool? thumbnail,
  IRestaurantCatalogService catalogService,
  CancellationToken ct) =>
{
  var image = await catalogService.GetProductImageAsync(BrunoSiteConstants.Rfc, productId, thumbnail ?? true, ct);
  return image.HasValue ? Results.File(image.Value.Bytes, image.Value.ContentType) : Results.NotFound();
});

app.MapRazorPages();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");
app.Run();

public partial class Program { }
