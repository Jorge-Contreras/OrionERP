using System.Net;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting.WindowsServices;
using OrionERP.Application.Features.Platform;
using OrionERP.Infrastructure.Features.Platform;

var builder=WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();
builder.Configuration.AddJsonFile("appsettings.Instance.json",optional:true,reloadOnChange:false)
  .AddEnvironmentVariables(prefix:"ASPNETCORE_")
  .AddEnvironmentVariables(prefix:"DOTNET_")
  .AddCommandLine(args);
var presentationProfile=builder.Configuration["PublicHost:PresentationProfile"];
if (!string.IsNullOrWhiteSpace(presentationProfile))
  builder.Configuration.AddJsonFile(presentationProfile,optional:false,reloadOnChange:true);

var instance=PublicWebsiteInstancePolicy.Create(
  builder.Configuration.GetSection(PublicWebsiteInstanceOptions.SectionName).Get<PublicWebsiteInstanceOptions>()
    ?? new PublicWebsiteInstanceOptions(),PlatformModuleCodes.Restaurant);
var presentation=PublicWebsitePresentationPolicy.Create(
  builder.Configuration.GetSection(PublicWebsitePresentationOptions.SectionName).Get<PublicWebsitePresentationOptions>()
    ?? new PublicWebsitePresentationOptions(),instance);
if (args.Any(value=>string.Equals(value,"--validate-instance-profile=true",StringComparison.OrdinalIgnoreCase)))
{
  Console.WriteLine("Restaurant generic instance profile validated successfully.");
  return;
}

var connectionString=builder.Configuration.GetConnectionString("OrionDb")
  ?? throw new InvalidOperationException("Missing ConnectionStrings:OrionDb.");
var sqlOptions=new SqlConnectionStringBuilder(connectionString);
if (builder.Environment.IsDevelopment()
    && string.Equals(sqlOptions.InitialCatalog,"grupocarpio",StringComparison.OrdinalIgnoreCase)
    && !builder.Configuration.GetValue<bool>("AllowProductionDbInDevelopment"))
{
  sqlOptions.InitialCatalog="Orion_Sandbox";
  connectionString=sqlOptions.ConnectionString;
}
builder.Configuration["ConnectionStrings:OrionDb"]=connectionString;
builder.Configuration["AllowedHosts"]=$"{instance.CanonicalHost};www.{instance.CanonicalHost};localhost;127.0.0.1";
if (!builder.Environment.IsDevelopment())
  builder.WebHost.ConfigureKestrel(options=>options.Listen(IPAddress.Loopback,instance.LoopbackPort));

builder.Services.AddPublicWebsiteInstance(connectionString,instance,presentation);
var app=builder.Build();
app.UseConfiguredPublicWebsite();
app.MapGet("/healthz",()=>Results.Text("OK","text/plain"));
app.MapGet("/readyz",async (IPublicWebsiteInstanceContext website,IOrionSqlSessionFactory sessions,CancellationToken ct)=>
{
  try
  {
    var binding=await website.ResolveRequiredAsync(ct);
    await using var connection=await sessions.OpenAsync(PlatformExecutionScope.FromPublicSite(binding),ct);
    var enabled=await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
      "SELECT IsWebsiteEnabled FROM restaurante.PublicSiteSettings WHERE PublicSiteId=@PublicSiteId;",
      new { binding.PublicSiteId },cancellationToken:ct));
    return enabled?Results.Text("OK","text/plain"):Results.Text("NOT READY","text/plain",statusCode:503);
  }
  catch { return Results.Text("NOT READY","text/plain",statusCode:503); }
});
app.MapGet("/api/catalog",async (IPublicWebsiteInstanceContext website,IOrionSqlSessionFactory sessions,CancellationToken ct)=>
{
  var binding=await website.ResolveRequiredAsync(ct);
  await using var connection=await sessions.OpenAsync(PlatformExecutionScope.FromPublicSite(binding),ct);
  var localSiteId=await connection.ExecuteScalarAsync<int>(new CommandDefinition(
    "SELECT Id FROM restaurante.Site WHERE OrionCompanyId=@CompanyId AND OrionSiteId=@SiteId AND IsEnabled=1;",
    new { binding.CompanyId,binding.SiteId },cancellationToken:ct));
  var menus=(await connection.QueryAsync<RestaurantMenuSummary>(new CommandDefinition(
    "SELECT Id,MenuCode,[Name] FROM restaurante.Menu WHERE Rfc=@CompanyRfc AND IsActive=1 AND IsPublished=1 ORDER BY [Name];",
    new { binding.CompanyRfc },cancellationToken:ct))).AsList();
  var promotions=(await connection.QueryAsync<RestaurantPromotionSummary>(new CommandDefinition("""
    SELECT Id,[Name],PublicDescription,PublicTerms,ValidFromLocal,ValidToLocal
    FROM restaurante.Promotion
    WHERE Rfc=@CompanyRfc AND (SiteId IS NULL OR SiteId=@LocalSiteId) AND [Status] IN('Active','Scheduled')
      AND WebEnabled=1 AND IsPublic=1 ORDER BY Priority DESC,Id;
    """,new { binding.CompanyRfc,LocalSiteId=localSiteId },cancellationToken:ct))).AsList();
  return Results.Ok(new { binding.PublicSiteKey,SiteId=localSiteId,Menus=menus,Promotions=promotions });
});
app.MapGet("/",()=>Results.Content($$"""
  <!doctype html><html lang="{{WebUtility.HtmlEncode(presentation.Locale)}}"><head><meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1"><title>{{WebUtility.HtmlEncode(presentation.PublicName)}}</title>
  <style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:{{presentation.PrimaryDarkColor}};color:#fff;font:18px/1.6 system-ui}main{max-width:48rem;padding:3rem}a{color:{{presentation.AccentColor}}}</style></head>
  <body><main><p>{{WebUtility.HtmlEncode(presentation.LocationName)}}</p><h1>{{WebUtility.HtmlEncode(presentation.PublicName)}}</h1>
  <p>{{WebUtility.HtmlEncode(presentation.Tagline)}}</p><a href="/api/catalog">Consultar catálogo</a></main></body></html>
  ""","text/html"));
app.Run();

public sealed record RestaurantMenuSummary(long Id,string MenuCode,string Name);
public sealed record RestaurantPromotionSummary(long Id,string Name,string PublicDescription,string PublicTerms,DateTime? ValidFromLocal,DateTime? ValidToLocal);
public partial class Program { }
