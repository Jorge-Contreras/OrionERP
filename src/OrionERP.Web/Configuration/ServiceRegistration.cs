using Microsoft.Extensions.DependencyInjection;
using OrionERP.Application.Features.Cfdi.CargarXmlSat;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts;
using OrionERP.Application.Features.Cfdi.ContabilidadRegistros;
using OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts;
using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.Infrastructure.Features.Cfdi.CargarXmlSat;
using OrionERP.Infrastructure.Features.Cfdi.CargarXmlSat.Services;
using OrionERP.Infrastructure.Features.Cfdi.ContabilidadRegistros;
using OrionERP.Infrastructure.Feautures.Cfdi.CargarXmlSat.Services;
using OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Dapper;
using OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Services;
using OrionERP.Infrastructure.Features.Contabilidad.Transacciones.Services;
using OrionERP.Infrastructure.Common;
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
    services.AddSingleton<IDbConnectionFactory>(sp => sp.GetRequiredService<SqlConnectionFactory>());
    services.AddScoped<IDbStoredProcService, DbStoredProcService>();
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
    services.AddSingleton<IDbConnectionFactory>(sp => sp.GetRequiredService<SqlConnectionFactory>());
    services.AddScoped<IDbStoredProcService, DbStoredProcService>();
    services.AddScoped<ISatSolicitudesRepository, SatSolicitudesRepository>();
    services.AddScoped<ISatPaquetesRepository, SatPaquetesRepository>();
    services.AddScoped<ISatDownloadCoordinator, SatDownloadCoordinator>();

    services.AddScoped<ICuentasContablesRepository, CuentasContablesRepository>();
    services.AddScoped<IContabilidadRegistrosService, ContabilidadRegistrosService>();

    services.AddScoped<ITransaccionService, TransaccionService>();


    return services;
  }



}


