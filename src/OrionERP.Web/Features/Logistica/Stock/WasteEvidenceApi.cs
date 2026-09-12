using OrionERP.Application.Common;
using OrionERP.Infrastructure.Features.Logistica.Stock;

namespace OrionERP.Web.Features.Logistica.Stock;

/// <summary>
/// Sirve la evidencia de una merma por HTTP. Una foto de hasta 10 MB no tiene por qué viajar
/// por el circuito de Blazor sólo para que el supervisor la vea antes de aprobar.
/// </summary>
public static class WasteEvidenceApi
{
  public static IEndpointRouteBuilder MapWasteEvidenceApi(this IEndpointRouteBuilder endpoints)
  {
    endpoints
      .MapGet("/api/logistica/merma/{adjustmentId:long}/evidencia", GetEvidenceAsync)
      .RequireAuthorization("RestaurantWaste");

    return endpoints;
  }

  private static async Task<IResult> GetEvidenceAsync(
    long adjustmentId,
    ICurrentCompanyContext companyContext,
    IDbConnectionFactory connectionFactory,
    CancellationToken ct)
  {
    // El navegador pide esta ruta con un fetch normal, fuera del circuito de Blazor que sostiene
    // el alcance de Hospedaje: IHospitalityScopeAccessor lee el estado de autenticación del
    // componente y aquí revienta. Se arma el servicio sin ese alcance a propósito. Es seguro
    // porque sin contexto de Hospedaje la visibilidad de ubicaciones sólo se estrecha —las
    // ubicaciones ligadas a una habitación quedan fuera—, nunca se amplía, y el RFC lo sigue
    // imponiendo la sesión.
    var evidence = await new WasteService(connectionFactory)
      .GetEvidenceAsync(companyContext.RequireRfc(), adjustmentId, ct);
    return evidence is null
      ? Results.NotFound()
      : Results.File(evidence.Content, evidence.ContentType, enableRangeProcessing: false);
  }
}
