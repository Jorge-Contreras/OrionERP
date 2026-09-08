using Microsoft.EntityFrameworkCore;
using OrionERP.Application.Features.Platform;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Infrastructure.Features.Platform;

/// <summary>
/// Provides the authoritative, non-cached authorization gate for platform
/// administration. This service is registered only by the management website's
/// platform-administration composition root.
/// </summary>
public sealed class PlatformAdministrationAccessValidator
  : IPlatformAdministrationAccessValidator
{
  private const string RevokedMessage =
    "La autorización administrativa fue revocada. Vuelve a iniciar sesión.";

  private readonly OrionIdentityDbContext _db;
  private readonly TimeProvider _timeProvider;

  public PlatformAdministrationAccessValidator(
    OrionIdentityDbContext db,
    TimeProvider timeProvider)
  {
    _db = db;
    _timeProvider = timeProvider;
  }

  public async Task EnsureAuthorizedAsync(
    string actorUserId,
    string companyRfc,
    CancellationToken ct = default)
  {
    var normalizedActorUserId = actorUserId?.Trim();
    var normalizedRfc = companyRfc?.Trim().ToUpperInvariant();
    if (string.IsNullOrWhiteSpace(normalizedActorUserId)
        || string.IsNullOrWhiteSpace(normalizedRfc))
      throw new UnauthorizedAccessException(RevokedMessage);

    var now = _timeProvider.GetUtcNow();
    var globalAdministratorRoleIds = _db.Roles.AsNoTracking()
      .Where(role => role.NormalizedName == "ADMINISTRADOR"
        && EF.Property<string>(role, "Scope") == IdentityRoleScopes.Global)
      .Select(role => role.Id);

    // A single fresh projection checks all revocable facts. Identity has no
    // separate IsActive column, so a present user that is not currently locked
    // out is considered active.
    var access = await _db.Users.AsNoTracking()
      .Where(user => user.Id == normalizedActorUserId)
      .Select(user => new
      {
        UserIsActive = !user.LockoutEnabled
          || !user.LockoutEnd.HasValue
          || user.LockoutEnd.Value <= now,
        HasActiveMembership = _db.UserCompanies.AsNoTracking().Any(membership =>
          membership.UserId == user.Id
          && membership.Rfc == normalizedRfc
          && membership.IsActive
          && membership.Company.IsActive),
        HasGlobalAdministratorRole = _db.UserRoles.AsNoTracking().Any(userRole =>
          userRole.UserId == user.Id
          && globalAdministratorRoleIds.Contains(userRole.RoleId))
      })
      .SingleOrDefaultAsync(ct);

    if (access is null
        || !access.UserIsActive
        || !access.HasActiveMembership
        || !access.HasGlobalAdministratorRole)
      throw new UnauthorizedAccessException(RevokedMessage);
  }
}
