namespace OrionERP.Application.Features.Restaurante;

public interface IRestaurantOrderService
{
  Task<RestaurantOrderResult> CreateOrderAsync(RestaurantOrderCreateRequest request, string userName, CancellationToken ct = default);
  Task<RestaurantOrderDto?> GetOrderAsync(string rfc, Guid orderId, CancellationToken ct = default);
  Task<RestaurantKitchenBoardDto> GetKitchenBoardAsync(string rfc, int siteId, CancellationToken ct = default);
  Task<IReadOnlyList<RestaurantPublicOrderDto>> GetPublicBoardAsync(string rfc, int siteId, CancellationToken ct = default);
  Task<RestaurantCommandResult> UpdateLineStatusAsync(string rfc, long lineId, string status, string userName, CancellationToken ct = default);
  Task<RestaurantCommandResult> RevertLineStatusAsync(string rfc, long lineId, string userName, CancellationToken ct = default);
  Task<RestaurantCommandResult> SetOrderPriorityAsync(string rfc, Guid orderId, byte priority, string reason, string supervisorUserName, CancellationToken ct = default);
  Task<RestaurantCommandResult> CancelOrderAsync(string rfc, Guid orderId, string reason, string supervisorUserName, CancellationToken ct = default);
  Task<IReadOnlyList<RestaurantOrderDto>> GetOperationalOrdersAsync(string rfc, int siteId, CancellationToken ct = default);
  Task<RestaurantCommandResult> UpdateOrderStatusAsync(string rfc, Guid orderId, string status, string userName, CancellationToken ct = default);
  Task<IReadOnlyList<RestaurantPaymentDto>> GetPaymentsAsync(string rfc, Guid orderId, CancellationToken ct = default);
  Task<RestaurantCommandResult> AddPaymentAsync(RestaurantAdditionalPaymentRequest request, string requestedBy, CancellationToken ct = default);
  Task<RestaurantCommandResult> RefundPaymentAsync(RestaurantPaymentRefundRequest request, string requestedBy, CancellationToken ct = default);
}
