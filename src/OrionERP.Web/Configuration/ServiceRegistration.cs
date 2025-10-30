using Microsoft.Extensions.DependencyInjection;
using OrionERP.Application.Features.Cfdi.CargarXmlSat;
using OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts;
using OrionERP.Application.Features.Cfdi.ContabilidadRegistros;
using OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts;
using OrionERP.Infrastructure.Features.Cfdi.CargarXmlSat;
using OrionERP.Infrastructure.Features.Cfdi.CargarXmlSat.Services;
using OrionERP.Infrastructure.Features.Cfdi.ContabilidadRegistros;
using OrionERP.Infrastructure.Feautures.Cfdi.CargarXmlSat.Services;
using OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Dapper;
using OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Services;
using Sat.MassiveDownload; // ISatMassiveService, SatMassiveClient
using Sat.MassiveDownload.Core;
using SatISvc = Sat.MassiveDownload.Core.ISatMassiveService;
using OrionERP.Application.Features.Rfcs.Contracts;
using OrionERP.Infrastructure.Features.Rfcs.Dapper;


namespace OrionERP.Web.Configuration;

public static class ServiceRegistration
{
  public static IServiceCollection AddCfdiCargarXmlSat(this IServiceCollection services)
  {
    services.AddHttpClient<SatISvc, Sat.MassiveDownload.SatMassiveClient>();
    services.AddScoped<ISatRfcProfileRepository, SatRfcProfileRepository>();

    services.AddSingleton<SqlConnectionFactory>();
    // Application contracts are interfaces; Infrastructure has concrete impls
    services.AddScoped<ITransaccionQueryService, TransaccionQueryService>();
    services.AddScoped<IComprobanteQueryService, ComprobanteQueryService>();
    services.AddScoped<ISatXmlInboxService, SatXmlInboxService>();
    services.AddScoped<IConciliacionService, ConciliacionService>();
    services.AddScoped<ISatMetadataIngestService, SatMetadataIngestService>();

    return services;
  }

  public static IServiceCollection AddOrionServices(this IServiceCollection services)
  {
    // ...your existing regs...

    // SAT client (from your Sat.MassiveDownload library)
    services.AddHttpClient<ISatMassiveService, SatMassiveClient>();

    // Dapper infra for DescargaMasiva
    services.AddSingleton<SqlConnectionFactory>();
    services.AddScoped<ISatSolicitudesRepository, SatSolicitudesRepository>();
    services.AddScoped<ISatPaquetesRepository, SatPaquetesRepository>();
    services.AddScoped<ISatDownloadCoordinator, SatDownloadCoordinator>();

    services.AddScoped<ICuentasContablesRepository, CuentasContablesRepository>();
    services.AddScoped<IContabilidadRegistrosService, ContabilidadRegistrosService>();

    return services;
  }



}


