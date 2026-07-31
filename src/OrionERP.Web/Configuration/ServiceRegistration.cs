using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity.UI.Services;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Arrendadores;
using OrionERP.Application.Features.Auth.AdminPortal;
using OrionERP.Application.Features.CapitalHumano;
using OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts;
using OrionERP.Application.Features.Cfdi.DeclaracionPrevia;
using OrionERP.Application.Features.Cfdi.Facturama;
using OrionERP.Application.Features.Cfdi.HtmlCFDI;
using OrionERP.Application.Features.CuentasPorPagar.Recurrentes;
using OrionERP.Application.Features.Rfcs.Contracts;
using OrionERP.Application.Features.Contabilidad.Bancos;
using OrionERP.Application.Features.Contabilidad.ContabilidadRegistros;
using OrionERP.Application.Features.Ajustes;
using OrionERP.Infrastructure.Common;
using OrionERP.Infrastructure.Features.Arrendadores;
using OrionERP.Infrastructure.Features.Ajustes;
using OrionERP.Infrastructure.Features.Auth.AdminPortal;
using OrionERP.Infrastructure.Features.CapitalHumano;
using OrionERP.Infrastructure.Features.Cfdi.DeclaracionPrevia;
using OrionERP.Infrastructure.Features.Cfdi.Facturama;
using OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Dapper;
using OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Services;
using OrionERP.Infrastructure.Features.Cfdi.HtmlCFDI;
using OrionERP.Infrastructure.Features.CuentasPorPagar.Recurrentes;
using OrionERP.Infrastructure.Features.Rfcs.Dapper;
using OrionERP.Infrastructure.Features.Contabilidad.Bancos;
using OrionERP.Infrastructure.Features.Contabilidad.ContabilidadRegistros;
using OrionERP.Application.Features.ReportesFinancieros;
using OrionERP.Application.Features.Reservaciones.Cfdi;
using OrionERP.Application.Features.Reservaciones.CalendarSync;
using OrionERP.Application.Features.Reservaciones.Experiencias;
using OrionERP.Application.Features.Reservaciones.OpenClaw;
using OrionERP.Application.Features.Logistica.BusinessPartners;
using OrionERP.Application.Features.Logistica.Locations;
using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Application.Features.Logistica.Purchasing;
using OrionERP.Application.Features.Logistica.PhysicalCounts;
using OrionERP.Application.Features.Logistica.Stock;
using OrionERP.Application.Features.Restaurante;
using OrionERP.Application.Features.OrdenesTrabajo;
using OrionERP.Infrastructure.Features.ReportesFinancieros.Dapper;
using OrionERP.Infrastructure.Features.Logistica.BusinessPartners;
using OrionERP.Infrastructure.Features.Logistica.Locations;
using OrionERP.Infrastructure.Features.Logistica.Materials;
using OrionERP.Infrastructure.Features.Logistica.Purchasing;
using OrionERP.Infrastructure.Features.Logistica.PhysicalCounts;
using OrionERP.Infrastructure.Features.Logistica.Stock;
using OrionERP.Infrastructure.Features.Restaurante;
using OrionERP.Infrastructure.Features.OrdenesTrabajo;
using OrionERP.Infrastructure.Features.Mail;
using OrionERP.Infrastructure.Features.Reservaciones.Cfdi;
using OrionERP.Infrastructure.Features.Reservaciones.CalendarSync;
using OrionERP.Infrastructure.Features.Reservaciones.Experiencias;
using OrionERP.Infrastructure.Features.Reservaciones.ListaReservaciones.Pdf;
using OrionERP.Web.Features.Arrendadores;
using OrionERP.Web.Features.Cfdi.HtmlCFDI;
using OrionERP.Web.Features.Logistica.Purchasing;
using OrionERP.Web.Features.ReportesFinancieros.SaludEmpresa;
using OrionERP.Web.Features.Restaurante;
using OrionERP.Web.Features.Reservaciones.OpenClaw;
using OrionERP.Web.Identity;
using OrionERP.Web.Services;
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

    services.AddScoped<SqlConnectionFactory>();
    services.AddScoped<IDbConnectionFactory>(sp => sp.GetRequiredService<SqlConnectionFactory>());
    services.AddScoped<IDbStoredProcService, DbStoredProcService>();
    services.AddScoped<ICurrentUserAccessor, AuthenticationStateCurrentUserAccessor>();

    services.AddScoped<IComprobanteQueryService, ComprobanteQueryService>();
    services.AddScoped<ISatXmlInboxService, SatXmlInboxService>();
    services.AddScoped<ContabITransaccionService, ContabTransaccionService>();
    services.AddHttpClient<IFacturamaApiClient, FacturamaApiClient>();
    services.AddScoped<ICfdiStampingService, CfdiStampingService>();
    services.AddScoped<CfdiReadableParser>();
    services.AddScoped<ITransactionAttachmentRepository, TransactionAttachmentRepository>();
    services.AddScoped<IHtmlCfdiService, HtmlCfdiService>();
    services.AddScoped<ICfdiPdfService, CfdiPdfService>();
    services.AddScoped<IRecurrentApService, RecurrentApService>();
    services.AddScoped<ReservacionesIListaReservacionesService, ReservacionesListaReservacionesService>();
    services.AddScoped<IReservacionExperiencesService, ReservacionExperiencesService>();
    services.AddScoped<IReservationCfdiService, ReservationCfdiService>();
    services.AddScoped<IOutlookRoomCalendarSyncRepository, OutlookRoomCalendarSyncRepository>();
    services.AddHttpClient<IBonhomiaRoomCalendarSyncService, BonhomiaRoomCalendarSyncService>();
    services.AddScoped<IReservacionPdfService, ReservacionPdfService>();
    services.AddScoped<IArrendadorEstadoCuentaPdfService, ArrendadorEstadoCuentaPdfService>();
    services.AddScoped<ISaludEmpresaPdfService, SaludEmpresaPdfService>();
    services.AddScoped<ReservacionesIOpenClawReservationsService, ReservacionesListaReservacionesService>();
    services.AddScoped<IReservacionPdfDocumentFactory, ReservacionPdfDocumentFactory>();
    services.AddSingleton<IOpenClawReservationPdfTokenService, OpenClawReservationPdfTokenService>();

    services.AddScoped<ISatMetadataIngestService, SatMetadataIngestService>();
    services.AddScoped<IArrendadoresEstadoCuentaService, ArrendadoresEstadoCuentaService>();

    return services;
  }

  public static IServiceCollection AddOrionServices(this IServiceCollection services)
  {
    services.AddHttpClient<ISatMassiveService, SatMassiveClient>();

    services.AddScoped<SqlConnectionFactory>();
    services.AddScoped<IDbConnectionFactory>(sp => sp.GetRequiredService<SqlConnectionFactory>());
    services.AddScoped<IDbStoredProcService, DbStoredProcService>();
    services.AddScoped<ICurrentUserAccessor, AuthenticationStateCurrentUserAccessor>();
    services.AddScoped<ISatRfcProfileRepository, SatRfcProfileRepository>();
    services.AddScoped<ISatSolicitudesRepository, SatSolicitudesRepository>();
    services.AddScoped<ISatPaquetesRepository, SatPaquetesRepository>();
    services.AddScoped<ISatDownloadCoordinator, SatDownloadCoordinator>();

    services.AddScoped<ICuentasContablesRepository, CuentasContablesRepository>();
    services.AddScoped<IAjustesService, AjustesService>();
    services.AddScoped<ICapitalHumanoService, CapitalHumanoService>();
    services.AddScoped<IContabilidadRegistrosService, ContabilidadRegistrosService>();
    services.AddScoped<IDeclaracionPreviaService, DeclaracionPreviaService>();
    services.AddScoped<IBancosService, BancosService>();
    services.AddScoped<IRecurrentApService, RecurrentApService>();
    services.AddScoped<IIdentityAdminService, IdentityAdminService>();
    services.AddHttpClient<IMicrosoftGraphMailClient<GraphMailOptions>, MicrosoftGraphMailClient<GraphMailOptions>>();
    services.AddScoped<IEmailSender, MicrosoftGraphEmailSender>();
    services.AddScoped<IReportesFinancierosService, ReportesFinancierosService>();
    services.AddScoped<IBusinessPartnerService, BusinessPartnerService>();
    services.AddScoped<IMaterialService, MaterialService>();
    services.AddScoped<ILocationService, LocationService>();
    services.AddScoped<IPurchaseOrderService, PurchaseOrderService>();
    services.AddScoped<IStockService, StockService>();
    services.AddScoped<IInventoryMovementService, InventoryMovementService>();
    services.AddScoped<IPhysicalCountService, PhysicalCountService>();
    services.AddScoped<IRestaurantCatalogService, RestaurantCatalogService>();
    services.AddScoped<IBomRecipeService, BomRecipeService>();
    services.AddScoped<IRestaurantOrderService, RestaurantOrderService>();
    services.AddScoped<IRestaurantPromotionService, RestaurantPromotionService>();
    services.AddScoped<ILoyaltyService, LoyaltyService>();
    services.AddScoped<IBrunoMemberService>(sp => sp.GetRequiredService<ILoyaltyService>());
    services.AddScoped<IBrunoPublicCatalogService, BrunoPublicCatalogService>();
    services.AddScoped<IRestaurantCashService, RestaurantCashService>();
    services.AddScoped<IRestaurantProductionService, RestaurantProductionService>();
    services.AddScoped<IRestaurantBackofficeService, RestaurantBackofficeService>();
    services.AddScoped<IRestaurantAccountingService, RestaurantAccountingService>();
    services.AddScoped<IRestaurantQuickPinService, RestaurantQuickPinService>();
    services.AddScoped<IOrdenTrabajoService, OrdenTrabajoService>();
    services.AddScoped<IArrendadoresEstadoCuentaService, ArrendadoresEstadoCuentaService>();
    services.AddScoped<IPurchaseMaterialThumbnailHydrator, PurchaseMaterialThumbnailHydrator>();
    services.AddScoped<IPurchaseOrderPdfDocumentFactory, PurchaseOrderPdfDocumentFactory>();
    services.AddScoped<IPurchaseOrderPdfService, PurchaseOrderPdfService>();
    services.AddScoped<IRestaurantReceiptPdfService, RestaurantReceiptPdfService>();
    services.AddSingleton<IRestaurantQzTraySigningService, RestaurantQzTraySigningService>();

    services.AddScoped<ContabITransaccionService, ContabTransaccionService>();
    services.AddHttpClient<IFacturamaApiClient, FacturamaApiClient>();
    services.AddScoped<ICfdiStampingService, CfdiStampingService>();
    services.AddScoped<CfdiReadableParser>();
    services.AddScoped<ITransactionAttachmentRepository, TransactionAttachmentRepository>();
    services.AddScoped<IHtmlCfdiService, HtmlCfdiService>();
    services.AddScoped<ICfdiPdfService, CfdiPdfService>();
    services.AddScoped<ReservacionesIListaReservacionesService, ReservacionesListaReservacionesService>();
    services.AddScoped<IReservacionExperiencesService, ReservacionExperiencesService>();
    services.AddScoped<IReservationCfdiService, ReservationCfdiService>();
    services.AddScoped<IOutlookRoomCalendarSyncRepository, OutlookRoomCalendarSyncRepository>();
    services.AddHttpClient<IBonhomiaRoomCalendarSyncService, BonhomiaRoomCalendarSyncService>();
    services.AddScoped<IReservacionPdfService, ReservacionPdfService>();
    services.AddScoped<IArrendadorEstadoCuentaPdfService, ArrendadorEstadoCuentaPdfService>();
    services.AddScoped<ISaludEmpresaPdfService, SaludEmpresaPdfService>();
    services.AddScoped<ReservacionesIOpenClawReservationsService, ReservacionesListaReservacionesService>();
    services.AddScoped<IReservacionPdfDocumentFactory, ReservacionPdfDocumentFactory>();
    services.AddSingleton<IOpenClawReservationPdfTokenService, OpenClawReservationPdfTokenService>();

    return services;
  }
}
