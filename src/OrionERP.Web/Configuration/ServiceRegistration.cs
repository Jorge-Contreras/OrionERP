using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity.UI.Services;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Auth.AdminPortal;
using OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts;
using OrionERP.Application.Features.Cfdi.DeclaracionPrevia;
using OrionERP.Application.Features.Cfdi.Facturama;
using OrionERP.Application.Features.Cfdi.HtmlCFDI;
using OrionERP.Application.Features.Rfcs.Contracts;
using OrionERP.Application.Features.Contabilidad.Bancos;
using OrionERP.Application.Features.Contabilidad.ContabilidadRegistros;
using OrionERP.Infrastructure.Common;
using OrionERP.Infrastructure.Features.Auth.AdminPortal;
using OrionERP.Infrastructure.Features.Cfdi.DeclaracionPrevia;
using OrionERP.Infrastructure.Features.Cfdi.Facturama;
using OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Dapper;
using OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Services;
using OrionERP.Infrastructure.Features.Cfdi.HtmlCFDI;
using OrionERP.Infrastructure.Features.Rfcs.Dapper;
using OrionERP.Infrastructure.Features.Contabilidad.Bancos;
using OrionERP.Infrastructure.Features.Contabilidad.ContabilidadRegistros;
using OrionERP.Application.Features.ReportesFinancieros;
using OrionERP.Application.Features.Reservaciones.CalendarSync;
using OrionERP.Application.Features.Reservaciones.OpenClaw;
using OrionERP.Application.Features.Logistica.BusinessPartners;
using OrionERP.Application.Features.Logistica.Locations;
using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Application.Features.Logistica.PhysicalCounts;
using OrionERP.Application.Features.Logistica.Stock;
using OrionERP.Infrastructure.Features.ReportesFinancieros.Dapper;
using OrionERP.Infrastructure.Features.Logistica.BusinessPartners;
using OrionERP.Infrastructure.Features.Logistica.Locations;
using OrionERP.Infrastructure.Features.Logistica.Materials;
using OrionERP.Infrastructure.Features.Logistica.PhysicalCounts;
using OrionERP.Infrastructure.Features.Logistica.Stock;
using OrionERP.Infrastructure.Features.Reservaciones.CalendarSync;
using OrionERP.Web.Features.Reservaciones.OpenClaw;
using OrionERP.Web.Features.Reservaciones.ListaReservaciones;
using OrionERP.Web.Identity;
using Sat.MassiveDownload;
using Sat.MassiveDownload.Core;
using ContabITransaccionService = OrionERP.Application.Features.Contabilidad.Transacciones.ITransaccionService;
using ContabTransaccionService = OrionERP.Infrastructure.Features.Contabilidad.Transacciones.Services.TransaccionService;
using OrionERP.Infrastructure.Features.Cfdi.CargarXmlSat.Services;
using OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts;
using ReservacionesIOpenClawReservationsService = OrionERP.Application.Features.Reservaciones.OpenClaw.IOpenClawReservationsService;
using ReservacionesIListaReservacionesService = OrionERP.Application.Features.Reservaciones.ListaReservaciones.IListaReservacionesService;
using ReservacionesListaReservacionesService = OrionERP.Infrastructure.Features.Reservaciones.ListaReservaciones.Services.ListaReservacionesService;

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
    services.AddScoped<ContabITransaccionService, ContabTransaccionService>();
    services.AddHttpClient<IFacturamaApiClient, FacturamaApiClient>();
    services.AddScoped<CfdiReadableParser>();
    services.AddScoped<ITransactionAttachmentRepository, TransactionAttachmentRepository>();
    services.AddScoped<IHtmlCfdiService, HtmlCfdiService>();
    services.AddScoped<ReservacionesIListaReservacionesService, ReservacionesListaReservacionesService>();
    services.AddScoped<IOutlookRoomCalendarSyncRepository, OutlookRoomCalendarSyncRepository>();
    services.AddHttpClient<IBonhomiaRoomCalendarSyncService, BonhomiaRoomCalendarSyncService>();
    services.AddScoped<IReservacionPdfService, ReservacionPdfService>();
    services.AddScoped<ReservacionesIOpenClawReservationsService, ReservacionesListaReservacionesService>();
    services.AddScoped<IReservacionPdfDocumentFactory, ReservacionPdfDocumentFactory>();
    services.AddSingleton<IOpenClawReservationPdfTokenService, OpenClawReservationPdfTokenService>();

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
    services.AddScoped<IDeclaracionPreviaService, DeclaracionPreviaService>();
    services.AddScoped<IBancosService, BancosService>();
    services.AddScoped<IIdentityAdminService, IdentityAdminService>();
    services.AddHttpClient<IEmailSender, MicrosoftGraphEmailSender>();
    services.AddScoped<IReportesFinancierosService, ReportesFinancierosService>();
    services.AddScoped<IBusinessPartnerService, BusinessPartnerService>();
    services.AddScoped<IMaterialService, MaterialService>();
    services.AddScoped<ILocationService, LocationService>();
    services.AddScoped<IStockService, StockService>();
    services.AddScoped<IPhysicalCountService, PhysicalCountService>();

    services.AddScoped<ContabITransaccionService, ContabTransaccionService>();
    services.AddHttpClient<IFacturamaApiClient, FacturamaApiClient>();
    services.AddScoped<CfdiReadableParser>();
    services.AddScoped<ITransactionAttachmentRepository, TransactionAttachmentRepository>();
    services.AddScoped<IHtmlCfdiService, HtmlCfdiService>();
    services.AddScoped<ReservacionesIListaReservacionesService, ReservacionesListaReservacionesService>();
    services.AddScoped<IOutlookRoomCalendarSyncRepository, OutlookRoomCalendarSyncRepository>();
    services.AddHttpClient<IBonhomiaRoomCalendarSyncService, BonhomiaRoomCalendarSyncService>();
    services.AddScoped<IReservacionPdfService, ReservacionPdfService>();
    services.AddScoped<ReservacionesIOpenClawReservationsService, ReservacionesListaReservacionesService>();
    services.AddScoped<IReservacionPdfDocumentFactory, ReservacionPdfDocumentFactory>();
    services.AddSingleton<IOpenClawReservationPdfTokenService, OpenClawReservationPdfTokenService>();

    return services;
  }
}
