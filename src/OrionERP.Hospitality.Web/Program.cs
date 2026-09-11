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
    ?? new PublicWebsiteInstanceOptions(),PlatformModuleCodes.Hospitality);
var presentation=PublicWebsitePresentationPolicy.Create(
  builder.Configuration.GetSection(PublicWebsitePresentationOptions.SectionName).Get<PublicWebsitePresentationOptions>()
    ?? new PublicWebsitePresentationOptions(),instance);
if (args.Any(value=>string.Equals(value,"--validate-instance-profile=true",StringComparison.OrdinalIgnoreCase)))
{
  Console.WriteLine("Hospitality generic instance profile validated successfully.");
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
    var rooms=await connection.ExecuteScalarAsync<int>(new CommandDefinition(
      "SELECT COUNT(*) FROM dbo.ROOM WHERE IsActive=1 AND IsRentable=1;",cancellationToken:ct));
    return rooms>0?Results.Text("OK","text/plain"):Results.Text("NOT READY","text/plain",statusCode:503);
  }
  catch { return Results.Text("NOT READY","text/plain",statusCode:503); }
});
app.MapGet("/api/availability",async (
  DateOnly? from,DateOnly? to,IPublicWebsiteInstanceContext website,IOrionSqlSessionFactory sessions,CancellationToken ct)=>
{
  var start=from??DateOnly.FromDateTime(DateTime.UtcNow);
  var end=to??start.AddDays(1);
  if (end<=start || end.DayNumber-start.DayNumber>31) return Results.BadRequest(new { error="invalid-date-range" });
  var binding=await website.ResolveRequiredAsync(ct);
  await using var connection=await sessions.OpenAsync(PlatformExecutionScope.FromPublicSite(binding),ct);
  var rooms=(await connection.QueryAsync<HospitalityRoomAvailability>(new CommandDefinition("""
    SELECT room.ID Id,room.ROOM_NAME [Name],room.ROOM_TYPE [Type],CONVERT(decimal(18,2),room.BASE_PRICE) BasePrice,
      CONVERT(bit,CASE WHEN EXISTS
      (
        SELECT 1 FROM dbo.ROOM_CALENDAR calendar
        WHERE calendar.RoomId=room.ID AND calendar.ROOM_DATE>=@Start AND calendar.ROOM_DATE<@End
          AND (calendar.IS_LOCKED=1 OR calendar.ReservationId IS NOT NULL)
      ) THEN 0 ELSE 1 END) IsAvailable
    FROM dbo.ROOM room WHERE room.IsActive=1 AND room.IsRentable=1 ORDER BY room.ROOM_NAME;
    """,new { Start=start.ToDateTime(TimeOnly.MinValue),End=end.ToDateTime(TimeOnly.MinValue) },cancellationToken:ct))).AsList();
  return Results.Ok(new { binding.PublicSiteKey,From=start,To=end,Rooms=rooms });
});
app.MapGet("/",()=>Results.Content($$"""
  <!doctype html><html lang="{{WebUtility.HtmlEncode(presentation.Locale)}}"><head><meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1"><title>{{WebUtility.HtmlEncode(presentation.PublicName)}}</title>
  <style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:{{presentation.PrimaryDarkColor}};color:#fff;font:18px/1.6 system-ui}main{max-width:48rem;padding:3rem}a{color:{{presentation.AccentColor}}}</style></head>
  <body><main><p>{{WebUtility.HtmlEncode(presentation.LocationName)}}</p><h1>{{WebUtility.HtmlEncode(presentation.PublicName)}}</h1>
  <p>{{WebUtility.HtmlEncode(presentation.Tagline)}}</p><a href="/api/availability">Consultar disponibilidad</a></main></body></html>
  ""","text/html"));
app.Run();

public sealed record HospitalityRoomAvailability(int Id,string Name,string Type,decimal? BasePrice,bool IsAvailable);
public partial class Program { }
