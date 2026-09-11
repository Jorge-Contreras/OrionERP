namespace OrionERP.Application.Features.Restaurante;

public interface IBrunoPublicCatalogService
{
  Task<BrunoPublicCatalogDto?> GetCatalogAsync(
    string rfc,
    string siteCode,
    DateTimeOffset at,
    CancellationToken ct = default);

  Task<BrunoPublicSiteSettingsDto?> GetSettingsAsync(
    string rfc,
    int siteId,
    CancellationToken ct = default);

  Task<BrunoPublicSiteSettingsDto?> GetSettingsAsync(
    string rfc,
    string siteCode,
    CancellationToken ct = default);

  Task<RestaurantCommandResult> SaveSettingsAsync(
    BrunoPublicSiteSettingsSaveRequest request,
    string userName,
    CancellationToken ct = default);
}
