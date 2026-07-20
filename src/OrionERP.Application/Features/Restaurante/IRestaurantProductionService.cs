namespace OrionERP.Application.Features.Restaurante;

public interface IRestaurantProductionService
{
  Task<RestaurantProductionWorkspaceDto> GetWorkspaceAsync(string rfc, int siteId, CancellationToken ct = default);
  Task<RestaurantCommandResult> PlanAsync(RestaurantProductionPlanRequest request, string userName, CancellationToken ct = default);
  Task<RestaurantCommandResult> StartAsync(string rfc, Guid productionOrderId, string userName, CancellationToken ct = default);
  Task<RestaurantCommandResult> CompleteAsync(RestaurantProductionCompleteRequest request, string userName, CancellationToken ct = default);
  Task<RestaurantCommandResult> CancelAsync(string rfc, Guid productionOrderId, string userName, CancellationToken ct = default);
}
