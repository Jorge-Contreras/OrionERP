namespace OrionERP.Application.Features.Restaurante;

public interface IRestaurantCashService
{
  Task<IReadOnlyList<RestaurantCashRegisterDto>> GetRegistersAsync(string rfc, int siteId, CancellationToken ct = default);
  Task<RestaurantCommandResult> SaveRegisterAsync(RestaurantCashRegisterUpsertRequest request, CancellationToken ct = default);
  Task<IReadOnlyList<RestaurantCashShiftDto>> GetShiftsAsync(string rfc, int siteId, CancellationToken ct = default);
  Task<RestaurantCashShiftDto> OpenShiftAsync(RestaurantCashShiftOpenRequest request, string userName, CancellationToken ct = default);
  Task<RestaurantCashShiftDto> CloseShiftAsync(RestaurantCashShiftCloseRequest request, string userName, CancellationToken ct = default);
  Task<RestaurantCommandResult> ApproveShiftAsync(string rfc, Guid shiftId, string supervisorUserName, CancellationToken ct = default);
}
