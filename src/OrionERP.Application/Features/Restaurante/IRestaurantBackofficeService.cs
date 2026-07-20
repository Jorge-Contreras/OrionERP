namespace OrionERP.Application.Features.Restaurante;

public interface IRestaurantBackofficeService
{
  Task<RestaurantReportDto> GetReportAsync(string rfc, int siteId, DateTime from, DateTime to, CancellationToken ct = default);
  Task<IReadOnlyList<RestaurantSettlementCandidateDto>> GetSettlementCandidatesAsync(string rfc, int siteId, CancellationToken ct = default);
  Task<IReadOnlyList<RestaurantProviderSettlementDto>> GetSettlementsAsync(string rfc, int siteId, CancellationToken ct = default);
  Task<RestaurantCommandResult> CreateSettlementAsync(RestaurantSettlementCreateRequest request, string userName, CancellationToken ct = default);
}
