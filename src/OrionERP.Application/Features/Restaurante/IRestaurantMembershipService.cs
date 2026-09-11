namespace OrionERP.Application.Features.Restaurante;

public interface IRestaurantMembershipService
{
  Task<LoyaltyMemberProfileDto?> GetMemberProfileByIdentityAsync(
    string rfc,
    long publicSiteId,
    string identityUserId,
    CancellationToken ct = default);

  Task<LoyaltyQrTokenDto> CreateQrTokenAsync(
    string rfc,
    long publicSiteId,
    Guid memberId,
    CancellationToken ct = default);

  Task<RestaurantCommandResult> UpdateConsentsAsync(
    LoyaltyConsentUpdateRequest request,
    CancellationToken ct = default);

  Task<RestaurantCommandResult> RequestClosureAsync(
    LoyaltyClosureRequest request,
    CancellationToken ct = default);
}
