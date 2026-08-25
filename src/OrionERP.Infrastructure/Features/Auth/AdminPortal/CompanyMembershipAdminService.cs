using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OrionERP.Application.Features.Auth.AdminPortal;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Infrastructure.Features.Auth.AdminPortal;

public sealed class CompanyMembershipAdminService : ICompanyMembershipAdminService
{
  private readonly OrionIdentityDbContext _db;
  private readonly UserManager<ApplicationUser> _userManager;

  public CompanyMembershipAdminService(OrionIdentityDbContext db, UserManager<ApplicationUser> userManager)
    => (_db, _userManager) = (db, userManager);

  public async Task<UserCompanyAccessEditor> GetUserAccessAsync(string userId, CancellationToken ct = default)
  {
    var companies = await _db.Companies.AsNoTracking()
      .OrderByDescending(company => company.IsActive).ThenBy(company => company.DisplayName)
      .Select(company => new MembershipCompanyOption(company.Rfc, company.DisplayName, company.IsActive))
      .ToListAsync(ct);
    var roles = await _db.Roles.AsNoTracking()
      .Where(role => EF.Property<string>(role, "Scope") == IdentityRoleScopes.Company)
      .OrderBy(role => role.Name)
      .Select(role => new MembershipRoleOption(role.Id, role.Name ?? role.Id))
      .ToListAsync(ct);

    if (!_db.Database.IsRelational()) return new(companies, roles, []);
    var connection = _db.Database.GetDbConnection();
    var close = connection.State == ConnectionState.Closed;
    if (close) await connection.OpenAsync(ct);
    try
    {
      const string sql = """
        SELECT membership.Rfc,company.DisplayName CompanyName,company.IsActive CompanyIsActive,
               membership.IsActive,membership.EmployeeId,
               COALESCE(NULLIF(LTRIM(RTRIM(employee.NombreCorto)),''),NULLIF(LTRIM(RTRIM(CONCAT(employee.Nombre,' ',employee.ApellidoPaterno,' ',employee.ApellidoMaterno))),'')) EmployeeName,
               NULLIF(LTRIM(RTRIM(employee.Puesto)),'') Position,NULLIF(LTRIM(RTRIM(employee.[Status])),'') EmployeeStatus,
               membership.AccessReviewRequired,membership.AccessReviewedAtUtc
        FROM auth.AspNetUserCompanies membership
        JOIN orion.Company company ON company.Rfc=membership.Rfc
        LEFT JOIN dbo.Capital_Humano employee ON employee.ID=membership.EmployeeId AND employee.RFC=membership.Rfc
        WHERE membership.UserId=@UserId
        ORDER BY company.DisplayName,membership.Rfc;

        SELECT link.Rfc,role.Name
        FROM auth.AspNetUserCompanyRoles link
        JOIN auth.AspNetRoles role ON role.Id=link.RoleId
        WHERE link.UserId=@UserId
        ORDER BY link.Rfc,role.Name;
        """;
      using var grid = await connection.QueryMultipleAsync(new CommandDefinition(sql, new { UserId = userId }, cancellationToken: ct));
      var rows = (await grid.ReadAsync<MembershipRow>()).AsList();
      var roleRows = (await grid.ReadAsync<MembershipRoleRow>()).AsList();
      var roleNames = roleRows.GroupBy(item => item.Rfc, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
      var memberships = rows.Select(row => new UserCompanyMembershipEditor(
        row.Rfc, row.CompanyName, row.CompanyIsActive, row.IsActive, row.EmployeeId, row.EmployeeName, row.Position, row.EmployeeStatus,
        row.AccessReviewRequired, row.AccessReviewedAtUtc,
        roleNames.GetValueOrDefault(row.Rfc) ?? [], BuildIssues(row))).ToArray();
      return new(companies, roles, memberships);
    }
    finally { if (close) await connection.CloseAsync(); }
  }

  public async Task<IReadOnlyList<MembershipEmployeeOption>> GetEmployeesAsync(string rfc, int? includeEmployeeId = null, CancellationToken ct = default)
  {
    if (!_db.Database.IsRelational()) return [];
    var normalizedRfc = NormalizeRfc(rfc);
    var connection = _db.Database.GetDbConnection();
    var close = connection.State == ConnectionState.Closed;
    if (close) await connection.OpenAsync(ct);
    try
    {
      const string sql = """
        SELECT employee.ID Id,
               COALESCE(NULLIF(LTRIM(RTRIM(employee.NombreCorto)),''),NULLIF(LTRIM(RTRIM(CONCAT(employee.Nombre,' ',employee.ApellidoPaterno,' ',employee.ApellidoMaterno))),''),CONCAT('ID ',employee.ID)) DisplayName,
               NULLIF(LTRIM(RTRIM(employee.Puesto)),'') Position,
               COALESCE(NULLIF(LTRIM(RTRIM(employee.[Status])),''),'SIN ESTADO') Status,
               CAST(CASE WHEN UPPER(LTRIM(RTRIM(ISNULL(employee.[Status],''))))='ACTIVO' THEN 1 ELSE 0 END AS bit) IsActive
        FROM dbo.Capital_Humano employee
        WHERE employee.RFC=@Rfc
          AND (UPPER(LTRIM(RTRIM(ISNULL(employee.[Status],''))))='ACTIVO' OR employee.ID=@IncludeEmployeeId)
        ORDER BY CASE WHEN employee.ID=@IncludeEmployeeId THEN 0 ELSE 1 END,DisplayName,employee.ID;
        """;
      var rows = await connection.QueryAsync<MembershipEmployeeOption>(new CommandDefinition(sql, new { Rfc = normalizedRfc, IncludeEmployeeId = includeEmployeeId }, cancellationToken: ct));
      return rows.AsList();
    }
    finally { if (close) await connection.CloseAsync(); }
  }

  public async Task<IdentityAdminCommandResult> SaveUserAccessAsync(SaveUserCompanyAccessRequest request, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ActorUserId)) return Failure("No fue posible identificar al usuario o al administrador.");
    var user = await _userManager.FindByIdAsync(request.UserId);
    if (user is null) return Failure("El usuario ya no existe.");

    var inputs = request.Memberships.Select(input => input with { Rfc = NormalizeRfc(input.Rfc), RoleNames = input.RoleNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() }).ToArray();
    if (inputs.Any(input => string.IsNullOrWhiteSpace(input.Rfc)) || inputs.GroupBy(input => input.Rfc, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)) return Failure("Cada empresa puede aparecer una sola vez.");
    var companyRfcs = await _db.Companies.Select(company => company.Rfc).ToListAsync(ct);
    var unknownCompanies = inputs.Where(input => !companyRfcs.Contains(input.Rfc, StringComparer.OrdinalIgnoreCase)).Select(input => input.Rfc).ToArray();
    if (unknownCompanies.Length > 0) return Failure($"Empresas no registradas: {string.Join(", ", unknownCompanies)}.");

    var companyRoles = await _db.Roles.AsNoTracking().Where(role => EF.Property<string>(role, "Scope") == IdentityRoleScopes.Company).Select(role => new { role.Id, role.Name }).ToListAsync(ct);
    var rolesByName = companyRoles.Where(role => !string.IsNullOrWhiteSpace(role.Name)).ToDictionary(role => role.Name!, role => role.Id, StringComparer.OrdinalIgnoreCase);
    var unknownRoles = inputs.SelectMany(input => input.RoleNames).Where(role => !rolesByName.ContainsKey(role)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    if (unknownRoles.Length > 0) return Failure($"Roles de empresa no válidos: {string.Join(", ", unknownRoles)}.");

    foreach (var input in inputs.Where(input => input.EmployeeId.HasValue))
    {
      var matches = await EmployeeMatchesCompanyAsync(input.EmployeeId!.Value, input.Rfc, ct);
      if (!matches) return Failure($"El empleado {input.EmployeeId} no pertenece al RFC {input.Rfc}.");
      var usedByOther = await _db.UserCompanies.AsNoTracking().AnyAsync(link => link.EmployeeId == input.EmployeeId && link.UserId != request.UserId, ct);
      if (usedByOther) return Failure($"El empleado {input.EmployeeId} ya está ligado a otra cuenta.");
    }

    await using var transaction = await _db.Database.BeginTransactionAsync(ct);
    var existing = await _db.UserCompanies.Where(link => link.UserId == request.UserId).ToListAsync(ct);
    var now = DateTime.UtcNow;
    foreach (var link in existing.Where(link => inputs.All(input => input.Rfc != link.Rfc)))
    {
      link.IsActive = false; link.AccessReviewRequired = true; link.UpdatedAtUtc = now; link.UpdatedBy = request.ActorUserId;
      _db.CompanyAccessAudits.Add(Audit(request, "MEMBERSHIP_DISABLED", link.Rfc, new { RemovedFromEditor = true }));
    }

    foreach (var input in inputs)
    {
      var link = existing.SingleOrDefault(candidate => candidate.Rfc == input.Rfc);
      var isNew = link is null;
      if (link is null)
      {
        link = new UserCompany { UserId = request.UserId, Rfc = input.Rfc, CreatedAtUtc = now };
        _db.UserCompanies.Add(link);
      }
      var changed = isNew || link.IsActive != input.IsActive || link.EmployeeId != input.EmployeeId;
      link.IsActive = input.IsActive; link.EmployeeId = input.EmployeeId; link.UpdatedAtUtc = now; link.UpdatedBy = request.ActorUserId;
      if (input.MarkReviewed)
      {
        link.AccessReviewRequired = false; link.AccessReviewedAtUtc = now; link.AccessReviewedBy = request.ActorUserId;
      }
      else if (changed) link.AccessReviewRequired = true;
      if (changed || input.MarkReviewed) _db.CompanyAccessAudits.Add(Audit(request, isNew ? "MEMBERSHIP_CREATED" : "MEMBERSHIP_UPDATED", input.Rfc, new { input.IsActive, input.EmployeeId, input.MarkReviewed }));
    }
    await _db.SaveChangesAsync(ct);

    var desiredRoleLinks = inputs.SelectMany(input => input.RoleNames.Select(roleName => (input.Rfc, RoleId: rolesByName[roleName]))).ToHashSet();
    var currentRoleLinks = await _db.UserCompanyRoles.Where(link => link.UserId == request.UserId).ToListAsync(ct);
    var remove = currentRoleLinks.Where(link => !desiredRoleLinks.Contains((link.Rfc, link.RoleId))).ToArray();
    if (remove.Length > 0) _db.UserCompanyRoles.RemoveRange(remove);
    var existingRoleKeys = currentRoleLinks.Select(link => (link.Rfc, link.RoleId)).ToHashSet();
    foreach (var desired in desiredRoleLinks.Where(key => !existingRoleKeys.Contains(key))) _db.UserCompanyRoles.Add(new UserCompanyRole { UserId = request.UserId, Rfc = desired.Rfc, RoleId = desired.RoleId });
    if (remove.Length > 0 || desiredRoleLinks.Any(key => !existingRoleKeys.Contains(key))) _db.CompanyAccessAudits.Add(Audit(request, "COMPANY_ROLES_UPDATED", null, new { MembershipCount = inputs.Length }));
    await _db.SaveChangesAsync(ct);

    var stamp = await _userManager.UpdateSecurityStampAsync(user);
    if (!stamp.Succeeded) return new(false, "Los accesos se guardaron, pero no fue posible invalidar las sesiones existentes.", Errors: stamp.Errors.Select(error => error.Description).ToArray());
    await transaction.CommitAsync(ct);
    return new(true, "Membresías, empleados y permisos por empresa actualizados.", user.Id, user.UserName ?? user.Email);
  }

  private async Task<bool> EmployeeMatchesCompanyAsync(int employeeId, string rfc, CancellationToken ct)
  {
    var connection = _db.Database.GetDbConnection(); var close = connection.State == ConnectionState.Closed; if (close) await connection.OpenAsync(ct);
    try { return await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM dbo.Capital_Humano WHERE ID=@EmployeeId AND RFC=@Rfc) THEN 1 ELSE 0 END AS bit);", new { EmployeeId = employeeId, Rfc = rfc }, cancellationToken: ct)); }
    finally { if (close) await connection.CloseAsync(); }
  }

  private static IReadOnlyList<string> BuildIssues(MembershipRow row)
  {
    var issues = new List<string>();
    if (!row.CompanyIsActive) issues.Add("Empresa inactiva");
    if (!row.IsActive) issues.Add("Membresía inactiva");
    if (!row.EmployeeId.HasValue) issues.Add("Sin empleado ligado");
    else if (string.IsNullOrWhiteSpace(row.EmployeeName)) issues.Add("Empleado inexistente o de otro RFC");
    else if (!string.Equals(row.EmployeeStatus?.Trim(), "ACTIVO", StringComparison.OrdinalIgnoreCase)) issues.Add("Empleado inactivo");
    if (row.AccessReviewRequired) issues.Add("Revisión pendiente");
    return issues;
  }

  private static CompanyAccessAudit Audit(SaveUserCompanyAccessRequest request, string action, string? rfc, object detail) => new() { OccurredAtUtc = DateTime.UtcNow, ActorUserId = request.ActorUserId, Action = action, TargetUserId = request.UserId, Rfc = rfc, DetailJson = JsonSerializer.Serialize(detail) };
  private static string NormalizeRfc(string? rfc) => (rfc ?? string.Empty).Trim().ToUpperInvariant();
  private static IdentityAdminCommandResult Failure(string message) => new(false, message);
  private sealed record MembershipRow(string Rfc, string CompanyName, bool CompanyIsActive, bool IsActive, int? EmployeeId, string? EmployeeName, string? Position, string? EmployeeStatus, bool AccessReviewRequired, DateTime? AccessReviewedAtUtc);
  private sealed record MembershipRoleRow(string Rfc, string Name);
}
