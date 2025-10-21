using System;
using System.Linq;
using Microsoft.AspNetCore.Authorization;

namespace OrionERP.Infrastructure.Auth
{
    public sealed class RoleForRfcHandler : AuthorizationHandler<RoleForRfcRequirement>
    {
        private readonly IRfcContext _rfc;

        public RoleForRfcHandler(IRfcContext rfc)
        {
            _rfc = rfc;
        }

        protected override Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            RoleForRfcRequirement requirement)
        {
            if (context.User?.Identity?.IsAuthenticated != true)
            {
                return Task.CompletedTask;
            }

            if (!context.User.IsInRole(requirement.RequiredRole))
            {
                return Task.CompletedTask;
            }

            var selected = _rfc.CurrentRfc;
            if (string.IsNullOrWhiteSpace(selected))
            {
                return Task.CompletedTask;
            }

            var hasMatchingRfc = context.User.Claims.Any(c =>
                c.Type == "rfc" &&
                string.Equals(c.Value, selected, StringComparison.OrdinalIgnoreCase));

            if (hasMatchingRfc)
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }
}
