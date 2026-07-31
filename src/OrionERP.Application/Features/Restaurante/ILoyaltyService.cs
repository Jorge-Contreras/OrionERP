namespace OrionERP.Application.Features.Restaurante;

public interface ILoyaltyService : IBrunoMemberService
{
  Task<LoyaltyMemberDto?> FindMemberAsync(string rfc, string identifier, CancellationToken ct = default);
  Task<LoyaltyMemberProfileDto?> GetMemberProfileAsync(string rfc, Guid memberId, CancellationToken ct = default);
  Task<LoyaltyMemberProfileDto> CreateMemberAsync(LoyaltyMemberCreateRequest request, CancellationToken ct = default);
  Task<RestaurantCommandResult> UpdateVerificationAsync(LoyaltyMemberVerificationRequest request, CancellationToken ct = default);
  Task<RestaurantCommandResult> AdjustPointsAsync(LoyaltyAdjustmentRequest request, string adjustedBy, CancellationToken ct = default);
  Task<LoyaltyProgramReportDto> GetReportAsync(string rfc, DateTime from, DateTime to, CancellationToken ct = default);
}
