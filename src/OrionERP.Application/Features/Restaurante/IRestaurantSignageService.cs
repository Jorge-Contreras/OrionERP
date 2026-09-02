namespace OrionERP.Application.Features.Restaurante;

/// <summary>
/// Pantallas de señalización digital del restaurante, separadas por RFC.
///
/// Vive aparte de <see cref="IRestaurantCatalogService"/> porque las lecturas
/// públicas fijan explícitamente el contexto de sesión RLS al RFC de la ruta
/// (la televisión no inicia sesión). Esa capacidad debe quedar acotada a un
/// servicio pequeño y auditable que solo alcanza tablas de señalización, nunca
/// productos, precios ni órdenes.
/// </summary>
public interface IRestaurantSignageService
{
  // --- Administración: siempre con el RFC de la sesión. ---
  Task<IReadOnlyList<RestaurantSignageScreenDto>> GetScreensAsync(string rfc, CancellationToken ct = default);
  Task<RestaurantCommandResult> SaveScreenAsync(RestaurantSignageScreenSaveRequest request, CancellationToken ct = default);
  Task<RestaurantCommandResult> DeleteScreenAsync(string rfc, int screenId, CancellationToken ct = default);
  Task<RestaurantCommandResult> AddImageAsync(RestaurantSignageImageUploadRequest request, CancellationToken ct = default);
  Task<RestaurantCommandResult> ReorderImagesAsync(RestaurantSignageOrderRequest request, CancellationToken ct = default);
  Task<RestaurantCommandResult> DeleteImageAsync(string rfc, long imageId, CancellationToken ct = default);
  Task<RestaurantSignageImagePayload?> GetImageThumbnailAsync(string rfc, long imageId, CancellationToken ct = default);

  // --- Público y anónimo: el RFC viene de la ruta. Solo filas habilitadas. ---
  Task<RestaurantSignagePublicScreenDto?> GetPublicScreenAsync(string rfc, string? screenKey, CancellationToken ct = default);
  Task<RestaurantSignageImagePayload?> GetPublicImageAsync(string rfc, long imageId, CancellationToken ct = default);
}
