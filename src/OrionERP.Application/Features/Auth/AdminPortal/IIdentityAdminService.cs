using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Auth.AdminPortal
{
    public interface IIdentityAdminService
    {
        Task<IdentityAdminPortalSnapshot> GetPortalSnapshotAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<IdentityEmployeeOption>> GetEmployeeOptionsAsync(CancellationToken cancellationToken = default);
        Task<IdentityUserEditor?> GetUserAsync(string userId, CancellationToken cancellationToken = default);
        Task<IdentityRoleEditor?> GetRoleAsync(string roleId, CancellationToken cancellationToken = default);
        Task<IdentityAdminCommandResult> SaveUserAsync(IdentityUserUpsertRequest request, CancellationToken cancellationToken = default);
        Task<IdentityAdminCommandResult> ResetUserPasswordAsync(IdentityAdminPasswordResetRequest request, CancellationToken cancellationToken = default);
        Task<IdentityAdminCommandResult> DeleteUserAsync(string userId, string? actorUserId, CancellationToken cancellationToken = default);
        Task<IdentityAdminCommandResult> SaveRoleAsync(IdentityRoleUpsertRequest request, CancellationToken cancellationToken = default);
        Task<IdentityAdminCommandResult> DeleteRoleAsync(string roleId, CancellationToken cancellationToken = default);
    }
}
