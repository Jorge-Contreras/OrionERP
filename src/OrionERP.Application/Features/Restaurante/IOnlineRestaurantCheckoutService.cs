using OrionERP.Application.Features.Platform;

namespace OrionERP.Application.Features.Restaurante;

public interface IOnlineRestaurantQuoteService
{
  Task<RestaurantOnlineOrderingConfigurationDto> GetConfigurationAsync(
    PublicSiteBinding binding,
    CancellationToken ct = default);

  Task<RestaurantOnlineQuoteResult> QuoteAsync(
    PublicSiteBinding binding,
    RestaurantOnlineQuoteRequest request,
    Guid? memberId,
    CancellationToken ct = default);
}

public interface IOnlineRestaurantCheckoutService : IOnlineRestaurantQuoteService
{

  Task<RestaurantOnlinePayPalOrderResult> CreatePayPalOrderAsync(
    PublicSiteBinding binding,
    RestaurantOnlinePayPalOrderCreateRequest request,
    Guid? memberId,
    CancellationToken ct = default);

  Task<RestaurantOnlinePayPalCaptureResult> CapturePayPalOrderAsync(
    PublicSiteBinding binding,
    string payPalOrderId,
    RestaurantOnlinePayPalCaptureRequest request,
    Guid? memberId,
    CancellationToken ct = default);

  Task<RestaurantOnlineCheckoutStatusDto?> GetStatusAsync(
    PublicSiteBinding binding,
    string trackingToken,
    CancellationToken ct = default);

  Task<RestaurantPayPalWebhookResult> ProcessPayPalWebhookAsync(
    PublicSiteBinding binding,
    RestaurantPayPalWebhookRequest request,
    CancellationToken ct = default);
}

public interface IOnlineRestaurantOrderingAdminService
{
  Task<RestaurantOnlineOrderingAdminDto?> GetAsync(string rfc, int siteId, CancellationToken ct = default);
  Task<RestaurantCommandResult> SaveAsync(
    RestaurantOnlineOrderingAdminSaveRequest request,
    string userName,
    CancellationToken ct = default);
  Task<RestaurantCommandResult> RequestRefundAsync(
    RestaurantOnlineRefundRequest request,
    CancellationToken ct = default);
}

public interface IOnlineOrderImportProcessor
{
  Task<int> ProcessPendingAsync(int batchSize = 10, CancellationToken ct = default);
  Task RecordHeartbeatAsync(CancellationToken ct = default);
}

public interface IPayPalRecoveryProcessor
{
  Task<int> ProcessPendingAsync(int batchSize = 10, CancellationToken ct = default);
}
