using Microsoft.Net.Http.Headers;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Web.Features.Restaurante;

public static class RestaurantSignageApi
{
  public static IEndpointRouteBuilder MapRestaurantSignageApi(this IEndpointRouteBuilder endpoints)
  {
    // Las dos rutas públicas viven bajo /menus/{rfc}/... para que el parámetro de
    // ruta se llame «rfc» y CompanyScopeGuardMiddleware bloquee a un usuario
    // autenticado que intente ver el tablero de otra empresa. Las peticiones
    // anónimas (las televisiones) pasan de largo por diseño.
    endpoints
      .MapGet("/menus/{rfc}/media/{imageId:long}", GetPublicImageAsync)
      .AllowAnonymous()
      .RequireRateLimiting("menu-signage");

    endpoints
      .MapGet("/menus/{rfc}/{screenKey}/manifest.json", GetManifestAsync)
      .AllowAnonymous()
      .RequireRateLimiting("menu-signage");

    endpoints
      .MapGet("/api/restaurant/signage/images/{imageId:long}/thumbnail", GetThumbnailAsync)
      .RequireAuthorization("RestaurantAdmin");

    return endpoints;
  }

  private static async Task<IResult> GetPublicImageAsync(
    string rfc,
    long imageId,
    string? v,
    IRestaurantSignageService signageService,
    HttpContext context,
    CancellationToken ct)
  {
    var image = await signageService.GetPublicImageAsync(rfc, imageId, ct);
    if (image is null) return Results.NotFound();

    var payload = image.Value;
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";

    // La URL que entrega el manifiesto lleva ?v={hash}, así que cuando coincide el
    // contenido es direccionable y la TV puede cachearlo indefinidamente. Sin ese
    // parámetro se revalida seguido para no congelar un tablero recién sustituido.
    context.Response.Headers[HeaderNames.CacheControl] =
      string.Equals(v, payload.ContentHash, StringComparison.OrdinalIgnoreCase)
        ? "public, max-age=31536000, immutable"
        : "public, max-age=60, must-revalidate";

    return Results.File(
      payload.Bytes,
      payload.ContentType,
      lastModified: null,
      entityTag: new EntityTagHeaderValue($"\"{payload.ContentHash}\""),
      enableRangeProcessing: false);
  }

  private static async Task<IResult> GetManifestAsync(
    string rfc,
    string screenKey,
    IRestaurantSignageService signageService,
    HttpContext context,
    CancellationToken ct)
  {
    var screen = await signageService.GetPublicScreenAsync(rfc, screenKey, ct);
    if (screen is null) return Results.NotFound();

    context.Response.Headers[HeaderNames.CacheControl] = "no-store";
    return Results.Ok(new
    {
      name = screen.Name,
      screenKey = screen.ScreenKey,
      intervalMs = screen.RotationSeconds * 1000,
      transitionMs = screen.TransitionMs,
      refreshMs = screen.RefreshSeconds * 1000,
      images = screen.Images.Select(image => new
      {
        id = image.Id,
        v = image.ContentHash,
        alt = image.AltText ?? screen.Name,
        url = $"/menus/{screen.Rfc}/media/{image.Id}?v={image.ContentHash}"
      })
    });
  }

  private static async Task<IResult> GetThumbnailAsync(
    long imageId,
    ICurrentCompanyContext companyContext,
    IRestaurantSignageService signageService,
    CancellationToken ct)
  {
    var image = await signageService.GetImageThumbnailAsync(companyContext.RequireRfc(), imageId, ct);
    return image is not null
      ? Results.File(image.Value.Bytes, image.Value.ContentType, enableRangeProcessing: false)
      : Results.NotFound();
  }
}
