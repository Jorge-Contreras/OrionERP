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
  /// <summary>
  /// Reserva el intento contra la cotización vigente. No toca a Clip: el pago no
  /// existe hasta el cargo.
  /// </summary>
  Task<RestaurantOnlineCheckoutBeginResult> BeginCheckoutAsync(
    PublicSiteBinding binding,
    RestaurantOnlineCheckoutBeginRequest request,
    Guid? memberId,
    CancellationToken ct = default);

  /// <summary>
  /// Cobra el token de tarjeta. Es el punto de no retorno: la base garantiza que
  /// sólo un cargo puede estar en vuelo por intento, y una llamada sin respuesta
  /// utilizable deja el intento en <c>ChargeUnknown</c> en vez de reintentarse.
  /// </summary>
  Task<RestaurantOnlineChargeResult> ChargeAsync(
    PublicSiteBinding binding,
    RestaurantOnlineChargeRequest request,
    Guid? memberId,
    CancellationToken ct = default);

  /// <summary>Cierra el cargo consultando el pago después de la autenticación 3DS.</summary>
  Task<RestaurantOnlineChargeResult> ConfirmChargeAsync(
    PublicSiteBinding binding,
    RestaurantOnlineChargeConfirmRequest request,
    Guid? memberId,
    CancellationToken ct = default);

  Task<RestaurantOnlineCheckoutStatusDto?> GetStatusAsync(
    PublicSiteBinding binding,
    string trackingToken,
    CancellationToken ct = default);

  Task<RestaurantClipWebhookResult> ProcessClipWebhookAsync(
    PublicSiteBinding binding,
    RestaurantClipWebhookRequest request,
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

public interface IPaymentRecoveryProcessor
{
  Task<int> ProcessPendingAsync(int batchSize = 10, CancellationToken ct = default);
}
