namespace OrionERP.Application.Features.Restaurante;

public interface IRestaurantPromotionService
{
  Task<RestaurantPromotionQuoteDto> QuoteAsync(RestaurantPromotionQuoteRequest request, CancellationToken ct = default);
  Task<IReadOnlyList<RestaurantPromotionDto>> GetPromotionsAsync(string rfc, int? siteId = null, bool includeInactive = true, CancellationToken ct = default);
  Task<RestaurantCommandResult> SavePromotionAsync(RestaurantPromotionSaveRequest request, string userName, CancellationToken ct = default);
  Task<RestaurantPromotionReportDto> GetReportAsync(string rfc, int siteId, DateTime from, DateTime to, CancellationToken ct = default);
}
