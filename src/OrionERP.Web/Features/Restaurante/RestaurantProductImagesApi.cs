using OrionERP.Application.Common;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Web.Features.Restaurante;

public static class RestaurantProductImagesApi
{
  public static IEndpointRouteBuilder MapRestaurantProductImagesApi(this IEndpointRouteBuilder endpoints)
  {
    endpoints
      .MapGet("/api/restaurant/products/{productId:long}/thumbnail", GetThumbnailAsync)
      .RequireAuthorization("RestaurantPos");

    return endpoints;
  }

  private static async Task<IResult> GetThumbnailAsync(
    long productId,
    ICurrentCompanyContext companyContext,
    IRestaurantCatalogService catalogService,
    CancellationToken ct)
  {
    var image = await catalogService.GetProductImageAsync(companyContext.RequireRfc(), productId, true, ct);
    return image.HasValue
      ? Results.File(image.Value.Bytes, image.Value.ContentType, enableRangeProcessing: false)
      : Results.NotFound();
  }
}
