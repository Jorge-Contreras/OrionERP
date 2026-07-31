namespace OrionERP.Application.Features.Restaurante;

public interface IBrunoMemberService
{
  Task<LoyaltyMemberProfileDto?> GetMemberProfileByIdentityAsync(
    string rfc,
    string identityUserId,
    CancellationToken ct = default);

  Task<LoyaltyQrTokenDto> CreateQrTokenAsync(
    string rfc,
    Guid memberId,
    CancellationToken ct = default);

  Task<RestaurantCommandResult> UpdateConsentsAsync(
    LoyaltyConsentUpdateRequest request,
    CancellationToken ct = default);

  Task<RestaurantCommandResult> RequestClosureAsync(
    LoyaltyClosureRequest request,
    CancellationToken ct = default);
}
