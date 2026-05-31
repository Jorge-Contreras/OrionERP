using System.Net;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting.WindowsServices;
using OrionERP.Application.Features.Bonhomia.PublicBooking;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;
using OrionERP.Bonhomia.Web.Features.Bonhomia.Checkout;
using OrionERP.Infrastructure.Features.Bonhomia.PublicBooking;
using OrionERP.Infrastructure.Features.Reservaciones.ListaReservaciones.Pdf;
using OrionERP.Infrastructure.Features.Reservaciones.ListaReservaciones.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();

if (!builder.Environment.IsDevelopment())
{
  builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 5010));
}

var appDataDirectory = Path.Combine(AppContext.BaseDirectory, "App_Data", "keys");
Directory.CreateDirectory(appDataDirectory);

builder.Services
  .AddDataProtection()
  .PersistKeysToFileSystem(new DirectoryInfo(appDataDirectory))
  .SetApplicationName("OrionERP");

builder.Configuration.Sources.Clear();
builder.Configuration
  .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
  .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
  .AddEnvironmentVariables(prefix: "ASPNETCORE_")
  .AddEnvironmentVariables(prefix: "DOTNET_");

if (builder.Environment.IsDevelopment())
{
  builder.Configuration.AddUserSecrets<Program>(optional: true);
}

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

builder.Services.Configure<BonhomiaCheckoutOptions>(builder.Configuration.GetSection(BonhomiaCheckoutOptions.SectionName));
builder.Services.Configure<ReservacionPdfOptions>(options =>
{
  var webRootPath = builder.Environment.WebRootPath ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
  options.LogoPath = Path.Combine(webRootPath, "Images", "Bonhomia", "logo-letterhead.svg");
});

builder.Services.AddScoped<IListaReservacionesService, ListaReservacionesService>();
builder.Services.AddScoped<IBonhomiaPublicBookingService, BonhomiaPublicBookingService>();
builder.Services.AddHttpClient<IBonhomiaPayPalClient, BonhomiaPayPalClient>();
builder.Services.AddSingleton<IBonhomiaQuoteTokenService, BonhomiaQuoteTokenService>();
builder.Services.AddSingleton<IBonhomiaReservationPdfTokenService, BonhomiaReservationPdfTokenService>();
builder.Services.AddScoped<IReservacionPdfDocumentFactory, ReservacionPdfDocumentFactory>();
builder.Services.AddScoped<IReservacionPdfService, ReservacionPdfService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
  app.UseExceptionHandler("/Error");
  app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();

app.MapGet("/healthz", () => Results.Text("OK", "text/plain"));
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
