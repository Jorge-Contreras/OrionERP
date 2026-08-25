using Microsoft.AspNetCore.Identity;

namespace OrionERP.Infrastructure.Auth;

public static class IdentityRoleScopes
{
  public const string Global = "Global";
  public const string Company = "Company";

  public static bool IsGlobalRole(string? roleName)
    => string.Equals(roleName, "Administrador", StringComparison.OrdinalIgnoreCase)
       || string.Equals(roleName, "Arrendadores", StringComparison.OrdinalIgnoreCase);
}

public static class CompanyClaimTypes
{
  public const string Rfc = "rfc";
  public const string EmployeeId = "employee_id";
  public const string EmployeeRfc = "employee_rfc";
  public const string CompanyName = "company_name";
  public const string SessionVersion = "company_session_version";
  public const string CurrentSessionVersion = "1";

  public static readonly IReadOnlySet<string> ReservedUserClaims = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
  {
    Rfc,
    EmployeeId,
    EmployeeRfc,
    CompanyName,
    SessionVersion,
    System.Security.Claims.ClaimTypes.Role
  };
}

public sealed class OrionCompany
{
  public string Rfc { get; set; } = string.Empty;
  public string DisplayName { get; set; } = string.Empty;
  public string? LegalName { get; set; }
  public bool IsActive { get; set; } = true;
  public byte[]? LogoBytes { get; set; }
  public string? LogoContentType { get; set; }
  public long BrandingVersion { get; set; } = 1;
  public DateTime CreatedAtUtc { get; set; }
  public DateTime UpdatedAtUtc { get; set; }
  public string? UpdatedBy { get; set; }
  public byte[] RowVersion { get; set; } = [];

  public ICollection<UserCompany> Users { get; set; } = new List<UserCompany>();
}

public sealed class UserCompany
{
  public string UserId { get; set; } = string.Empty;
  public string Rfc { get; set; } = string.Empty;
  public int? EmployeeId { get; set; }
  public bool IsActive { get; set; } = true;
  public bool AccessReviewRequired { get; set; }
  public DateTime? AccessReviewedAtUtc { get; set; }
  public string? AccessReviewedBy { get; set; }
  public DateTime CreatedAtUtc { get; set; }
  public DateTime UpdatedAtUtc { get; set; }
  public string? UpdatedBy { get; set; }
  public byte[] RowVersion { get; set; } = [];

  public ApplicationUser User { get; set; } = default!;
  public OrionCompany Company { get; set; } = default!;
  public ICollection<UserCompanyRole> Roles { get; set; } = new List<UserCompanyRole>();
}

public sealed class UserCompanyRole
{
  public string UserId { get; set; } = string.Empty;
  public string Rfc { get; set; } = string.Empty;
  public string RoleId { get; set; } = string.Empty;

  public UserCompany Membership { get; set; } = default!;
  public IdentityRole Role { get; set; } = default!;
}

public sealed class CompanyAccessAudit
{
  public long Id { get; set; }
  public DateTime OccurredAtUtc { get; set; }
  public string ActorUserId { get; set; } = string.Empty;
  public string Action { get; set; } = string.Empty;
  public string? TargetUserId { get; set; }
  public string? Rfc { get; set; }
  public string? RoleId { get; set; }
  public string? DetailJson { get; set; }
}
