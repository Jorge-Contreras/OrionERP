using Microsoft.EntityFrameworkCore;
using OrionERP.Application.Features.Reservaciones;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Infrastructure.Features.Reservaciones;

public sealed class HospitalityAdministrationAccessValidator(
  OrionIdentityDbContext db,
  TimeProvider timeProvider) : IHospitalityAdministrationAccessValidator
{
  public async Task EnsureAuthorizedAsync(string actorUserId, string companyRfc, CancellationToken ct = default)
  {
    const string denied = "La autorización de Hospedaje fue revocada. Vuelve a iniciar sesión.";
    var actor = actorUserId?.Trim();
    var rfc = companyRfc?.Trim().ToUpperInvariant();
    if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(rfc))
      throw new UnauthorizedAccessException(denied);
    var now = timeProvider.GetUtcNow();

    var globalRoles = db.Roles.AsNoTracking()
      .Where(role => (role.NormalizedName == "ADMINISTRADOR" || role.NormalizedName == "SATOPERATOR")
        && EF.Property<string>(role, "Scope") == IdentityRoleScopes.Global)
      .Select(role => role.Id);
    var companyRoles = db.Roles.AsNoTracking()
      .Where(role => (role.NormalizedName == "ADMINISTRADOR" || role.NormalizedName == "SATOPERATOR")
        && EF.Property<string>(role, "Scope") == IdentityRoleScopes.Company)
      .Select(role => role.Id);

    var access = await db.Users.AsNoTracking().Where(user => user.Id == actor)
      .Select(user => new
      {
        IsActive = !user.LockoutEnabled || !user.LockoutEnd.HasValue || user.LockoutEnd.Value <= now,
        HasMembership = db.UserCompanies.AsNoTracking().Any(link =>
          link.UserId == user.Id && link.Rfc == rfc && link.IsActive && link.Company.IsActive),
        HasRole = db.UserRoles.AsNoTracking().Any(link => link.UserId == user.Id && globalRoles.Contains(link.RoleId))
          || db.UserCompanyRoles.AsNoTracking().Any(link =>
            link.UserId == user.Id && link.Rfc == rfc && companyRoles.Contains(link.RoleId))
      }).SingleOrDefaultAsync(ct);

    if (access is null || !access.IsActive || !access.HasMembership || !access.HasRole)
      throw new UnauthorizedAccessException(denied);
  }
}
