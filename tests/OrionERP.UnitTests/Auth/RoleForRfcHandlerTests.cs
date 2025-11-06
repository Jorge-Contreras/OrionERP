using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.UnitTests.Auth;

public class RoleForRfcHandlerTests
{
    [Fact]
    public async Task HandleRequirementAsync_Succeeds_WhenUserHasRoleAndMatchingRfcClaim()
    {
        // Arrange
        const string requiredRole = "Administrador";
        const string selectedRfc = "ABC123456";

        var context = new RfcContext { CurrentRfc = selectedRfc };
        var handler = new RoleForRfcHandler(context);
        var requirement = new RoleForRfcRequirement(requiredRole);

        var identity = new ClaimsIdentity(
            authenticationType: "Test",
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role);

        identity.AddClaim(new Claim(ClaimTypes.Role, requiredRole));
        identity.AddClaim(new Claim("rfc", selectedRfc));

        var principal = new ClaimsPrincipal(identity);
        var authorizationContext = new AuthorizationHandlerContext(
            new[] { requirement },
            principal,
            resource: null);

        // Act
        await handler.HandleAsync(authorizationContext);

        // Assert
        Assert.True(authorizationContext.HasSucceeded);
    }
}
