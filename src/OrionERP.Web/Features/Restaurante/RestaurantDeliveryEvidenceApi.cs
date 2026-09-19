using Microsoft.Net.Http.Headers;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Web.Features.Restaurante;

public static class RestaurantDeliveryEvidenceApi
{
  public static IEndpointRouteBuilder MapRestaurantDeliveryEvidenceApi(this IEndpointRouteBuilder endpoints)
  {
    endpoints.MapGet("/api/restaurante/entregas/evidencia/{evidenceId:long}", GetEvidenceAsync)
      .RequireAuthorization("RestaurantDelivery");
    return endpoints;
  }

  private static async Task<IResult> GetEvidenceAsync(
    long evidenceId,
    HttpContext context,
    ICurrentCompanyContext companyContext,
    IRestaurantDeliveryService deliveryService,
    CancellationToken ct)
  {
    var userName = context.User.Identity?.Name;
    if (string.IsNullOrWhiteSpace(userName)) return Results.Unauthorized();
    var canSupervise = context.User.IsInRole("RestauranteSupervisor")
      || context.User.IsInRole("RestauranteAdmin")
      || context.User.IsInRole("Administrador");
    var evidence = await deliveryService.GetEvidenceAsync(
      companyContext.RequireRfc(), evidenceId, userName, canSupervise, ct);
    if (evidence is null) return Results.NotFound();
    context.Response.Headers.CacheControl = "private, no-store, max-age=0";
    context.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
    return Results.File(evidence.Bytes, evidence.ContentType, enableRangeProcessing: false);
  }
}
