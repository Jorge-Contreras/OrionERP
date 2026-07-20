namespace OrionERP.Application.Features.Restaurante;

public interface IRestaurantQuickPinService
{
  Task<RestaurantCommandResult> SetOwnPinAsync(
    RestaurantQuickPinSetupRequest request,
    string userId,
    string userName,
    CancellationToken ct = default);

  Task<RestaurantQuickPinResult> VerifySupervisorPinAsync(
    RestaurantQuickPinVerifyRequest request,
    CancellationToken ct = default);
}
