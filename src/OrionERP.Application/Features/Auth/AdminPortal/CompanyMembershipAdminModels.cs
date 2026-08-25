namespace OrionERP.Application.Features.Auth.AdminPortal;

public sealed record MembershipCompanyOption(string Rfc, string DisplayName, bool IsActive);
public sealed record MembershipRoleOption(string Id, string Name);
public sealed record MembershipEmployeeOption(int Id, string DisplayName, string? Position, string Status, bool IsActive);

public sealed record UserCompanyMembershipEditor(
  string Rfc,
  string CompanyName,
  bool CompanyIsActive,
  bool IsActive,
  int? EmployeeId,
  string? EmployeeName,
  string? Position,
  string? EmployeeStatus,
  bool AccessReviewRequired,
  DateTime? AccessReviewedAtUtc,
  IReadOnlyList<string> RoleNames,
  IReadOnlyList<string> Issues);

public sealed record UserCompanyAccessEditor(
  IReadOnlyList<MembershipCompanyOption> Companies,
  IReadOnlyList<MembershipRoleOption> CompanyRoles,
  IReadOnlyList<UserCompanyMembershipEditor> Memberships);

public sealed record UserCompanyMembershipInput(
  string Rfc,
  bool IsActive,
  int? EmployeeId,
  bool MarkReviewed,
  IReadOnlyList<string> RoleNames);

public sealed record SaveUserCompanyAccessRequest(
  string UserId,
  string ActorUserId,
  IReadOnlyList<UserCompanyMembershipInput> Memberships);

public interface ICompanyMembershipAdminService
{
  Task<UserCompanyAccessEditor> GetUserAccessAsync(string userId, CancellationToken ct = default);
  Task<IReadOnlyList<MembershipEmployeeOption>> GetEmployeesAsync(string rfc, int? includeEmployeeId = null, CancellationToken ct = default);
  Task<IdentityAdminCommandResult> SaveUserAccessAsync(SaveUserCompanyAccessRequest request, CancellationToken ct = default);
}
