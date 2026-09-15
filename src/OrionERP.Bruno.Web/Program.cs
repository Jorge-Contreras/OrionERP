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
using OrionERP.Application.Features.Payments.PayPal;
using OrionERP.Application.Features.Platform;
using OrionERP.Application.Features.Restaurante;
using OrionERP.Bruno.Web;
using OrionERP.Bruno.Web.Configuration;
using OrionERP.Bruno.Web.Features.Ordering;
using OrionERP.Bruno.Web.Services;
using OrionERP.Infrastructure.Auth;
using OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Dapper;
using OrionERP.Infrastructure.Features.Mail;
using OrionERP.Infrastructure.Features.Payments.PayPal;
using OrionERP.Infrastructure.Features.Platform;
using OrionERP.Infrastructure.Features.Restaurante;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

builder.Configuration
  .AddJsonFile("appsettings.Instance.json", optional: true, reloadOnChange: false)
  .AddEnvironmentVariables(prefix: "ASPNETCORE_")
  .AddEnvironmentVariables(prefix: "DOTNET_")
  .AddCommandLine(args);

var usesLegacyMail = LegacyConfigurationAliases.ApplySectionAlias(
  builder.Configuration,
  RestaurantMailOptions.SectionName,
  RestaurantMailOptions.LegacySectionName);
if (usesLegacyMail)
  Console.WriteLine("[RESTAURANT BOOT] Legacy configuration aliases are active until 2026-12-31.");

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
builder.Configuration[$"{RestaurantMailOptions.SectionName}:PublicBaseUrl"] = canonicalBaseUrl;
builder.Configuration[$"{RestaurantMailOptions.SectionName}:SenderAddress"] = presentation.PublicEmail;
builder.Configuration[$"{RestaurantCheckoutOptions.SectionName}:PublicBaseUrl"] = canonicalBaseUrl;
builder.Configuration[$"{RestaurantCheckoutOptions.SectionName}:TermsVersion"] = presentation.TermsVersion;
builder.Configuration[$"{RestaurantCheckoutOptions.SectionName}:PrivacyVersion"] = presentation.PrivacyVersion;
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

builder.Services.AddScoped<PublicIdentityConnectionInterceptor>();
builder.Services.AddDbContext<PublicIdentityDbContext>((services, options) => options
  .UseSqlServer(connectionString)
  .AddInterceptors(services.GetRequiredService<PublicIdentityConnectionInterceptor>()));
builder.Services
  .AddIdentity<PublicSiteUser, PublicSiteRole>(options =>
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
  .AddEntityFrameworkStores<PublicIdentityDbContext>()
  .AddUserStore<PublicSiteUserStore>()
  .AddDefaultTokenProviders();
builder.Services.AddHttpContextAccessor();
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
  var culture = CultureInfo.GetCultureInfo(presentation.Locale);
  options.DefaultRequestCulture = new RequestCulture(culture);
  options.SupportedCultures = [culture];
  options.SupportedUICultures = [culture];
});
builder.Services.AddScoped<IPublicIdentityScopeAccessor, VerifiedPublicIdentityScopeAccessor>();
builder.Services.AddScoped<IUserClaimsPrincipalFactory<PublicSiteUser>, PublicSiteUserClaimsPrincipalFactory>();
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
  options.Events.OnValidatePrincipal = PublicSiteUserCookieScopeValidator.ValidatePrincipalAsync;
});

builder.Services.AddAuthorization();
builder.Services.AddRazorPages(options =>
{
  options.Conventions.AuthorizeFolder("/Account/Member");
});
builder.Services.AddServerSideBlazor();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAntiforgery(options =>
{
  // The layout emits a token on every page, and antiforgery throws on a Secure-only
  // cookie over plain HTTP, so Development (http://localhost:5220) drops the __Host- prefix.
  var requireSecureCookie = !builder.Environment.IsDevelopment();
  options.Cookie.Name = requireSecureCookie
    ? $"__Host-OrionRestaurant.{publicWebsite.PublicSiteKey}.Xsrf"
    : $"OrionRestaurant.{publicWebsite.PublicSiteKey}.Xsrf";
  options.Cookie.HttpOnly = true;
  options.Cookie.SecurePolicy = requireSecureCookie
    ? CookieSecurePolicy.Always
    : CookieSecurePolicy.SameAsRequest;
  options.Cookie.SameSite = SameSiteMode.Strict;
  options.HeaderName = "X-CSRF-TOKEN";
});

builder.Services.AddPublicWebsiteInstance(connectionString, publicWebsite, presentation);
builder.Services.AddScoped<PublicWebsiteSqlConnectionFactory>();
builder.Services.AddScoped<IDbConnectionFactory>(sp => sp.GetRequiredService<PublicWebsiteSqlConnectionFactory>());
builder.Services.AddScoped<IRestaurantCatalogService, RestaurantCatalogService>();
builder.Services.AddScoped<IRestaurantPromotionService, RestaurantPromotionService>();
builder.Services.AddScoped<IRestaurantLoyaltyService, LoyaltyService>();
builder.Services.AddScoped<IRestaurantMembershipService>(sp => sp.GetRequiredService<IRestaurantLoyaltyService>());
builder.Services.AddScoped<IRestaurantPublicCatalogService, RestaurantPublicCatalogService>();
builder.Services.AddScoped<IPublicIdentityReadiness, PublicIdentityReadiness>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IOnlineRestaurantQuoteTokenService, RestaurantOnlineQuoteTokenService>();
builder.Services.AddScoped<IOnlineRestaurantCheckoutService, RestaurantOnlineCheckoutService>();
builder.Services.AddScoped<IOnlineRestaurantQuoteService>(services =>
  services.GetRequiredService<IOnlineRestaurantCheckoutService>());
builder.Services
  .AddOptions<RestaurantCheckoutOptions>()
  .Bind(builder.Configuration.GetSection(RestaurantCheckoutOptions.SectionName))
  .Validate(
    options => RestaurantCheckoutOptionsPolicy.Validate(options, builder.Environment.IsProduction()).Count == 0,
    "RestaurantCheckout contiene una configuración inválida para el ambiente actual.")
  .ValidateOnStart();
builder.Services.AddHttpClient<IPayPalOrdersClient, PayPalOrdersClient<RestaurantCheckoutOptions>>(client =>
  client.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHttpClient("RestaurantPayPalHistorical", client =>
  client.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddScoped<IRestaurantPayPalClientResolver, RestaurantPayPalClientResolver>();
builder.Services.AddScoped<IPayPalRecoveryProcessor, RestaurantPayPalRecoveryProcessor>();
builder.Services.AddScoped<IOnlineRestaurantOrderEmailSender, OnlineRestaurantOrderEmailSender>();
builder.Services.AddScoped<IOnlineRestaurantOrderNotificationQueue, OnlineRestaurantOrderNotificationQueue>();
builder.Services.AddHostedService<OnlineRestaurantOrderNotificationWorker>();
builder.Services.AddHostedService<OnlineRestaurantPaymentRecoveryWorker>();

builder.Services
  .AddOptions<RestaurantMailOptions>()
  .Bind(builder.Configuration.GetSection(RestaurantMailOptions.SectionName))
  .Validate(
    options =>
      !string.IsNullOrWhiteSpace(options.TenantId) &&
      !string.IsNullOrWhiteSpace(options.ClientId) &&
      !string.IsNullOrWhiteSpace(options.ClientSecret) &&
      !string.IsNullOrWhiteSpace(options.SenderAddress),
    "PublicIntegrations:Mail requiere TenantId, ClientId, ClientSecret y SenderAddress.")
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
builder.Services.AddHttpClient<IMicrosoftGraphMailClient<RestaurantMailOptions>, MicrosoftGraphMailClient<RestaurantMailOptions>>();
builder.Services.AddScoped<IEmailSender<PublicSiteUser>, BrunoEmailSender>();
builder.Services.AddHttpClient<IBrunoTurnstileService, BrunoTurnstileService>(client =>
{
  client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddRateLimiter(options =>
{
  options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
  options.AddPolicy("account", context =>
    RateLimitPartition.GetFixedWindowLimiter(
      context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
      _ => new FixedWindowRateLimiterOptions
      {
        PermitLimit = 10,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0
      }));
  options.AddPolicy("checkout", context =>
    RateLimitPartition.GetFixedWindowLimiter(
      context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
      _ => new FixedWindowRateLimiterOptions
      {
        PermitLimit = 30,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0
      }));
  options.AddPolicy("webhook", context =>
    RateLimitPartition.GetFixedWindowLimiter(
      context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
      _ => new FixedWindowRateLimiterOptions
      {
        PermitLimit = 120,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0
      }));
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
  var incoming = context.Request.Headers["X-Correlation-ID"].ToString();
  var correlationId = Guid.TryParse(incoming, out var parsed)
    ? parsed.ToString("N")
    : Guid.NewGuid().ToString("N");
  context.TraceIdentifier = correlationId;
  context.Response.Headers["X-Correlation-ID"] = correlationId;
  await next();
});
app.Use(async (context, next) =>
{
  var isPrivateCheckoutPath = context.Request.Path.StartsWithSegments("/checkout")
    || context.Request.Path.StartsWithSegments("/pedido")
    || context.Request.Path.StartsWithSegments("/api/restaurant/checkout");
  context.Response.Headers["X-Content-Type-Options"] = "nosniff";
  context.Response.Headers["Referrer-Policy"] = isPrivateCheckoutPath ? "no-referrer" : "strict-origin-when-cross-origin";
  if (isPrivateCheckoutPath) context.Response.Headers.CacheControl = "no-store, private";
  context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(self)";
  context.Response.Headers["Content-Security-Policy"] =
    "default-src 'self'; img-src 'self' data: https:; style-src 'self' 'unsafe-inline'; " +
    "script-src 'self' 'unsafe-inline' https://challenges.cloudflare.com https://static.cloudflareinsights.com https://www.paypal.com https://www.sandbox.paypal.com https://www.paypalobjects.com https://*.paypal.com https://*.paypalobjects.com; " +
    "frame-src https://challenges.cloudflare.com https://www.paypal.com https://www.sandbox.paypal.com https://*.paypal.com https://*.paypalobjects.com; " +
    "connect-src 'self' https://cloudflareinsights.com https://www.paypal.com https://www.sandbox.paypal.com https://api.paypal.com https://api-m.paypal.com https://api-m.sandbox.paypal.com https://*.paypal.com https://*.paypalobjects.com; " +
    "base-uri 'self'; frame-ancestors 'none'; form-action 'self' https://*.paypal.com;";
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

  RestaurantPublicSiteSettingsDto? settings = null;
  try
  {
    var publicCatalog = context.RequestServices.GetRequiredService<IRestaurantPublicCatalogService>();
    var website = context.RequestServices.GetRequiredService<IPublicWebsiteInstanceContext>();
    var binding = await website.ResolveRequiredAsync(context.RequestAborted);
    settings = await publicCatalog.GetSettingsAsync(binding, context.RequestAborted);
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
  IPublicIdentityScopeAccessor identityScope,
  IPublicIdentityReadiness identityReadiness,
  IRestaurantPublicCatalogService publicCatalog,
  IOnlineRestaurantCheckoutService checkout,
  IOptions<BrunoTurnstileOptions> turnstile,
  IOptions<RestaurantCheckoutOptions> checkoutOptions,
  ILoggerFactory loggerFactory,
  CancellationToken ct) =>
{
  try
  {
    var binding = await website.ResolveRequiredAsync(ct);
    if (!await identityReadiness.IsReadyAsync(identityScope.Current, ct))
      return Results.Text("NOT READY", "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);
    var settings = await publicCatalog.GetSettingsAsync(binding, ct);
    if (settings is null || (settings.IsMembershipEnabled && !turnstile.Value.IsConfigured))
      return Results.Text("NOT READY", "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);

    var online = await checkout.GetConfigurationAsync(binding, ct);
    if (online.IsEnabled)
    {
      var options = checkoutOptions.Value;
      var heartbeatMaximumAge = TimeSpan.FromSeconds(options.ProcessorHeartbeatMaxAgeSeconds);
      var heartbeatIsFresh = online.ProcessorHeartbeatAtUtc.HasValue
        && DateTime.UtcNow - DateTime.SpecifyKind(online.ProcessorHeartbeatAtUtc.Value, DateTimeKind.Utc) <= heartbeatMaximumAge;
      var checkoutIsReady = options.IsPayPalConfigured
        && options.IsWebhookVerificationConfigured
        && (!app.Environment.IsProduction() || options.UseLivePayPal)
        && !string.IsNullOrWhiteSpace(online.PayPalClientId)
        && online.MaximumOrderAmount > 0
        && !string.IsNullOrWhiteSpace(online.OnlineHoursJson)
        && string.Equals(online.TermsVersion, presentation.TermsVersion, StringComparison.Ordinal)
        && string.Equals(online.PrivacyVersion, presentation.PrivacyVersion, StringComparison.Ordinal)
        && heartbeatIsFresh;
      if (!checkoutIsReady)
        return Results.Text("NOT READY", "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Text("OK", "text/plain");
  }
  catch (Exception exception)
  {
    loggerFactory
      .CreateLogger("OrionERP.Restaurant.Readiness")
      .LogError(exception, "Restaurant public-site readiness verification failed.");
    return Results.Text("NOT READY", "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);
  }
});
app.MapGet("/robots.txt", (IPublicWebsiteInstanceContext website) => Results.Text(
  $"User-agent: *\nAllow: /\nDisallow: /cuenta/\nDisallow: /checkout\nDisallow: /pedido/\nSitemap: {website.Instance.CanonicalBaseUri}sitemap.xml\n",
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
    <url><loc>{{origin}}/ordenar</loc></url>
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
  IRestaurantPublicCatalogService publicCatalog,
  CancellationToken ct) =>
{
  var binding = await website.ResolveRequiredAsync(ct);
  var image = await publicCatalog.GetProductImageAsync(binding, productId, thumbnail ?? true, ct);
  return image.HasValue ? Results.File(image.Value.Bytes, image.Value.ContentType) : Results.NotFound();
});

app.MapRestaurantCheckoutApi();
app.MapRazorPages();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");
app.Run();

public partial class Program { }
