using Microsoft.AspNetCore.Authorization;

namespace OrionERP.Infrastructure.Auth
{
    public sealed class RoleForRfcRequirement : IAuthorizationRequirement
    {
        public IReadOnlySet<string> RequiredRoles { get; }

        public RoleForRfcRequirement(params string[] requiredRoles)
        {
            RequiredRoles = requiredRoles
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }
}
