using Microsoft.Extensions.DependencyInjection;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts;
using OrionERP.Application.Features.Cfdi.ContabilidadRegistros;
using OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts;
using OrionERP.Application.Features.Cfdi.HtmlCFDI;
using OrionERP.Application.Features.Rfcs.Contracts;
using OrionERP.Application.Features.Contabilidad.Bancos;
using OrionERP.Infrastructure.Common;
using OrionERP.Infrastructure.Features.Cfdi.CargarXmlSat.Services;
using OrionERP.Infrastructure.Features.Cfdi.ContabilidadRegistros;
using OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Dapper;
using OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Services;
using OrionERP.Infrastructure.Features.Cfdi.HtmlCFDI;
using OrionERP.Infrastructure.Features.Rfcs.Dapper;
using OrionERP.Infrastructure.Features.Contabilidad.Bancos;
using OrionERP.Application.Features.ReportesFinancieros;
using OrionERP.Infrastructure.Features.ReportesFinancieros.Dapper;
using OrionERP.Application.Features.Cfdi.DeclaracionPrevia.Interfaces;
using OrionERP.Infrastructure.Features.Cfdi.DeclaracionPrevia.Services;
using Sat.MassiveDownload;
using Sat.MassiveDownload.Core;
using ContabITransaccionService = OrionERP.Application.Features.Contabilidad.Transacciones.ITransaccionService;
using ContabTransaccionService = OrionERP.Infrastructure.Features.Contabilidad.Transacciones.Services.TransaccionService;

namespace OrionERP.Web.Configuration;

public static class ServiceRegistration
{
  public static IServiceCollection AddCfdiCargarXmlSat(this IServiceCollection services)
  {
    services.AddHttpClient<ISatMassiveService, SatMassiveClient>();
    services.AddScoped<ISatRfcProfileRepository, SatRfcProfileRepository>();

    services.AddSingleton<SqlConnectionFactory>();
    services.AddSingleton<IDbConnectionFactory>(sp => sp.GetRequiredService<SqlConnectionFactory>());
    services.AddScoped<IDbStoredProcService, DbStoredProcService>();

    services.AddScoped<IComprobanteQueryService, ComprobanteQueryService>();
    services.AddScoped<ISatXmlInboxService, SatXmlInboxService>();
    services.AddScoped<IConciliacionService, ConciliacionService>();
    services.AddScoped<ContabITransaccionService, ContabTransaccionService>();
    services.AddScoped<CfdiReadableParser>();
    services.AddScoped<ITransactionAttachmentRepository, TransactionAttachmentRepository>();
    services.AddScoped<IHtmlCfdiService, HtmlCfdiService>();

    services.AddScoped<ISatMetadataIngestService, SatMetadataIngestService>();

    return services;
  }

  public static IServiceCollection AddOrionServices(this IServiceCollection services)
  {
    services.AddHttpClient<ISatMassiveService, SatMassiveClient>();

    services.AddSingleton<SqlConnectionFactory>();
    services.AddSingleton<IDbConnectionFactory>(sp => sp.GetRequiredService<SqlConnectionFactory>());
    services.AddScoped<IDbStoredProcService, DbStoredProcService>();
    services.AddScoped<ISatSolicitudesRepository, SatSolicitudesRepository>();
    services.AddScoped<ISatPaquetesRepository, SatPaquetesRepository>();
    services.AddScoped<ISatDownloadCoordinator, SatDownloadCoordinator>();

    services.AddScoped<ICuentasContablesRepository, CuentasContablesRepository>();
    services.AddScoped<IContabilidadRegistrosService, ContabilidadRegistrosService>();
    services.AddScoped<IBancosService, BancosService>();
    services.AddScoped<IReportesFinancierosService, ReportesFinancierosService>();

    services.AddScoped<ContabITransaccionService, ContabTransaccionService>();
    services.AddScoped<CfdiReadableParser>();
    services.AddScoped<ITransactionAttachmentRepository, TransactionAttachmentRepository>();
    services.AddScoped<IHtmlCfdiService, HtmlCfdiService>();

    services.AddScoped<IDeclaracionPreviaService, DeclaracionPreviaService>();

    return services;
  }
}
