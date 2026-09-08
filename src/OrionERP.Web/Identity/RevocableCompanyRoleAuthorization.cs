using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Web.Identity;

public static class CompanyOperationPolicies
{
  public const string HospitalitySite = "CompanyOperations.HospitalitySite";
  public const string WorkOrders = "CompanyOperations.WorkOrders";
  public const string Logistics = "CompanyOperations.Logistics";
  public const string PhysicalCounts = "CompanyOperations.PhysicalCounts";

  public static void AddRevocableCompanyOperationPolicies(this AuthorizationOptions options)
  {
    options.AddPolicy(HospitalitySite, policy =>
      policy.RequireRevocableCompanyRoles("SatOperator"));
    options.AddPolicy(WorkOrders, policy =>
      policy.RequireRevocableCompanyRoles("OrdenTrabajoAdmin", "OrdenTrabajoSupervisor", "OrdenTrabajoOperador"));
    options.AddPolicy(Logistics, policy =>
      policy.RequireRevocableCompanyRoles("Logistica"));
    options.AddPolicy(PhysicalCounts, policy =>
      policy.RequireRevocableCompanyRoles("Logistica", "Conteo"));
  }
}

public static class RevocableCompanyRoleAuthorizationExtensions
{
  public static AuthorizationPolicyBuilder RequireRevocableCompanyRoles(
    this AuthorizationPolicyBuilder policy,
    params string[] roles)
  {
    var allowedRoles = new[] { "Administrador" }
      .Concat(roles)
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToArray();

    policy.RequireCompanySession();
    policy.RequireRole(allowedRoles);
    policy.AddRequirements(new RevocableCompanyRoleRequirement(allowedRoles));
    return policy;
  }
}

public sealed class RevocableCompanyRoleRequirement : IAuthorizationRequirement
{
  public RevocableCompanyRoleRequirement(IEnumerable<string> allowedRoles)
  {
    AllowedNormalizedRoles = allowedRoles
      .Select(role => role?.Trim().ToUpperInvariant())
      .Where(role => !string.IsNullOrWhiteSpace(role))
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .Cast<string>()
      .ToArray();

    if (AllowedNormalizedRoles.Count == 0)
      throw new ArgumentException("At least one company role is required.", nameof(allowedRoles));
  }

  public IReadOnlyList<string> AllowedNormalizedRoles { get; }
}

/// <summary>
/// Rechecks the selected company, user status and role assignment on every
/// component authorization. This closes the interval between an access change
/// and the next HTTP request without granting newly assigned roles to an old
/// principal.
/// </summary>
public sealed class RevocableCompanyRoleAuthorizationHandler(
  OrionIdentityDbContext db,
  TimeProvider timeProvider)
  : AuthorizationHandler<RevocableCompanyRoleRequirement>
{
  protected override async Task HandleRequirementAsync(
    AuthorizationHandlerContext context,
    RevocableCompanyRoleRequirement requirement)
  {
    var actorUserId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)?.Trim();
    var rfcs = context.User.FindAll(CompanyClaimTypes.Rfc)
      .Select(claim => claim.Value.Trim().ToUpperInvariant())
      .Where(value => value.Length > 0)
      .ToArray();
    if (string.IsNullOrWhiteSpace(actorUserId) || rfcs.Length != 1)
      return;

    var rfc = rfcs[0];
    var normalizedRoles = requirement.AllowedNormalizedRoles;
    var now = timeProvider.GetUtcNow();
    var globalRoleIds = db.Roles.AsNoTracking()
      .Where(role => role.NormalizedName != null
        && normalizedRoles.Contains(role.NormalizedName)
        && EF.Property<string>(role, "Scope") == IdentityRoleScopes.Global)
      .Select(role => role.Id);
    var companyRoleIds = db.Roles.AsNoTracking()
      .Where(role => role.NormalizedName != null
        && normalizedRoles.Contains(role.NormalizedName)
        && EF.Property<string>(role, "Scope") == IdentityRoleScopes.Company)
      .Select(role => role.Id);

    var access = await db.Users.AsNoTracking()
      .Where(user => user.Id == actorUserId)
      .Select(user => new
      {
        IsActive = !user.LockoutEnabled || !user.LockoutEnd.HasValue || user.LockoutEnd.Value <= now,
        HasMembership = db.UserCompanies.AsNoTracking().Any(link =>
          link.UserId == user.Id && link.Rfc == rfc && link.IsActive && link.Company.IsActive),
        HasRole = db.UserRoles.AsNoTracking().Any(link =>
          link.UserId == user.Id && globalRoleIds.Contains(link.RoleId))
          || db.UserCompanyRoles.AsNoTracking().Any(link =>
            link.UserId == user.Id && link.Rfc == rfc && companyRoleIds.Contains(link.RoleId))
      })
      .SingleOrDefaultAsync();

    if (access is { IsActive: true, HasMembership: true, HasRole: true })
      context.Succeed(requirement);
  }
}
