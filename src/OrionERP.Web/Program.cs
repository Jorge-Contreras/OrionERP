using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using OfficeOpenXml;
using OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts;
using OrionERP.Infrastructure.Features.Cfdi.CargarXmlSat.Services;
using OrionERP.Web.Configuration;
using OrionERP.Web.Data;


var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddSingleton<WeatherForecastService>();
builder.Services.AddCfdiCargarXmlSat(); // neat 1-liner per feature
var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();


//SET OFFICEOPENXML LICENSE CONTEXT

ExcelPackage.License.SetNonCommercialOrganization("Orion Habitat de Mexico S.A. de C.V.");
