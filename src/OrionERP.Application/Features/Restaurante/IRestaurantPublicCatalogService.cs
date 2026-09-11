using OrionERP.Application.Features.Platform;

namespace OrionERP.Application.Features.Restaurante;

public interface IRestaurantPublicCatalogService
{
  Task<RestaurantPublicCatalogDto?> GetCatalogAsync(
    PublicSiteBinding binding,
    DateTimeOffset at,
    CancellationToken ct = default);

  Task<RestaurantPublicSiteSettingsDto?> GetSettingsAsync(
    PublicSiteBinding binding,
    CancellationToken ct = default);

  Task<(byte[] Bytes, string ContentType)?> GetProductImageAsync(
    PublicSiteBinding binding,
    long productId,
    bool thumbnail,
    CancellationToken ct = default);

  Task<RestaurantPublicSiteSettingsDto?> GetSettingsAsync(
    string rfc,
    int siteId,
    CancellationToken ct = default);

  Task<RestaurantCommandResult> SaveSettingsAsync(
    RestaurantPublicSiteSettingsSaveRequest request,
    string userName,
    CancellationToken ct = default);
}
