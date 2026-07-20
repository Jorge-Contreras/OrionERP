namespace OrionERP.Application.Features.Restaurante;

public interface IRestaurantAccountingService
{
  Task<RestaurantAccountingPreviewDto> GetDailyPreviewAsync(string rfc, int siteId, DateTime operationalDate, CancellationToken ct = default);
  Task<RestaurantCommandResult> GenerateDailyPolicyAsync(string rfc, int siteId, DateTime operationalDate, string userName, CancellationToken ct = default);
  Task<RestaurantCommandResult> GenerateIndividualCfdiPolicyAsync(string rfc, Guid orderId, int comprobanteId, string userName, CancellationToken ct = default);
}
