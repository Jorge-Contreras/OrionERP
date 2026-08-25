using System.ComponentModel.DataAnnotations;

namespace OrionERP.Application.Features.Auth.Companies;

public sealed record CompanyLoginOption(
  string Rfc,
  string DisplayName,
  string? LegalName,
  int? EmployeeId,
  string? EmployeeName,
  string? Position,
  long BrandingVersion,
  bool HasLogo);

public sealed record CompanySummary(
  string Rfc,
  string DisplayName,
  string? LegalName,
  bool IsActive,
  long BrandingVersion,
  bool HasLogo,
  int MemberCount,
  int EmployeeCount,
  int ReviewCount,
  bool HasFiscalProfile,
  DateTime UpdatedAtUtc);

public sealed record CompanyEditor(
  string Rfc,
  string DisplayName,
  string? LegalName,
  bool IsActive,
  long BrandingVersion,
  bool HasLogo);

public sealed class CompanySaveRequest
{
  [Required, StringLength(20, MinimumLength = 12)]
  public string Rfc { get; set; } = string.Empty;

  [Required, StringLength(200)]
  public string DisplayName { get; set; } = string.Empty;

  [StringLength(300)]
  public string? LegalName { get; set; }

  public bool IsActive { get; set; } = true;
  public string ActorUserId { get; set; } = string.Empty;
}

public sealed record CompanyLogo(byte[] Bytes, string ContentType, long BrandingVersion);

public sealed record CompanyCommandResult(bool Succeeded, string Message, string? Rfc = null)
{
  public static CompanyCommandResult Ok(string message, string rfc) => new(true, message, rfc);
  public static CompanyCommandResult Fail(string message) => new(false, message);
}

public interface ICompanyAccessService
{
  Task<IReadOnlyList<CompanyLoginOption>> GetLoginOptionsAsync(string userId, CancellationToken ct = default);
  Task<bool> HasActiveMembershipAsync(string userId, string rfc, CancellationToken ct = default);
  Task<IReadOnlyList<CompanySummary>> GetCompaniesAsync(CancellationToken ct = default);
  Task<CompanyEditor?> GetCompanyAsync(string rfc, CancellationToken ct = default);
  Task<CompanyCommandResult> SaveCompanyAsync(CompanySaveRequest request, CancellationToken ct = default);
  Task<CompanyCommandResult> SaveLogoAsync(string rfc, byte[] bytes, string contentType, string actorUserId, CancellationToken ct = default);
  Task<CompanyLogo?> GetLogoAsync(string rfc, CancellationToken ct = default);
}
