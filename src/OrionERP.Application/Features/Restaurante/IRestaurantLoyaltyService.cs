namespace OrionERP.Application.Features.Restaurante;

public interface ILoyaltyService : IBrunoMemberService
{
  Task<LoyaltyMemberDto?> FindMemberAsync(string rfc, string identifier, CancellationToken ct = default);
  Task<LoyaltyMemberProfileDto?> GetMemberProfileAsync(string rfc, Guid memberId, CancellationToken ct = default);
  Task<LoyaltyMemberProfileDto> CreateMemberAsync(LoyaltyMemberCreateRequest request, CancellationToken ct = default);
  Task<RestaurantCommandResult> UpdateVerificationAsync(LoyaltyMemberVerificationRequest request, CancellationToken ct = default);
  Task<RestaurantCommandResult> AdjustPointsAsync(LoyaltyAdjustmentRequest request, string adjustedBy, CancellationToken ct = default);
  Task<LoyaltyProgramReportDto> GetReportAsync(string rfc, DateTime from, DateTime to, CancellationToken ct = default);

  Task<LoyaltyProgramSettingsDto?> GetProgramSettingsAsync(string rfc, CancellationToken ct = default);
  Task<RestaurantCommandResult> SaveProgramSettingsAsync(LoyaltyProgramSettingsSaveRequest request, string updatedBy, CancellationToken ct = default);

  /// <summary>Saldo canjeable del socio y puntos próximos a caducar.</summary>
  Task<LoyaltyRedeemablePreviewDto?> GetRedeemablePreviewAsync(string rfc, Guid memberId, CancellationToken ct = default);

  /// <summary>
  /// Caduca los puntos que rebasaron la vigencia del programa.
  /// Con <paramref name="applyChanges"/> en false solo simula y no escribe nada.
  /// </summary>
  Task<LoyaltyExpirationRunDto> ExpirePointsAsync(string rfc, bool applyChanges, string runBy, CancellationToken ct = default);
}
