using System.ComponentModel.DataAnnotations;

namespace OrionERP.Application.Features.Restaurante;

public static class LoyaltyMemberStatuses
{
  public const string Active = "Active";
  public const string PendingVerification = "PendingVerification";
  public const string Closed = "Closed";
  public const string Suspended = "Suspended";
}

public static class LoyaltyLedgerTypes
{
  public const string Earn = "Earn";
  public const string Redeem = "Redeem";
  public const string Expiration = "Expiration";
  public const string RefundReversal = "RefundReversal";
  public const string CancellationReversal = "CancellationReversal";
  public const string AdminAdjustment = "AdminAdjustment";
}

public class LoyaltyMemberDto
{
  public Guid Id { get; set; }
  public string MembershipNumber { get; set; } = string.Empty;
  public string FirstName { get; set; } = string.Empty;
  public string LastName { get; set; } = string.Empty;
  public string DisplayName => $"{FirstName} {LastName}".Trim();
  public string MaskedEmail { get; set; } = string.Empty;
  public string MaskedPhone { get; set; } = string.Empty;
  public bool EmailVerified { get; set; }
  public bool PhoneVerified { get; set; }
  public string Status { get; set; } = LoyaltyMemberStatuses.PendingVerification;
  public int PointsBalance { get; set; }
  public DateTime CreatedAt { get; set; }
}

public sealed class LoyaltyMemberProfileDto : LoyaltyMemberDto
{
  public string Email { get; set; } = string.Empty;
  public string Phone { get; set; } = string.Empty;
  public bool EmailMarketingConsent { get; set; }
  public bool SmsMarketingConsent { get; set; }
  public bool WhatsAppMarketingConsent { get; set; }
  public IReadOnlyList<LoyaltyMemberOrderDto> OrderHistory { get; set; } = Array.Empty<LoyaltyMemberOrderDto>();
  public IReadOnlyList<LoyaltyPointLedgerDto> PointHistory { get; set; } = Array.Empty<LoyaltyPointLedgerDto>();
}

public sealed class LoyaltyMemberOrderDto
{
  public Guid Id { get; set; }
  public int Folio { get; set; }
  public string Status { get; set; } = string.Empty;
  public string PaymentStatus { get; set; } = string.Empty;
  public decimal Total { get; set; }
  public int PointsEarned { get; set; }
  public DateTime CreatedAt { get; set; }
}

public sealed class LoyaltyPointLedgerDto
{
  public long Id { get; set; }
  public string EntryType { get; set; } = string.Empty;
  public int PointsDelta { get; set; }
  public int BalanceAfter { get; set; }
  public Guid? OrderId { get; set; }
  public Guid? RefundId { get; set; }
  public string? Reason { get; set; }
  public DateTime OccurredAt { get; set; }
}

public sealed class LoyaltyMemberCreateRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  [Required] public string IdentityUserId { get; set; } = string.Empty;
  [Required, StringLength(100)] public string FirstName { get; set; } = string.Empty;
  [Required, StringLength(100)] public string LastName { get; set; } = string.Empty;
  [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = string.Empty;
  [Required, Phone, StringLength(30)] public string Phone { get; set; } = string.Empty;
  public bool IsAdultConfirmed { get; set; }
  [Required, StringLength(30)] public string PrivacyVersion { get; set; } = string.Empty;
  [Required, StringLength(30)] public string TermsVersion { get; set; } = string.Empty;
  public bool EmailMarketingConsent { get; set; }
  public bool SmsMarketingConsent { get; set; }
  public bool WhatsAppMarketingConsent { get; set; }
}

public sealed class LoyaltyMemberVerificationRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public Guid MemberId { get; set; }
  public bool EmailVerified { get; set; }
  public bool PhoneVerified { get; set; }
}

public sealed class LoyaltyAdjustmentRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public Guid MemberId { get; set; }
  [Range(-1000000, 1000000)] public int PointsDelta { get; set; }
  [Required, StringLength(500)] public string Reason { get; set; } = string.Empty;
}

public sealed class LoyaltyClosureRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public Guid MemberId { get; set; }
  [Required, StringLength(500)] public string Reason { get; set; } = string.Empty;
}

public sealed class LoyaltyConsentUpdateRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public Guid MemberId { get; set; }
  [Required, StringLength(30)] public string PrivacyVersion { get; set; } = string.Empty;
  [Required, StringLength(30)] public string TermsVersion { get; set; } = string.Empty;
  public bool EmailMarketingConsent { get; set; }
  public bool SmsMarketingConsent { get; set; }
  public bool WhatsAppMarketingConsent { get; set; }
}

public sealed class LoyaltyQrTokenDto
{
  public string Token { get; set; } = string.Empty;
  public DateTime ExpiresAtUtc { get; set; }
}

public sealed class LoyaltyProgramReportDto
{
  public int ActiveMembers { get; set; }
  public int NewMembers { get; set; }
  public int PointsIssued { get; set; }
  public int PointsReversed { get; set; }
  public int PointsRedeemed { get; set; }
  public int PointsExpired { get; set; }
  public decimal RedeemedValue { get; set; }
  public int OutstandingPoints { get; set; }
  public decimal OutstandingLiability { get; set; }
}

public sealed class LoyaltyProgramSettingsDto
{
  public decimal PesosPerPoint { get; set; } = 10m;
  public bool IsAccrualEnabled { get; set; }
  public bool PointsExpire { get; set; }
  public decimal PointValueMxn { get; set; } = 1m;
  public int MinimumRedeemPoints { get; set; } = 100;
  public int PointsValidityMonths { get; set; } = 12;
  public DateTime UpdatedAt { get; set; }
  public string? UpdatedBy { get; set; }
}

public sealed class LoyaltyProgramSettingsSaveRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  [Range(typeof(decimal), "0.01", "100000")] public decimal PesosPerPoint { get; set; } = 10m;
  public bool IsAccrualEnabled { get; set; }
  public bool PointsExpire { get; set; }
  [Range(typeof(decimal), "0.01", "1000")] public decimal PointValueMxn { get; set; } = 1m;
  [Range(1, 1000000)] public int MinimumRedeemPoints { get; set; } = 100;
  [Range(1, 240)] public int PointsValidityMonths { get; set; } = 12;
}

public sealed class LoyaltyRedeemRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public Guid MemberId { get; set; }
  [Range(1, 1000000)] public int Points { get; set; }
  [StringLength(500)] public string? Reason { get; set; }
  /// <summary>Clave idempotente opcional. Si se repite, el canje no se duplica.</summary>
  [StringLength(120)] public string? IdempotencyKey { get; set; }
}

public sealed class LoyaltyRedeemResultDto
{
  public bool Success { get; set; }
  public string Message { get; set; } = string.Empty;
  public int PointsRedeemed { get; set; }
  public decimal ValueMxn { get; set; }
  public int BalanceAfter { get; set; }
  public string? VoucherCode { get; set; }
}

public sealed class LoyaltyRedeemablePreviewDto
{
  public int PointsBalance { get; set; }
  public int MinimumRedeemPoints { get; set; }
  public decimal PointValueMxn { get; set; }
  public bool CanRedeem { get; set; }
  public int RedeemablePoints { get; set; }
  public decimal RedeemableValue { get; set; }
  public int PointsExpiringSoon { get; set; }
  public DateTime? NextExpirationDate { get; set; }
}

public sealed class LoyaltyExpirationRunDto
{
  public DateTime CutoffUtc { get; set; }
  public int MembersAffected { get; set; }
  public int PointsExpired { get; set; }
  public bool WasApplied { get; set; }
}
