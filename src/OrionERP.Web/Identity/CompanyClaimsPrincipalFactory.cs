using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Web.Identity;

public static class CompanyAuthenticationSchemes
{
  public const string PendingCompanySelection = "OrionERP.CompanySelection";
}

public interface ICompanySignInContext
{
  string? SelectedRfc { get; set; }
}

public sealed class CompanySignInContext : ICompanySignInContext
{
  public string? SelectedRfc { get; set; }
}

public sealed class CompanyClaimsPrincipalFactory : UserClaimsPrincipalFactory<ApplicationUser>
{
  private readonly OrionIdentityDbContext _db;
  private readonly ICompanySignInContext _signInContext;
  private readonly IHttpContextAccessor _httpContextAccessor;

  public CompanyClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    IOptions<IdentityOptions> optionsAccessor,
    OrionIdentityDbContext db,
    ICompanySignInContext signInContext,
    IHttpContextAccessor httpContextAccessor)
    : base(userManager, optionsAccessor)
  {
    _db = db;
    _signInContext = signInContext;
    _httpContextAccessor = httpContextAccessor;
  }

  protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
  {
    var identity = await base.GenerateClaimsAsync(user);
    foreach (var claim in identity.Claims.Where(claim => CompanyClaimTypes.ReservedUserClaims.Contains(claim.Type)).ToArray())
      identity.RemoveClaim(claim);

    var selectedRfc = NormalizeRfc(_signInContext.SelectedRfc)
      ?? NormalizeRfc(_httpContextAccessor.HttpContext?.User.FindFirst(CompanyClaimTypes.Rfc)?.Value);
    if (selectedRfc is null) return identity;

    var membership = await _db.UserCompanies.AsNoTracking()
      .Include(link => link.Company)
      .SingleOrDefaultAsync(link =>
        link.UserId == user.Id && link.Rfc == selectedRfc && link.IsActive && link.Company.IsActive);
    if (membership is null) return identity;

    var globalRoleIds = await (
      from link in _db.Set<IdentityUserRole<string>>().AsNoTracking()
      join role in _db.Roles.AsNoTracking() on link.RoleId equals role.Id
      where link.UserId == user.Id && EF.Property<string>(role, "Scope") == IdentityRoleScopes.Global
      select role.Id).ToListAsync();

    var companyRoleIds = await _db.UserCompanyRoles.AsNoTracking()
      .Where(link => link.UserId == user.Id && link.Rfc == selectedRfc)
      .Select(link => link.RoleId)
      .ToListAsync();

    var roleIds = globalRoleIds.Concat(companyRoleIds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var roles = await _db.Roles.AsNoTracking()
      .Where(role => roleIds.Contains(role.Id))
      .Select(role => new { role.Id, role.Name })
      .ToListAsync();
    foreach (var role in roles.Where(role => !string.IsNullOrWhiteSpace(role.Name)))
      identity.AddClaim(new Claim(Options.ClaimsIdentity.RoleClaimType, role.Name!));

    var roleClaims = await _db.Set<IdentityRoleClaim<string>>().AsNoTracking()
      .Where(claim => roleIds.Contains(claim.RoleId) && claim.ClaimType != null && claim.ClaimValue != null)
      .Select(claim => new { claim.ClaimType, claim.ClaimValue })
      .ToListAsync();
    foreach (var claim in roleClaims)
      identity.AddClaim(new Claim(claim.ClaimType!, claim.ClaimValue!));

    identity.AddClaim(new Claim(CompanyClaimTypes.Rfc, membership.Rfc));
    identity.AddClaim(new Claim(CompanyClaimTypes.CompanyName, membership.Company.DisplayName));
    identity.AddClaim(new Claim(CompanyClaimTypes.SessionVersion, CompanyClaimTypes.CurrentSessionVersion));
    if (membership.EmployeeId.HasValue)
    {
      identity.AddClaim(new Claim(CompanyClaimTypes.EmployeeId, membership.EmployeeId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
      identity.AddClaim(new Claim(CompanyClaimTypes.EmployeeRfc, membership.Rfc));
    }

    return identity;
  }

  private static string? NormalizeRfc(string? rfc)
  {
    var normalized = rfc?.Trim().ToUpperInvariant();
    return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
  }
}
