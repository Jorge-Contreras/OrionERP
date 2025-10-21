using Microsoft.AspNetCore.Authorization;

namespace OrionERP.Infrastructure.Auth
{
    public sealed class RoleForRfcRequirement : IAuthorizationRequirement
    {
        public string RequiredRole { get; }

        public RoleForRfcRequirement(string requiredRole)
        {
            RequiredRole = requiredRole;
        }
    }
}
