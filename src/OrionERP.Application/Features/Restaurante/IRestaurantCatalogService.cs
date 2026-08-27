namespace OrionERP.Application.Features.Restaurante;

public interface IRestaurantCatalogService
{
  Task<IReadOnlyList<RestaurantSiteDto>> GetSitesAsync(string rfc, CancellationToken ct = default);
  Task<RestaurantCommandResult> SaveSiteAsync(RestaurantSiteUpsertRequest request, CancellationToken ct = default);
  Task<IReadOnlyList<RestaurantProductDto>> GetProductsAsync(string rfc, int? siteId = null, CancellationToken ct = default);
  Task<RestaurantCommandResult> SaveProductAsync(RestaurantProductUpsertRequest request, CancellationToken ct = default);
  Task<RestaurantPosCatalogDto> GetPosCatalogAsync(string rfc, int siteId, DateTimeOffset at, CancellationToken ct = default);
  Task<(byte[] Bytes, string ContentType)?> GetProductImageAsync(string rfc, long productId, bool thumbnail, CancellationToken ct = default);
  Task<IReadOnlyList<RestaurantMenuAdminDto>> GetMenusAsync(string rfc, CancellationToken ct = default);
  Task<RestaurantCommandResult> SaveMenuAsync(RestaurantMenuSaveRequest request, CancellationToken ct = default);
  Task<IReadOnlyList<RestaurantModifierAdminDto>> GetModifierGroupsAsync(string rfc, CancellationToken ct = default);
  Task<RestaurantCommandResult> SaveModifierGroupAsync(RestaurantModifierSaveRequest request, CancellationToken ct = default);
  Task<IReadOnlyList<RestaurantKitchenStationLookupDto>> GetKitchenStationsAsync(string rfc, CancellationToken ct = default);
  Task<RestaurantSiteOperationsDto> GetSiteOperationsAsync(string rfc, int siteId, CancellationToken ct = default);
  Task<RestaurantCommandResult> SaveSiteOperationsAsync(RestaurantSiteOperationsSaveRequest request, CancellationToken ct = default);
}
