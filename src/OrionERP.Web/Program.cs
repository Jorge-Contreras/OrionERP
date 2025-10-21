using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OfficeOpenXml;
using OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts;
using OrionERP.Infrastructure.Auth;
using OrionERP.Infrastructure.Features.Cfdi.CargarXmlSat.Services;
using OrionERP.Web.Configuration;
using OrionERP.Web.Data;
using OrionERP.Web.Features.Cfdi.DescargaMasiva;
using OrionERP.Web.Identity;

var builder = WebApplication.CreateBuilder(args);
ExcelPackage.License.SetNonCommercialOrganization("Orion Habitat de Mexico S.A. de C.V.");

builder.Services.AddDbContext<OrionIdentityDbContext>(opt =>
    opt.UseSqlServer(builder.Configuration.GetConnectionString("OrionDb")));

builder.Services
    .AddDefaultIdentity<ApplicationUser>(o =>
    {
        o.SignIn.RequireConfirmedAccount = false;
        o.Password.RequiredLength = 8;
        o.Lockout.MaxFailedAccessAttempts = 5;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<OrionIdentityDbContext>();

builder.Services.AddScoped<IRfcContext, RfcContext>();
builder.Services.AddScoped<IAuthorizationHandler, RoleForRfcHandler>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        "RoleForSelectedRfc",
        policy => policy.Requirements.Add(new RoleForRfcRequirement("Administrador")));
});

builder.Services.AddScoped<AuthenticationStateProvider, RevalidatingIdentityAuthenticationStateProvider<ApplicationUser>>();

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddSingleton<WeatherForecastService>();
builder.Services.AddCfdiCargarXmlSat();
builder.Services.AddOrionServices();
builder.Services.Configure<SatIntegrationOptions>(builder.Configuration.GetSection("SatIntegration"));

var app = builder.Build();

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

using (var scope = app.Services.CreateScope())
{
    await IdentitySeeder.RunAsync(scope.ServiceProvider);
    // await CapitalHumanoImporter.ImportAsync(scope.ServiceProvider);
}

app.Run();
