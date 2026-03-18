using System;
using System.Collections.Generic;

namespace OrionERP.Application.Features.Auth.AdminPortal
{
    public sealed record IdentityAdminPortalSnapshot(
        IdentityAdminMetrics Metrics,
        IReadOnlyList<IdentityUserSummary> Users,
        IReadOnlyList<IdentityRoleSummary> Roles);

    public sealed record IdentityAdminMetrics(
        int UserCount,
        int RoleCount,
        int RoleClaimCount,
        int UserClaimCount,
        int UserRoleCount,
        int LoginCount,
        int TokenCount,
        int LockedUserCount);

    public sealed record IdentityUserSummary(
        string Id,
        string UserName,
        string? Email,
        int? EmployeeId,
        bool EmailConfirmed,
        bool IsLocked,
        DateTimeOffset? LockoutEnd,
        bool TwoFactorEnabled,
        int AccessFailedCount,
        int ClaimCount,
        int LoginCount,
        int TokenCount,
        IReadOnlyList<string> Roles);

    public sealed record IdentityRoleSummary(
        string Id,
        string Name,
        int ClaimCount,
        int UserCount);

    public sealed record IdentityUserEditor(
        string? Id,
        string UserName,
        string? Email,
        string? PhoneNumber,
        int? EmployeeId,
        bool EmailConfirmed,
        bool PhoneNumberConfirmed,
        bool TwoFactorEnabled,
        bool LockoutEnabled,
        DateTimeOffset? LockoutEnd,
        int AccessFailedCount,
        IReadOnlyList<string> AssignedRoles,
        IReadOnlyList<IdentityClaimRecord> Claims,
        IReadOnlyList<IdentityLoginRecord> Logins,
        IReadOnlyList<IdentityTokenRecord> Tokens);

    public sealed record IdentityRoleEditor(
        string? Id,
        string Name,
        IReadOnlyList<IdentityClaimRecord> Claims,
        IReadOnlyList<IdentityUserReference> Users);

    public sealed record IdentityClaimRecord(int Id, string ClaimType, string ClaimValue);

    public sealed record IdentityClaimInput(string ClaimType, string ClaimValue);

    public sealed record IdentityLoginRecord(string LoginProvider, string ProviderKey, string? ProviderDisplayName);

    public sealed record IdentityTokenRecord(string LoginProvider, string Name, bool HasValue, string? ValuePreview);

    public sealed record IdentityUserReference(string Id, string UserName, string? Email);

    public sealed record IdentityUserUpsertRequest(
        string? Id,
        string? ActorUserId,
        string UserName,
        string? Email,
        string? PhoneNumber,
        int? EmployeeId,
        bool EmailConfirmed,
        bool PhoneNumberConfirmed,
        bool TwoFactorEnabled,
        bool LockoutEnabled,
        DateTimeOffset? LockoutEnd,
        string? NewPassword,
        IReadOnlyList<string> RoleNames,
        IReadOnlyList<IdentityClaimInput> Claims);

    public sealed record IdentityAdminPasswordResetRequest(
        string UserId,
        string NewPassword);

    public sealed record IdentityRoleUpsertRequest(
        string? Id,
        string Name,
        IReadOnlyList<IdentityClaimInput> Claims);

    public sealed record IdentityAdminCommandResult(
        bool Succeeded,
        string Message,
        string? EntityId = null,
        string? EntityName = null,
        IReadOnlyList<string>? Errors = null);
}
