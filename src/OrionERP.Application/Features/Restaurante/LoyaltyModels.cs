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
  public const string RefundReversal = "RefundReversal";
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
  public IReadOnlyList<LoyaltyPointLedgerDto> PointHistory { get; set; } = Array.Empty<LoyaltyPointLedgerDto>();
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
  public int OutstandingPoints { get; set; }
}
