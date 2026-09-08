using System.Net;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting.WindowsServices;
using OrionERP.Application.Features.Bonhomia.PublicBooking;
using OrionERP.Application.Features.Platform;
using OrionERP.Bonhomia.Web.Features.Bonhomia.Checkout;
using OrionERP.Infrastructure.Features.Bonhomia.PublicBooking;
using OrionERP.Infrastructure.Features.Mail;
using OrionERP.Infrastructure.Features.Platform;
using OrionERP.Infrastructure.Features.Reservaciones.ListaReservaciones.Pdf;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();

builder.Configuration.Sources.Clear();
builder.Configuration
  .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
  .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
  .AddJsonFile("appsettings.Instance.json", optional: true, reloadOnChange: false)
  .AddEnvironmentVariables(prefix: "ASPNETCORE_")
  .AddEnvironmentVariables(prefix: "DOTNET_");

if (builder.Environment.IsDevelopment())
{
  builder.Configuration.AddUserSecrets<Program>(optional: true);
}

var publicWebsite = PublicWebsiteInstancePolicy.Create(
  builder.Configuration
    .GetSection(PublicWebsiteInstanceOptions.SectionName)
    .Get<PublicWebsiteInstanceOptions>() ?? new PublicWebsiteInstanceOptions(),
  PlatformModuleCodes.Hospitality);
var hospitalityOptions = builder.Configuration
  .GetSection(HospitalityWebsiteOptions.SectionName)
  .Get<HospitalityWebsiteOptions>() ?? new HospitalityWebsiteOptions();
var requiredPresentationAssets = hospitalityOptions.HomeGalleryAssetKeys
  .Concat(hospitalityOptions.BuildingGalleryAssetKeys)
  .Concat(hospitalityOptions.Rooms.Select(room => room.PrimaryAssetKey))
  .Concat(hospitalityOptions.Rooms.SelectMany(room => room.GalleryAssetKeys))
  .Append("hero-image")
  .Append("letterhead-logo")
  .Distinct(StringComparer.OrdinalIgnoreCase)
  .ToArray();
var presentation = PublicWebsitePresentationPolicy.Create(
  builder.Configuration
    .GetSection(PublicWebsitePresentationOptions.SectionName)
    .Get<PublicWebsitePresentationOptions>() ?? new PublicWebsitePresentationOptions(),
  publicWebsite,
  requiredPresentationAssets);
var hospitalityWebsite = HospitalityWebsitePolicy.Create(hospitalityOptions, presentation);
var presentationWebRootPath = builder.Environment.WebRootPath
  ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
PublicWebsitePresentationPolicy.EnsureAssetsExist(presentation, presentationWebRootPath);

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
  Console.WriteLine("Hospitality instance profile validated successfully.");
  return;
}

// Existing checkout code consumes these values synchronously. Deriving both
// from the fixed process identity prevents a request from choosing a tenant or
// a callback origin.
builder.Configuration[$"{BonhomiaCheckoutOptions.SectionName}:PublicBaseUrl"] =
  publicWebsite.CanonicalBaseUri.GetLeftPart(UriPartial.Authority);
builder.Configuration[$"{BonhomiaCheckoutOptions.SectionName}:AccountingRfc"] =
  publicWebsite.ExpectedCompanyRfc;
builder.Configuration[$"{BonhomiaCheckoutOptions.SectionName}:AccountingAccount"] =
  hospitalityWebsite.AccountingAccount;
builder.Configuration[$"{BonhomiaCheckoutOptions.SectionName}:PublicName"] =
  presentation.PublicName;
builder.Configuration[$"{BonhomiaCheckoutOptions.SectionName}:ReservationSourceLabel"] =
  hospitalityWebsite.ReservationSourceLabel;
builder.Configuration[$"{BonhomiaCheckoutOptions.SectionName}:PdfFilePrefix"] =
  hospitalityWebsite.PdfFilePrefix;
builder.Configuration[$"{BonhomiaGraphMailOptions.SectionName}:SenderAddress"] =
  presentation.PublicEmail;
builder.Configuration["AllowedHosts"] =
  $"{publicWebsite.CanonicalHost};www.{publicWebsite.CanonicalHost};localhost;127.0.0.1";

var appDataDirectory = Path.Combine(
  AppContext.BaseDirectory,
  "App_Data",
  "keys",
  publicWebsite.PublicSiteKey);
Directory.CreateDirectory(appDataDirectory);

builder.Services
  .AddDataProtection()
  .PersistKeysToFileSystem(new DirectoryInfo(appDataDirectory))
  .SetApplicationName($"OrionERP.PublicWebsite.Hospitality.{publicWebsite.PublicSiteKey}");

var conn = builder.Configuration.GetConnectionString("OrionDb");
var allowProductionDbInDevelopment = builder.Configuration.GetValue<bool>("AllowProductionDbInDevelopment");

if (!string.IsNullOrWhiteSpace(conn))
{
  try
  {
    var connBuilder = new SqlConnectionStringBuilder(conn);
    if (builder.Environment.IsDevelopment()
        && string.Equals(connBuilder.InitialCatalog, "grupocarpio", StringComparison.OrdinalIgnoreCase)
        && !allowProductionDbInDevelopment)
    {
      connBuilder.InitialCatalog = "Orion_Sandbox";
      conn = connBuilder.ConnectionString;
      Console.WriteLine("[BONHOMIA BOOT] Development connection retargeted from 'grupocarpio' to 'Orion_Sandbox'.");
    }
  }
  catch
  {
    // Leave validation/error reporting to the missing/empty connection-string check below.
  }
}

if (!string.IsNullOrWhiteSpace(conn))
{
  builder.Configuration["ConnectionStrings:OrionDb"] = conn;
}

var checkoutOptions = builder.Configuration
  .GetSection(BonhomiaCheckoutOptions.SectionName)
  .Get<BonhomiaCheckoutOptions>() ?? new BonhomiaCheckoutOptions();

Console.WriteLine(
  $"[BONHOMIA BOOT] ENV={builder.Environment.EnvironmentName} " +
  $"PublicSiteKey={publicWebsite.PublicSiteKey} " +
  $"OrionDb={BuildConnectionSummary(conn)} " +
  $"PayPalMode={BuildPayPalModeSummary(checkoutOptions)} " +
  $"PayPalConfigured={checkoutOptions.IsPayPalConfigured} " +
  $"PublicBaseUrl={BuildPublicBaseUrlSummary(checkoutOptions.PublicBaseUrl)}");

if (string.IsNullOrWhiteSpace(conn))
{
  throw new InvalidOperationException(
    "Missing/empty ConnectionStrings:OrionDb. In Development, set it with User Secrets, " +
    "a local appsettings.Development.json, or ConnectionStrings__OrionDb. In Production, " +
    "use ASPNETCORE_ConnectionStrings__OrionDb.");
}

var checkoutValidationErrors = BonhomiaCheckoutOptionsValidator.ValidateForEnvironment(
  checkoutOptions,
  builder.Environment.EnvironmentName);
if (checkoutValidationErrors.Count > 0)
{
  throw new InvalidOperationException(
    "Invalid BonhomiaCheckout production configuration: " +
    string.Join(" ", checkoutValidationErrors));
}

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddScoped<ProtectedSessionStorage>();
builder.Services.AddPublicWebsiteInstance(conn, publicWebsite, presentation);
builder.Services.AddSingleton(hospitalityWebsite);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
  options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
    ForwardedHeaders.XForwardedProto |
    ForwardedHeaders.XForwardedHost;
  options.ForwardLimit = 1;
  options.KnownProxies.Clear();
  options.KnownIPNetworks.Clear();
  options.KnownProxies.Add(IPAddress.Loopback);
  options.KnownProxies.Add(IPAddress.IPv6Loopback);
});

builder.Services.Configure<BonhomiaCheckoutOptions>(builder.Configuration.GetSection(BonhomiaCheckoutOptions.SectionName));
builder.Services.Configure<BonhomiaGraphMailOptions>(builder.Configuration.GetSection(BonhomiaGraphMailOptions.SectionName));
builder.Services.Configure<ReservacionPdfOptions>(options =>
{
  var webRootPath = builder.Environment.WebRootPath ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
  options.LogoPath = PublicWebsitePresentationPolicy.ResolveExistingAssetPhysicalPath(
    webRootPath,
    presentation.Asset("letterhead-logo"));
  options.PublicName = presentation.PublicName;
  options.Tagline = presentation.Tagline;
  options.PrimaryColor = presentation.PrimaryColor;
  options.PrimaryDarkColor = presentation.PrimaryDarkColor;
  options.AccentColor = presentation.AccentColor;
});

builder.Services.AddScoped<IHospitalityWebsiteScopeAccessor, HospitalityWebsiteScopeAccessor>();
builder.Services.AddScoped<IBonhomiaScopedPublicDataReader, BonhomiaScopedPublicDataReader>();
builder.Services.AddScoped<IBonhomiaPublicBookingService, BonhomiaPublicBookingService>();
builder.Services.AddHttpClient<IBonhomiaPayPalClient, BonhomiaPayPalClient>();
builder.Services.AddHttpClient<IMicrosoftGraphMailClient<BonhomiaGraphMailOptions>, MicrosoftGraphMailClient<BonhomiaGraphMailOptions>>();
builder.Services.AddScoped<IBonhomiaReservationConfirmationEmailSender, BonhomiaReservationConfirmationEmailSender>();
builder.Services.AddSingleton<IBonhomiaQuoteTokenService, BonhomiaQuoteTokenService>();
builder.Services.AddSingleton<IBonhomiaReservationPdfTokenService, BonhomiaReservationPdfTokenService>();
builder.Services.AddScoped<IReservacionPdfDocumentFactory, ReservacionPdfDocumentFactory>();
builder.Services.AddScoped<IReservacionPdfService, ReservacionPdfService>();

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
  app.UseExceptionHandler("/Error");
  app.UseHsts();
}

app.UseConfiguredPublicWebsite();
app.UseStaticFiles();
app.UseRouting();

app.MapGet("/healthz", () => Results.Text("OK", "text/plain"));
app.MapGet("/readyz", async (
  IPublicWebsiteInstanceContext website,
  IBonhomiaScopedPublicDataReader hospitalityData,
  IBonhomiaPublicBookingService bookingService,
  CancellationToken ct) =>
{
  try
  {
    await website.ResolveRequiredAsync(ct);
    await hospitalityData.ValidateSchemaAsync(ct);
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    var availability = await bookingService.GetAvailabilityAsync(today, today.AddDays(1), ct);
    if (availability.Rooms.Count == 0)
      throw new InvalidOperationException("The configured hospitality website has no scoped public rooms.");
    await hospitalityData.GetExtraOptionsAsync(ct);
    return Results.Text("OK", "text/plain");
  }
  catch
  {
    return Results.Text("NOT READY", "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);
  }
});
app.MapBonhomiaCheckoutApi();
app.MapRazorPages();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();

static string BuildConnectionSummary(string? connectionString)
{
  if (string.IsNullOrWhiteSpace(connectionString))
  {
    return "<missing>";
  }

  try
  {
    var connBuilder = new SqlConnectionStringBuilder(connectionString);
    var authSummary = string.IsNullOrWhiteSpace(connBuilder.UserID)
      ? "IntegratedSecurity"
      : $"SqlAuth:{connBuilder.UserID}";
    return $"Server={connBuilder.DataSource};Database={connBuilder.InitialCatalog};Auth={authSummary}";
  }
  catch
  {
    return "<present>";
  }
}

static string BuildPayPalModeSummary(BonhomiaCheckoutOptions options)
{
  var mode = string.IsNullOrWhiteSpace(options.Environment)
    ? "<empty>"
    : options.Environment.Trim();
  var target = options.UseLivePayPal ? "Live" : "Sandbox";
  return $"{mode}->{target}";
}

static string BuildPublicBaseUrlSummary(string? publicBaseUrl)
  => string.IsNullOrWhiteSpace(publicBaseUrl)
    ? "<missing>"
    : publicBaseUrl.Trim();

public partial class Program { }
