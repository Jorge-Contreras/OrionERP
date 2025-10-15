using Microsoft.Extensions.DependencyInjection;
using OrionERP.Application.Features.Cfdi.CargarXmlSat;
using OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts;
using OrionERP.Infrastructure.Features.Cfdi.CargarXmlSat;
using OrionERP.Infrastructure.Features.Cfdi.CargarXmlSat.Services;
using OrionERP.Infrastructure.Feautures.Cfdi.CargarXmlSat.Services;

namespace OrionERP.Web.Configuration;

public static class ServiceRegistration
{
  public static IServiceCollection AddCfdiCargarXmlSat(this IServiceCollection services)
  {
    // Application contracts are interfaces; Infrastructure has concrete impls
    services.AddScoped<ITransaccionQueryService, TransaccionQueryService>();
    services.AddScoped<IComprobanteQueryService, ComprobanteQueryService>();
    services.AddScoped<ISatXmlInboxService, SatXmlInboxService>();
    services.AddScoped<IConciliacionService, ConciliacionService>();
    return services;
  }
}
