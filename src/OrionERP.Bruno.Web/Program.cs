using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Platform;
using OrionERP.Application.Features.Restaurante;
using OrionERP.Bruno.Web;
using OrionERP.Bruno.Web.Configuration;
using OrionERP.Bruno.Web.Services;
using OrionERP.Infrastructure.Auth;
using OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Dapper;
using OrionERP.Infrastructure.Features.Mail;
using OrionERP.Infrastructure.Features.Platform;
using OrionERP.Infrastructure.Features.Restaurante;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

builder.Configuration
  .AddJsonFile("appsettings.Instance.json", optional: true, reloadOnChange: false)
  .AddEnvironmentVariables(prefix: "ASPNETCORE_")
  .AddEnvironmentVariables(prefix: "DOTNET_")
  .AddCommandLine(args);

var publicWebsite = PublicWebsiteInstancePolicy.Create(
  builder.Configuration
    .GetSection(PublicWebsiteInstanceOptions.SectionName)
    .Get<PublicWebsiteInstanceOptions>() ?? new PublicWebsiteInstanceOptions(),
  PlatformModuleCodes.Restaurant);
var presentation = PublicWebsitePresentationPolicy.Create(
  builder.Configuration
    .GetSection(PublicWebsitePresentationOptions.SectionName)
    .Get<PublicWebsitePresentationOptions>() ?? new PublicWebsitePresentationOptions(),
  publicWebsite,
  "open-graph",
  "home-hero",
  "menu-hero",
  "visit-hero",
  "membership-hero",
  "hero-decoration",
  "membership-decoration");
PublicWebsitePresentationPolicy.EnsureAssetsExist(
  presentation,
  builder.Environment.WebRootPath ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot"));

if (!builder.Environment.IsDevelopment())
{
  if (!string.IsNullOrWhiteSpace(builder.Configuration["Urls"])
      || !string.IsNullOrWhiteSpace(builder.Configuration["HTTP_PORTS"])
      || !string.IsNullOrWhiteSpace(builder.Configuration["HTTPS_PORTS"])
      || builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
    throw new InvalidOperationException(
      "Public website address overrides are forbidden in Production. " +
      "The process must use its configured loopback-only port for the Cloudflare tunnel.");

  builder.WebHost.ConfigureKestrel(options =>
    options.Listen(IPAddress.Loopback, publicWebsite.LoopbackPort));
}

if (args.Any(argument => string.Equals(
      argument,
      "--validate-instance-profile=true",
      StringComparison.OrdinalIgnoreCase)))
{
  Console.WriteLine("Restaurant instance profile validated successfully.");
  return;
}

var canonicalBaseUrl = publicWebsite.CanonicalBaseUri.GetLeftPart(UriPartial.Authority);
builder.Configuration[$"{BrunoSiteOptions.SectionName}:PublicBaseUrl"] = canonicalBaseUrl;
builder.Configuration[$"{BrunoGraphMailOptions.SectionName}:PublicBaseUrl"] = canonicalBaseUrl;
builder.Configuration[$"{BrunoGraphMailOptions.SectionName}:SenderAddress"] = presentation.PublicEmail;
builder.Configuration[$"{BrunoTurnstileOptions.SectionName}:ExpectedHostname"] = publicWebsite.CanonicalHost;
builder.Configuration["AllowedHosts"] =
  $"{publicWebsite.CanonicalHost};www.{publicWebsite.CanonicalHost};localhost;127.0.0.1";

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

var keyDirectory = Path.Combine(
  AppContext.BaseDirectory,
  "App_Data",
  "keys",
  publicWebsite.PublicSiteKey);
Directory.CreateDirectory(keyDirectory);
builder.Services.AddDataProtection()
  .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory))
  .SetApplicationName($"OrionERP.PublicWebsite.Restaurant.{publicWebsite.PublicSiteKey}");

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
    options.Password.RequiredLength = 8;
    options.Password.RequireDigit = false;
    options.Password.RequireLowercase = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireNonAlphanumeric = false;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    options.User.RequireUniqueEmail = true;
    options.Tokens.EmailConfirmationTokenProvider = TokenOptions.DefaultEmailProvider;
  })
  .AddEntityFrameworkStores<BrunoIdentityDbContext>()
  .AddUserStore<RestaurantMemberUserStore>()
  .AddDefaultTokenProviders();
builder.Services.AddHttpContextAccessor();
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
  var culture = CultureInfo.GetCultureInfo(presentation.Locale);
  options.DefaultRequestCulture = new RequestCulture(culture);
  options.SupportedCultures = [culture];
  options.SupportedUICultures = [culture];
});
builder.Services.AddScoped<IRestaurantPublicIdentityScopeAccessor, ConfiguredRestaurantIdentityScopeAccessor>();
builder.Services.AddScoped<IUserClaimsPrincipalFactory<BrunoMemberUser>, RestaurantMemberClaimsPrincipalFactory>();
builder.Services.ConfigureApplicationCookie(options =>
{
  options.Cookie.Name = $"__Host-OrionRestaurant.{publicWebsite.PublicSiteKey}.Member";
  options.Cookie.HttpOnly = true;
  options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
  options.Cookie.SameSite = SameSiteMode.Lax;
  options.LoginPath = "/cuenta/acceso";
  options.LogoutPath = "/cuenta/salir";
  options.AccessDeniedPath = "/cuenta/acceso";
  options.ExpireTimeSpan = TimeSpan.FromHours(8);
  options.SlidingExpiration = true;
  options.Events.OnValidatePrincipal = RestaurantMemberCookieScopeValidator.ValidatePrincipalAsync;
});

builder.Services.AddAuthorization();
builder.Services.AddRazorPages(options =>
{
  options.Conventions.AuthorizeFolder("/Account/Member");
});
builder.Services.AddServerSideBlazor();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddPublicWebsiteInstance(connectionString, publicWebsite, presentation);
builder.Services.AddScoped<SqlConnectionFactory>();
builder.Services.AddScoped<IDbConnectionFactory>(sp => sp.GetRequiredService<SqlConnectionFactory>());
builder.Services.AddScoped<IRestaurantCatalogService, RestaurantCatalogService>();
builder.Services.AddScoped<IRestaurantPromotionService, RestaurantPromotionService>();
builder.Services.AddScoped<ILoyaltyService, LoyaltyService>();
builder.Services.AddScoped<IBrunoMemberService>(sp => sp.GetRequiredService<ILoyaltyService>());
builder.Services.AddScoped<IBrunoPublicCatalogService, BrunoPublicCatalogService>();
builder.Services.AddScoped<IRestaurantPublicIdentityReadiness, RestaurantPublicIdentityReadiness>();

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
  .ValidateOnStart();
builder.Services.Configure<BrunoSiteOptions>(builder.Configuration.GetSection(BrunoSiteOptions.SectionName));
builder.Services
  .AddOptions<BrunoTurnstileOptions>()
  .Bind(builder.Configuration.GetSection(BrunoTurnstileOptions.SectionName))
  .Validate(
    options => options.HasConsistentKeyPair,
    "Turnstile requiere SiteKey y SecretKey juntos; no configure solamente una de las dos llaves.")
  .Validate(
    options => !options.IsConfigured || !string.IsNullOrWhiteSpace(options.ExpectedHostname),
    "Turnstile:ExpectedHostname es obligatorio cuando Turnstile está configurado.")
  .ValidateOnStart();
builder.Services.AddHttpClient<IMicrosoftGraphMailClient<BrunoGraphMailOptions>, MicrosoftGraphMailClient<BrunoGraphMailOptions>>();
builder.Services.AddScoped<IEmailSender<BrunoMemberUser>, BrunoEmailSender>();
builder.Services.AddHttpClient<IBrunoTurnstileService, BrunoTurnstileService>(client =>
{
  client.Timeout = TimeSpan.FromSeconds(10);
});

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
app.UseRequestLocalization();
if (!app.Environment.IsDevelopment())
{
  app.UseExceptionHandler("/Error");
  app.UseHsts();
  app.UseHttpsRedirection();
}
app.UseConfiguredPublicWebsite();
app.Use(async (context, next) =>
{
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
    var website = context.RequestServices.GetRequiredService<IPublicWebsiteInstanceContext>();
    var binding = await website.ResolveRequiredAsync(context.RequestAborted);
    settings = await publicCatalog.GetSettingsAsync(
      binding.CompanyRfc,
      binding.SiteKey,
      ct: context.RequestAborted);
  }
  catch (Exception ex)
  {
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RestaurantWebsiteGate");
    logger.LogError(ex, "Could not evaluate the restaurant public website feature flag.");
  }

  if (settings?.IsWebsiteEnabled != true)
  {
    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
    context.Response.ContentType = "text/html; charset=utf-8";
    context.Response.Headers.RetryAfter = "300";
    context.Response.Headers.CacheControl = "no-store";
    var publicName = WebUtility.HtmlEncode(presentation.PublicName);
    var tagline = WebUtility.HtmlEncode(presentation.Tagline);
    var logoPath = WebUtility.HtmlEncode(presentation.LogoPath);
    var primaryDark = WebUtility.HtmlEncode(presentation.PrimaryDarkColor);
    var accent = WebUtility.HtmlEncode(presentation.AccentColor);
    await context.Response.WriteAsync(
      $$"""
        <!doctype html>
        <html lang="{{WebUtility.HtmlEncode(presentation.Locale)}}">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width,initial-scale=1">
          <meta name="robots" content="noindex,nofollow">
          <title>{{publicName}}</title>
          <style>
            body{margin:0;min-height:100vh;display:grid;place-items:center;background:{{primaryDark}};color:#fff;font:18px/1.6 system-ui,sans-serif;text-align:center}
            main{max-width:40rem;padding:3rem 1.5rem}
            img{width:min(15rem,64vw);height:auto;-webkit-mask-image:radial-gradient(closest-side,#000 76%,transparent 99%);mask-image:radial-gradient(closest-side,#000 76%,transparent 99%)}
            h1{font-size:clamp(2rem,7vw,3.6rem);margin:1rem 0 .4rem;letter-spacing:.01em}p{color:{{accent}}}
          </style>
        </head>
        <body><main><img src="{{logoPath}}" alt="{{publicName}}"><h1>Estamos preparando el sitio.</h1><p>{{tagline}}</p></main></body>
        </html>
      """,
      context.RequestAborted);
    return;
  }

  if (context.Request.Path.StartsWithSegments("/cuenta") &&
      !settings.IsMembershipEnabled)
  {
    context.Response.Redirect("/membresia?membership=no-disponible");
    return;
  }

  await next();
});
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Text("OK", "text/plain"));
app.MapGet("/readyz", async (
  IPublicWebsiteInstanceContext website,
  IRestaurantPublicIdentityScopeAccessor identityScope,
  IRestaurantPublicIdentityReadiness identityReadiness,
  IBrunoPublicCatalogService publicCatalog,
  IOptions<BrunoTurnstileOptions> turnstile,
  CancellationToken ct) =>
{
  try
  {
    var binding = await website.ResolveRequiredAsync(ct);
    if (!await identityReadiness.IsReadyAsync(identityScope.Current, ct))
      return Results.Text("NOT READY", "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);
    var settings = await publicCatalog.GetSettingsAsync(binding.CompanyRfc, binding.SiteKey, ct);
    return settings is null ||
           (settings.IsMembershipEnabled && !turnstile.Value.IsConfigured)
      ? Results.Text("NOT READY", "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable)
      : Results.Text("OK", "text/plain");
  }
  catch
  {
    return Results.Text("NOT READY", "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);
  }
});
app.MapGet("/robots.txt", (IPublicWebsiteInstanceContext website) => Results.Text(
  $"User-agent: *\nAllow: /\nDisallow: /cuenta/\nSitemap: {website.Instance.CanonicalBaseUri}sitemap.xml\n",
  "text/plain"));
app.MapGet("/sitemap.xml", (IPublicWebsiteInstanceContext website) =>
{
  var origin = website.Instance.CanonicalBaseUri.GetLeftPart(UriPartial.Authority);
  return Results.Text(
  $$"""
  <?xml version="1.0" encoding="UTF-8"?>
  <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
    <url><loc>{{origin}}/</loc></url>
    <url><loc>{{origin}}/menu</loc></url>
    <url><loc>{{origin}}/promociones</loc></url>
    <url><loc>{{origin}}/membresia</loc></url>
    <url><loc>{{origin}}/visitanos</loc></url>
    <url><loc>{{origin}}/privacidad</loc></url>
    <url><loc>{{origin}}/terminos</loc></url>
  </urlset>
  """,
  "application/xml");
});
app.MapGet("/media/productos/{productId:long}", async (
  long productId,
  bool? thumbnail,
  IPublicWebsiteInstanceContext website,
  IRestaurantCatalogService catalogService,
  CancellationToken ct) =>
{
  var binding = await website.ResolveRequiredAsync(ct);
  var image = await catalogService.GetProductImageAsync(
    binding.CompanyRfc,
    binding.SiteId,
    productId,
    thumbnail ?? true,
    ct);
  return image.HasValue ? Results.File(image.Value.Bytes, image.Value.ContentType) : Results.NotFound();
});

app.MapRazorPages();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");
app.Run();

public partial class Program { }
