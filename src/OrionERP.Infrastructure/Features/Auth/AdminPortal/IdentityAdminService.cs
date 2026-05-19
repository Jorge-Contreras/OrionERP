using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OrionERP.Application.Features.Auth.AdminPortal;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Infrastructure.Features.Auth.AdminPortal
{
    public sealed class IdentityAdminService : IIdentityAdminService
    {
        private const string AdministratorRoleName = "Administrador";
        private const string AdministratorRoleNormalizedName = "ADMINISTRADOR";
        private const string ArrendadoresRoleName = "Arrendadores";
        private static readonly StringComparer RoleNameComparer = StringComparer.OrdinalIgnoreCase;

        private readonly OrionIdentityDbContext _db;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<IdentityRole> _roleManager;

        public IdentityAdminService(
            OrionIdentityDbContext db,
            UserManager<ApplicationUser> userManager,
            RoleManager<IdentityRole> roleManager)
        {
            _db = db;
            _userManager = userManager;
            _roleManager = roleManager;
        }

        public async Task<IdentityAdminPortalSnapshot> GetPortalSnapshotAsync(CancellationToken cancellationToken = default)
        {
            var users = await _userManager.Users
                .AsNoTracking()
                .OrderBy(u => u.UserName ?? u.Email ?? string.Empty)
                .ThenBy(u => u.Id)
                .Select(user => new
                {
                    user.Id,
                    user.UserName,
                    user.Email,
                    user.EmployeeId,
                    user.ArrendadorProveedorId,
                    user.EmailConfirmed,
                    user.LockoutEnabled,
                    user.LockoutEnd,
                    user.TwoFactorEnabled,
                    user.AccessFailedCount
                })
                .ToListAsync(cancellationToken);

            var roles = await _roleManager.Roles
                .AsNoTracking()
                .OrderBy(r => r.Name ?? string.Empty)
                .ThenBy(r => r.Id)
                .Select(role => new
                {
                    role.Id,
                    role.Name
                })
                .ToListAsync(cancellationToken);

            var userRoles = await (
                from link in _db.Set<IdentityUserRole<string>>().AsNoTracking()
                join role in _roleManager.Roles.AsNoTracking() on link.RoleId equals role.Id
                select new
                {
                    link.UserId,
                    link.RoleId,
                    RoleName = role.Name
                })
                .ToListAsync(cancellationToken);

            var claimCountByUserId = await _db.Set<IdentityUserClaim<string>>()
                .AsNoTracking()
                .GroupBy(claim => claim.UserId)
                .Select(group => new
                {
                    UserId = group.Key,
                    Count = group.Count()
                })
                .ToDictionaryAsync(item => item.UserId, item => item.Count, StringComparer.OrdinalIgnoreCase, cancellationToken);

            var claimCountByRoleId = await _db.Set<IdentityRoleClaim<string>>()
                .AsNoTracking()
                .GroupBy(claim => claim.RoleId)
                .Select(group => new
                {
                    RoleId = group.Key,
                    Count = group.Count()
                })
                .ToDictionaryAsync(item => item.RoleId, item => item.Count, StringComparer.OrdinalIgnoreCase, cancellationToken);

            var loginCountByUserId = await _db.Set<IdentityUserLogin<string>>()
                .AsNoTracking()
                .GroupBy(login => login.UserId)
                .Select(group => new
                {
                    UserId = group.Key,
                    Count = group.Count()
                })
                .ToDictionaryAsync(item => item.UserId, item => item.Count, StringComparer.OrdinalIgnoreCase, cancellationToken);

            var tokenCountByUserId = await _db.Set<IdentityUserToken<string>>()
                .AsNoTracking()
                .GroupBy(token => token.UserId)
                .Select(group => new
                {
                    UserId = group.Key,
                    Count = group.Count()
                })
                .ToDictionaryAsync(item => item.UserId, item => item.Count, StringComparer.OrdinalIgnoreCase, cancellationToken);

            var rolesByUserId = userRoles
                .GroupBy(link => link.UserId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<string>)group
                        .Select(link => link.RoleName)
                        .Where(roleName => !string.IsNullOrWhiteSpace(roleName))
                        .Select(roleName => roleName!)
                        .Distinct(RoleNameComparer)
                        .OrderBy(roleName => roleName, RoleNameComparer)
                        .ToArray(),
                    StringComparer.OrdinalIgnoreCase);

            var roleUserCounts = userRoles
                .GroupBy(link => link.RoleId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(link => link.UserId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    StringComparer.OrdinalIgnoreCase);

            var now = DateTimeOffset.UtcNow;

            var metrics = new IdentityAdminMetrics(
                UserCount: users.Count,
                RoleCount: roles.Count,
                RoleClaimCount: claimCountByRoleId.Values.Sum(),
                UserClaimCount: claimCountByUserId.Values.Sum(),
                UserRoleCount: userRoles.Count,
                LoginCount: loginCountByUserId.Values.Sum(),
                TokenCount: tokenCountByUserId.Values.Sum(),
                LockedUserCount: users.Count(user => user.LockoutEnabled && user.LockoutEnd.HasValue && user.LockoutEnd.Value > now));

            var userSummaries = users
                .Select(user =>
                {
                    var assignedRoles = rolesByUserId.TryGetValue(user.Id, out var userRolesForUser)
                        ? userRolesForUser
                        : Array.Empty<string>();

                    return new IdentityUserSummary(
                        user.Id,
                        user.UserName ?? user.Email ?? "(sin usuario)",
                        user.Email,
                        user.EmployeeId,
                        user.ArrendadorProveedorId,
                        user.EmailConfirmed,
                        user.LockoutEnabled && user.LockoutEnd.HasValue && user.LockoutEnd.Value > now,
                        user.LockoutEnd,
                        user.TwoFactorEnabled,
                        user.AccessFailedCount,
                        claimCountByUserId.TryGetValue(user.Id, out var userClaimCount) ? userClaimCount : 0,
                        loginCountByUserId.TryGetValue(user.Id, out var userLoginCount) ? userLoginCount : 0,
                        tokenCountByUserId.TryGetValue(user.Id, out var userTokenCount) ? userTokenCount : 0,
                        assignedRoles);
                })
                .ToArray();

            var roleSummaries = roles
                .Select(role => new IdentityRoleSummary(
                    role.Id,
                    role.Name ?? "(sin nombre)",
                    claimCountByRoleId.TryGetValue(role.Id, out var roleClaimCount) ? roleClaimCount : 0,
                    roleUserCounts.TryGetValue(role.Id, out var roleUserCount) ? roleUserCount : 0))
                .ToArray();

            return new IdentityAdminPortalSnapshot(metrics, userSummaries, roleSummaries);
        }

        public async Task<IdentityUserEditor?> GetUserAsync(string userId, CancellationToken cancellationToken = default)
        {
            var user = await _userManager.Users
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);

            if (user is null)
            {
                return null;
            }

            var assignedRoles = await (
                from link in _db.Set<IdentityUserRole<string>>().AsNoTracking()
                join role in _roleManager.Roles.AsNoTracking() on link.RoleId equals role.Id
                where link.UserId == userId
                orderby role.Name
                select role.Name ?? string.Empty)
                .Where(roleName => roleName != string.Empty)
                .ToListAsync(cancellationToken);

            var claims = await _db.Set<IdentityUserClaim<string>>()
                .AsNoTracking()
                .Where(claim => claim.UserId == userId)
                .OrderBy(claim => claim.ClaimType)
                .ThenBy(claim => claim.ClaimValue)
                .Select(claim => new IdentityClaimRecord(
                    claim.Id,
                    claim.ClaimType ?? string.Empty,
                    claim.ClaimValue ?? string.Empty))
                .ToListAsync(cancellationToken);

            var logins = await _db.Set<IdentityUserLogin<string>>()
                .AsNoTracking()
                .Where(login => login.UserId == userId)
                .OrderBy(login => login.LoginProvider)
                .ThenBy(login => login.ProviderKey)
                .Select(login => new IdentityLoginRecord(
                    login.LoginProvider,
                    login.ProviderKey,
                    login.ProviderDisplayName))
                .ToListAsync(cancellationToken);

            var tokens = await _db.Set<IdentityUserToken<string>>()
                .AsNoTracking()
                .Where(token => token.UserId == userId)
                .OrderBy(token => token.LoginProvider)
                .ThenBy(token => token.Name)
                .Select(token => new IdentityTokenRecord(
                    token.LoginProvider,
                    token.Name,
                    !string.IsNullOrWhiteSpace(token.Value),
                    CreateTokenPreview(token.Value)))
                .ToListAsync(cancellationToken);

            return new IdentityUserEditor(
                user.Id,
                user.UserName ?? user.Email ?? string.Empty,
                user.Email,
                user.PhoneNumber,
                user.EmployeeId,
                user.ArrendadorProveedorId,
                user.EmailConfirmed,
                user.PhoneNumberConfirmed,
                user.TwoFactorEnabled,
                user.LockoutEnabled,
                user.LockoutEnd,
                user.AccessFailedCount,
                assignedRoles,
                claims,
                logins,
                tokens);
        }

        public async Task<IdentityRoleEditor?> GetRoleAsync(string roleId, CancellationToken cancellationToken = default)
        {
            var role = await _roleManager.Roles
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == roleId, cancellationToken);

            if (role is null)
            {
                return null;
            }

            var claims = await _db.Set<IdentityRoleClaim<string>>()
                .AsNoTracking()
                .Where(claim => claim.RoleId == roleId)
                .OrderBy(claim => claim.ClaimType)
                .ThenBy(claim => claim.ClaimValue)
                .Select(claim => new IdentityClaimRecord(
                    claim.Id,
                    claim.ClaimType ?? string.Empty,
                    claim.ClaimValue ?? string.Empty))
                .ToListAsync(cancellationToken);

            var users = await (
                from link in _db.Set<IdentityUserRole<string>>().AsNoTracking()
                join user in _userManager.Users.AsNoTracking() on link.UserId equals user.Id
                where link.RoleId == roleId
                orderby user.UserName, user.Email
                select new IdentityUserReference(
                    user.Id,
                    user.UserName ?? user.Email ?? "(sin usuario)",
                    user.Email))
                .ToListAsync(cancellationToken);

            return new IdentityRoleEditor(
                role.Id,
                role.Name ?? string.Empty,
                claims,
                users);
        }

        public async Task<IdentityAdminCommandResult> SaveUserAsync(IdentityUserUpsertRequest request, CancellationToken cancellationToken = default)
        {
            var normalizedUserName = NormalizeRequired(request.UserName);
            if (string.IsNullOrWhiteSpace(normalizedUserName))
            {
                return Failure("El nombre de usuario es obligatorio.");
            }

            if (string.IsNullOrWhiteSpace(request.Id) && string.IsNullOrWhiteSpace(request.NewPassword))
            {
                return Failure("La contraseña es obligatoria al crear un usuario.");
            }

            var desiredRoleNames = NormalizeRoleNames(request.RoleNames);
            var validRoleNames = await _roleManager.Roles
                .AsNoTracking()
                .Select(role => role.Name)
                .Where(roleName => roleName != null)
                .ToListAsync(cancellationToken);

            var validRoleNameSet = validRoleNames
                .Select(roleName => roleName!)
                .ToHashSet(RoleNameComparer);

            var unknownRoleNames = desiredRoleNames
                .Where(roleName => !validRoleNameSet.Contains(roleName))
                .ToArray();

            if (unknownRoleNames.Length > 0)
            {
                return Failure($"Los siguientes roles no existen: {string.Join(", ", unknownRoleNames)}.");
            }

            if (request.ArrendadorProveedorId.HasValue && request.ArrendadorProveedorId.Value <= 0)
            {
                return Failure("El arrendador ligado debe ser un proveedor válido.");
            }

            var employeeValidationFailure = await ValidateEmployeeIdAsync(request, cancellationToken);
            if (employeeValidationFailure is not null)
            {
                return employeeValidationFailure;
            }

            if (desiredRoleNames.Contains(ArrendadoresRoleName, RoleNameComparer) && !request.ArrendadorProveedorId.HasValue)
            {
                return Failure("Los usuarios con rol Arrendadores deben tener un Arrendador (Proveedor) ligado.");
            }

            if (request.ArrendadorProveedorId.HasValue)
            {
                var providerAlreadyLinked = await _userManager.Users
                    .AsNoTracking()
                    .AnyAsync(user =>
                        user.ArrendadorProveedorId == request.ArrendadorProveedorId.Value
                        && (string.IsNullOrWhiteSpace(request.Id) || user.Id != request.Id),
                        cancellationToken);

                if (providerAlreadyLinked)
                {
                    return Failure("Este arrendador ya está ligado a otro usuario.");
                }
            }

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            var isNewUser = string.IsNullOrWhiteSpace(request.Id);
            var user = isNewUser
                ? new ApplicationUser()
                : await _userManager.FindByIdAsync(request.Id!);

            if (user is null)
            {
                return Failure("No se encontró el usuario solicitado.");
            }

            ApplyUserValues(user, request, normalizedUserName);

            IdentityResult identityResult;
            try
            {
                identityResult = isNewUser
                    ? await _userManager.CreateAsync(user, request.NewPassword!)
                    : await _userManager.UpdateAsync(user);
            }
            catch (DbUpdateException ex)
            {
                return Failure(BuildUserSaveDatabaseFailureMessage(request, ex));
            }

            if (!identityResult.Succeeded)
            {
                return FromIdentityResult(identityResult, "No se pudo guardar el usuario.");
            }

            var currentRoleNames = (await _userManager.GetRolesAsync(user)).ToArray();
            var removesAdministrator = currentRoleNames.Contains(AdministratorRoleName, RoleNameComparer)
                && !desiredRoleNames.Contains(AdministratorRoleName, RoleNameComparer);

            if (removesAdministrator)
            {
                if (string.Equals(request.ActorUserId, user.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return Failure("No puedes quitarte a ti mismo el rol Administrador desde este portal.");
                }

                if (await CountAdministratorUsersAsync(cancellationToken) <= 1)
                {
                    return Failure("No es posible quitar el rol Administrador al último administrador activo.");
                }
            }

            var rolesToRemove = currentRoleNames
                .Where(roleName => !desiredRoleNames.Contains(roleName, RoleNameComparer))
                .ToArray();

            if (rolesToRemove.Length > 0)
            {
                identityResult = await _userManager.RemoveFromRolesAsync(user, rolesToRemove);
                if (!identityResult.Succeeded)
                {
                    return FromIdentityResult(identityResult, "No se pudieron desasignar algunos roles.");
                }
            }

            var rolesToAdd = desiredRoleNames
                .Where(roleName => !currentRoleNames.Contains(roleName, RoleNameComparer))
                .ToArray();

            if (rolesToAdd.Length > 0)
            {
                identityResult = await _userManager.AddToRolesAsync(user, rolesToAdd);
                if (!identityResult.Succeeded)
                {
                    return FromIdentityResult(identityResult, "No se pudieron asignar algunos roles.");
                }
            }

            var claimsChanged = await SyncUserClaimsAsync(user.Id, NormalizeClaims(request.Claims), cancellationToken);
            var rolesChanged = rolesToAdd.Length > 0 || rolesToRemove.Length > 0;
            var passwordChanged = false;

            if (!string.IsNullOrWhiteSpace(request.NewPassword))
            {
                var passwordResult = await SetUserPasswordAsync(user, request.NewPassword);
                if (!passwordResult.Succeeded)
                {
                    return FromIdentityResult(passwordResult, "No se pudo actualizar la contraseña.");
                }

                passwordChanged = true;
            }

            if (rolesChanged || claimsChanged || passwordChanged)
            {
                var securityStampResult = await _userManager.UpdateSecurityStampAsync(user);
                if (!securityStampResult.Succeeded)
                {
                    return FromIdentityResult(securityStampResult, "No se pudo refrescar la sesión de seguridad del usuario.");
                }
            }

            await transaction.CommitAsync(cancellationToken);

            return new IdentityAdminCommandResult(
                true,
                isNewUser ? "Usuario creado correctamente." : "Usuario actualizado correctamente.",
                user.Id,
                user.UserName ?? user.Email ?? normalizedUserName);
        }

        public async Task<IdentityAdminCommandResult> ResetUserPasswordAsync(
            IdentityAdminPasswordResetRequest request,
            CancellationToken cancellationToken = default)
        {
            var normalizedPassword = NormalizeOptional(request.NewPassword);
            if (string.IsNullOrWhiteSpace(request.UserId))
            {
                return Failure("Se requiere un usuario válido para resetear la contraseña.");
            }

            if (string.IsNullOrWhiteSpace(normalizedPassword))
            {
                return Failure("La nueva contraseña es obligatoria.");
            }

            var user = await _userManager.FindByIdAsync(request.UserId);
            if (user is null)
            {
                return Failure("No se encontró el usuario solicitado.");
            }

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            var passwordResult = await SetUserPasswordAsync(user, normalizedPassword);
            if (!passwordResult.Succeeded)
            {
                return FromIdentityResult(passwordResult, "No se pudo restablecer la contraseña.");
            }

            var securityStampResult = await _userManager.UpdateSecurityStampAsync(user);
            if (!securityStampResult.Succeeded)
            {
                return FromIdentityResult(securityStampResult, "No se pudo refrescar la sesión de seguridad del usuario.");
            }

            await transaction.CommitAsync(cancellationToken);

            return new IdentityAdminCommandResult(
                true,
                "Contraseña restablecida correctamente.",
                user.Id,
                user.UserName ?? user.Email ?? request.UserId);
        }

        public async Task<IdentityAdminCommandResult> DeleteUserAsync(string userId, string? actorUserId, CancellationToken cancellationToken = default)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user is null)
            {
                return Failure("No se encontró el usuario solicitado.");
            }

            if (!string.IsNullOrWhiteSpace(actorUserId) &&
                string.Equals(user.Id, actorUserId, StringComparison.OrdinalIgnoreCase))
            {
                return Failure("No puedes eliminar tu propia cuenta desde este portal.");
            }

            var roleNames = await _userManager.GetRolesAsync(user);
            if (roleNames.Contains(AdministratorRoleName, RoleNameComparer) &&
                await CountAdministratorUsersAsync(cancellationToken) <= 1)
            {
                return Failure("No es posible eliminar al último administrador activo.");
            }

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            var identityResult = await _userManager.DeleteAsync(user);
            if (!identityResult.Succeeded)
            {
                return FromIdentityResult(identityResult, "No se pudo eliminar el usuario.");
            }

            await transaction.CommitAsync(cancellationToken);

            return new IdentityAdminCommandResult(
                true,
                "Usuario eliminado correctamente.",
                EntityName: user.UserName ?? user.Email ?? userId);
        }

        public async Task<IdentityAdminCommandResult> SaveRoleAsync(IdentityRoleUpsertRequest request, CancellationToken cancellationToken = default)
        {
            var normalizedRoleName = NormalizeRequired(request.Name);
            var desiredUserIds = NormalizeEntityIds(request.UserIds);
            if (string.IsNullOrWhiteSpace(normalizedRoleName))
            {
                return Failure("El nombre del rol es obligatorio.");
            }

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            var isNewRole = string.IsNullOrWhiteSpace(request.Id);
            var role = isNewRole
                ? new IdentityRole(normalizedRoleName)
                : await _roleManager.FindByIdAsync(request.Id!);

            if (role is null)
            {
                return Failure("No se encontró el rol solicitado.");
            }

            var isAdministratorRole = string.Equals(role.NormalizedName, AdministratorRoleNormalizedName, StringComparison.OrdinalIgnoreCase)
                || RoleNameComparer.Equals(role.Name, AdministratorRoleName);

            if (isAdministratorRole)
            {
                if (!RoleNameComparer.Equals(normalizedRoleName, AdministratorRoleName))
                {
                    return Failure("El rol Administrador está protegido y no se puede renombrar.");
                }

                normalizedRoleName = AdministratorRoleName;
            }

            if (!isNewRole)
            {
                role.Name = normalizedRoleName;
            }

            var identityResult = isNewRole
                ? await _roleManager.CreateAsync(role)
                : await _roleManager.UpdateAsync(role);

            if (!identityResult.Succeeded)
            {
                return FromIdentityResult(identityResult, "No se pudo guardar el rol.");
            }

            await SyncRoleClaimsAsync(role.Id, NormalizeClaims(request.Claims), cancellationToken);
            var syncRoleUsersFailure = await SyncRoleUsersAsync(role, desiredUserIds, request.ActorUserId, cancellationToken);
            if (syncRoleUsersFailure is not null)
            {
                return syncRoleUsersFailure;
            }

            await transaction.CommitAsync(cancellationToken);

            return new IdentityAdminCommandResult(
                true,
                isNewRole ? "Rol creado correctamente." : "Rol actualizado correctamente.",
                role.Id,
                role.Name ?? normalizedRoleName);
        }

        public async Task<IdentityAdminCommandResult> DeleteRoleAsync(string roleId, CancellationToken cancellationToken = default)
        {
            var role = await _roleManager.FindByIdAsync(roleId);
            if (role is null)
            {
                return Failure("No se encontró el rol solicitado.");
            }

            if (RoleNameComparer.Equals(role.Name, AdministratorRoleName))
            {
                return Failure("El rol Administrador está protegido y no se puede eliminar.");
            }

            var assignedUserCount = await _db.Set<IdentityUserRole<string>>()
                .AsNoTracking()
                .CountAsync(link => link.RoleId == roleId, cancellationToken);

            if (assignedUserCount > 0)
            {
                return Failure("No se puede eliminar un rol que todavía está asignado a usuarios.");
            }

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            var identityResult = await _roleManager.DeleteAsync(role);
            if (!identityResult.Succeeded)
            {
                return FromIdentityResult(identityResult, "No se pudo eliminar el rol.");
            }

            await transaction.CommitAsync(cancellationToken);

            return new IdentityAdminCommandResult(
                true,
                "Rol eliminado correctamente.",
                EntityName: role.Name ?? roleId);
        }

        private async Task<int> CountAdministratorUsersAsync(CancellationToken cancellationToken)
        {
            return await (
                from link in _db.Set<IdentityUserRole<string>>().AsNoTracking()
                join role in _roleManager.Roles.AsNoTracking() on link.RoleId equals role.Id
                where role.NormalizedName == AdministratorRoleNormalizedName
                select link.UserId)
                .Distinct()
                .CountAsync(cancellationToken);
        }

        private async Task<IdentityResult> SetUserPasswordAsync(ApplicationUser user, string newPassword)
        {
            if (await _userManager.HasPasswordAsync(user))
            {
                var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
                return await _userManager.ResetPasswordAsync(user, resetToken, newPassword);
            }

            return await _userManager.AddPasswordAsync(user, newPassword);
        }

        private void ApplyUserValues(ApplicationUser user, IdentityUserUpsertRequest request, string normalizedUserName)
        {
            var email = NormalizeOptional(request.Email);
            var phoneNumber = NormalizeOptional(request.PhoneNumber);

            user.UserName = normalizedUserName;
            user.Email = email;
            user.PhoneNumber = phoneNumber;
            user.EmployeeId = request.EmployeeId;
            user.ArrendadorProveedorId = request.ArrendadorProveedorId;
            user.EmailConfirmed = email is not null && request.EmailConfirmed;
            user.PhoneNumberConfirmed = phoneNumber is not null && request.PhoneNumberConfirmed;
            user.TwoFactorEnabled = request.TwoFactorEnabled;
            user.LockoutEnabled = request.LockoutEnabled;
            user.LockoutEnd = request.LockoutEnabled ? request.LockoutEnd : null;
        }

        private async Task<bool> SyncUserClaimsAsync(
            string userId,
            IReadOnlyList<ClaimSignature> desiredClaims,
            CancellationToken cancellationToken)
        {
            var userClaimSet = _db.Set<IdentityUserClaim<string>>();
            var existingClaims = await userClaimSet
                .Where(claim => claim.UserId == userId)
                .ToListAsync(cancellationToken);

            var changed = false;
            var desiredClaimSet = desiredClaims.ToHashSet();
            var existingClaimSet = new HashSet<ClaimSignature>();

            foreach (var group in existingClaims.GroupBy(claim => new ClaimSignature(
                         NormalizeRequired(claim.ClaimType),
                         NormalizeRequired(claim.ClaimValue))))
            {
                if (!desiredClaimSet.Contains(group.Key) || string.IsNullOrWhiteSpace(group.Key.ClaimType) || string.IsNullOrWhiteSpace(group.Key.ClaimValue))
                {
                    userClaimSet.RemoveRange(group);
                    changed = true;
                    continue;
                }

                existingClaimSet.Add(group.Key);
                var duplicates = group.Skip(1).ToArray();
                if (duplicates.Length > 0)
                {
                    userClaimSet.RemoveRange(duplicates);
                    changed = true;
                }
            }

            foreach (var desiredClaim in desiredClaims)
            {
                if (existingClaimSet.Contains(desiredClaim))
                {
                    continue;
                }

                await userClaimSet.AddAsync(new IdentityUserClaim<string>
                {
                    UserId = userId,
                    ClaimType = desiredClaim.ClaimType,
                    ClaimValue = desiredClaim.ClaimValue
                }, cancellationToken);

                changed = true;
            }

            if (changed)
            {
                await _db.SaveChangesAsync(cancellationToken);
            }

            return changed;
        }

        private async Task<IdentityAdminCommandResult?> ValidateEmployeeIdAsync(
            IdentityUserUpsertRequest request,
            CancellationToken cancellationToken)
        {
            if (!request.EmployeeId.HasValue)
            {
                return null;
            }

            var employeeId = request.EmployeeId.Value;
            if (employeeId <= 0)
            {
                return Failure("EmployeeId debe ser un número mayor a cero.");
            }

            if (!_db.Database.IsRelational())
            {
                return null;
            }

            try
            {
                var employee = await GetEmployeeLinkTargetAsync(employeeId, cancellationToken);
                if (employee is null)
                {
                    return Failure($"EmployeeId {employeeId} no existe en Capital Humano (dbo.Capital_Humano.ID). Verifica el ID en el módulo Capital Humano antes de guardar el usuario.");
                }

                var foreignKey = await GetEmployeeIdForeignKeyAsync(cancellationToken);
                if (foreignKey is not null && !foreignKey.PointsToCapitalHumano)
                {
                    return Failure(
                        $"EmployeeId {employeeId} existe en Capital Humano ({employee.Describe()}), pero la base de datos tiene la llave foránea {foreignKey.Name} de auth.AspNetUsers.EmployeeId apuntando a {foreignKey.Target} en lugar de dbo.Capital_Humano.ID. SQL Server rechazará esta asignación aunque el empleado sea válido. Ejecuta la reparación de la llave foránea de AspNetUsers.EmployeeId y vuelve a guardar.");
                }

                return null;
            }
            catch (DbException ex)
            {
                return Failure(
                    $"No se pudo validar EmployeeId {employeeId} contra Capital Humano antes de guardar el usuario.",
                    GetBaseExceptionMessage(ex));
            }
        }

        private async Task<EmployeeLinkTarget?> GetEmployeeLinkTargetAsync(int employeeId, CancellationToken cancellationToken)
        {
            var connection = _db.Database.GetDbConnection();
            var shouldClose = connection.State == ConnectionState.Closed;
            if (shouldClose)
            {
                await connection.OpenAsync(cancellationToken);
            }

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT TOP (1)
                        ch.ID,
                        NULLIF(LTRIM(RTRIM(ch.RFC)), '') AS Rfc,
                        NULLIF(LTRIM(RTRIM(ch.NombreCorto)), '') AS NombreCorto,
                        NULLIF(LTRIM(RTRIM(CONCAT(
                            ISNULL(ch.Nombre, ''),
                            ' ',
                            ISNULL(ch.ApellidoPaterno, ''),
                            ' ',
                            ISNULL(ch.ApellidoMaterno, '')))), '') AS NombreCompleto,
                        NULLIF(LTRIM(RTRIM(ch.[Status])), '') AS [Status]
                    FROM dbo.Capital_Humano ch
                    WHERE ch.ID = @EmployeeId;
                    """;
                AddParameter(command, "@EmployeeId", employeeId);

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    return null;
                }

                var shortName = ReadNullableString(reader, "NombreCorto");
                var fullName = ReadNullableString(reader, "NombreCompleto");
                var displayName = !string.IsNullOrWhiteSpace(shortName)
                    ? shortName
                    : !string.IsNullOrWhiteSpace(fullName)
                        ? fullName
                        : $"ID {employeeId}";

                return new EmployeeLinkTarget(
                    employeeId,
                    displayName,
                    ReadNullableString(reader, "Rfc"),
                    ReadNullableString(reader, "Status"));
            }
            finally
            {
                if (shouldClose)
                {
                    await connection.CloseAsync();
                }
            }
        }

        private async Task<EmployeeIdForeignKey?> GetEmployeeIdForeignKeyAsync(CancellationToken cancellationToken)
        {
            var connection = _db.Database.GetDbConnection();
            var shouldClose = connection.State == ConnectionState.Closed;
            if (shouldClose)
            {
                await connection.OpenAsync(cancellationToken);
            }

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT TOP (1)
                        fk.name AS ForeignKeyName,
                        principal_schema.name AS PrincipalSchema,
                        principal_table.name AS PrincipalTable,
                        principal_column.name AS PrincipalColumn
                    FROM sys.foreign_keys fk
                    JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
                    JOIN sys.tables parent_table ON parent_table.object_id = fkc.parent_object_id
                    JOIN sys.schemas parent_schema ON parent_schema.schema_id = parent_table.schema_id
                    JOIN sys.columns parent_column
                        ON parent_column.object_id = parent_table.object_id
                        AND parent_column.column_id = fkc.parent_column_id
                    JOIN sys.tables principal_table ON principal_table.object_id = fkc.referenced_object_id
                    JOIN sys.schemas principal_schema ON principal_schema.schema_id = principal_table.schema_id
                    JOIN sys.columns principal_column
                        ON principal_column.object_id = principal_table.object_id
                        AND principal_column.column_id = fkc.referenced_column_id
                    WHERE parent_schema.name = 'auth'
                      AND parent_table.name = 'AspNetUsers'
                      AND parent_column.name = 'EmployeeId'
                    ORDER BY
                        CASE
                            WHEN principal_schema.name = 'dbo'
                             AND principal_table.name = 'Capital_Humano'
                             AND principal_column.name = 'ID'
                            THEN 1
                            ELSE 0
                        END,
                        fk.name;
                    """;

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    return null;
                }

                return new EmployeeIdForeignKey(
                    ReadRequiredString(reader, "ForeignKeyName"),
                    ReadRequiredString(reader, "PrincipalSchema"),
                    ReadRequiredString(reader, "PrincipalTable"),
                    ReadRequiredString(reader, "PrincipalColumn"));
            }
            finally
            {
                if (shouldClose)
                {
                    await connection.CloseAsync();
                }
            }
        }

        private async Task<bool> SyncRoleClaimsAsync(
            string roleId,
            IReadOnlyList<ClaimSignature> desiredClaims,
            CancellationToken cancellationToken)
        {
            var roleClaimSet = _db.Set<IdentityRoleClaim<string>>();
            var existingClaims = await roleClaimSet
                .Where(claim => claim.RoleId == roleId)
                .ToListAsync(cancellationToken);

            var changed = false;
            var desiredClaimSet = desiredClaims.ToHashSet();
            var existingClaimSet = new HashSet<ClaimSignature>();

            foreach (var group in existingClaims.GroupBy(claim => new ClaimSignature(
                         NormalizeRequired(claim.ClaimType),
                         NormalizeRequired(claim.ClaimValue))))
            {
                if (!desiredClaimSet.Contains(group.Key) || string.IsNullOrWhiteSpace(group.Key.ClaimType) || string.IsNullOrWhiteSpace(group.Key.ClaimValue))
                {
                    roleClaimSet.RemoveRange(group);
                    changed = true;
                    continue;
                }

                existingClaimSet.Add(group.Key);
                var duplicates = group.Skip(1).ToArray();
                if (duplicates.Length > 0)
                {
                    roleClaimSet.RemoveRange(duplicates);
                    changed = true;
                }
            }

            foreach (var desiredClaim in desiredClaims)
            {
                if (existingClaimSet.Contains(desiredClaim))
                {
                    continue;
                }

                await roleClaimSet.AddAsync(new IdentityRoleClaim<string>
                {
                    RoleId = roleId,
                    ClaimType = desiredClaim.ClaimType,
                    ClaimValue = desiredClaim.ClaimValue
                }, cancellationToken);

                changed = true;
            }

            if (changed)
            {
                await _db.SaveChangesAsync(cancellationToken);
            }

            return changed;
        }

        private async Task<IdentityAdminCommandResult?> SyncRoleUsersAsync(
            IdentityRole role,
            IReadOnlyList<string> desiredUserIds,
            string? actorUserId,
            CancellationToken cancellationToken)
        {
            var roleName = NormalizeRequired(role.Name);
            if (string.IsNullOrWhiteSpace(roleName))
            {
                return Failure("El rol solicitado no tiene un nombre válido.");
            }

            var currentUserIds = await _db.Set<IdentityUserRole<string>>()
                .AsNoTracking()
                .Where(link => link.RoleId == role.Id)
                .Select(link => link.UserId)
                .Distinct()
                .ToArrayAsync(cancellationToken);

            var currentUserIdSet = currentUserIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var desiredUserIdSet = desiredUserIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var referencedUserIds = currentUserIds
                .Concat(desiredUserIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var usersById = referencedUserIds.Length == 0
                ? new Dictionary<string, ApplicationUser>(StringComparer.OrdinalIgnoreCase)
                : await _userManager.Users
                    .Where(user => referencedUserIds.Contains(user.Id))
                    .ToDictionaryAsync(user => user.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);

            var unknownUserIds = desiredUserIds
                .Where(userId => !usersById.ContainsKey(userId))
                .ToArray();

            if (unknownUserIds.Length > 0)
            {
                return Failure($"No se encontraron los usuarios solicitados: {string.Join(", ", unknownUserIds)}.");
            }

            if (RoleNameComparer.Equals(roleName, AdministratorRoleName))
            {
                var normalizedActorUserId = NormalizeOptional(actorUserId);
                if (!string.IsNullOrWhiteSpace(normalizedActorUserId)
                    && currentUserIdSet.Contains(normalizedActorUserId)
                    && !desiredUserIdSet.Contains(normalizedActorUserId))
                {
                    return Failure("No puedes quitarte a ti mismo el rol Administrador desde este portal.");
                }

                if (desiredUserIdSet.Count == 0)
                {
                    return Failure("No es posible quitar el rol Administrador al último administrador activo.");
                }
            }

            var userIdsToRemove = currentUserIds
                .Where(userId => !desiredUserIdSet.Contains(userId))
                .ToArray();

            foreach (var userId in userIdsToRemove)
            {
                if (!usersById.TryGetValue(userId, out var user))
                {
                    continue;
                }

                var identityResult = await _userManager.RemoveFromRoleAsync(user, roleName);
                if (!identityResult.Succeeded)
                {
                    return FromIdentityResult(identityResult, "No se pudieron actualizar algunas asignaciones de rol.");
                }

                identityResult = await _userManager.UpdateSecurityStampAsync(user);
                if (!identityResult.Succeeded)
                {
                    return FromIdentityResult(identityResult, "No se pudo refrescar la sesión de seguridad de algunos usuarios.");
                }
            }

            var userIdsToAdd = desiredUserIds
                .Where(userId => !currentUserIdSet.Contains(userId))
                .ToArray();

            foreach (var userId in userIdsToAdd)
            {
                if (!usersById.TryGetValue(userId, out var user))
                {
                    continue;
                }

                var identityResult = await _userManager.AddToRoleAsync(user, roleName);
                if (!identityResult.Succeeded)
                {
                    return FromIdentityResult(identityResult, "No se pudieron actualizar algunas asignaciones de rol.");
                }

                identityResult = await _userManager.UpdateSecurityStampAsync(user);
                if (!identityResult.Succeeded)
                {
                    return FromIdentityResult(identityResult, "No se pudo refrescar la sesión de seguridad de algunos usuarios.");
                }
            }

            return null;
        }

        private static IReadOnlyList<ClaimSignature> NormalizeClaims(IReadOnlyList<IdentityClaimInput> claims)
        {
            return claims
                .Select(claim => new ClaimSignature(
                    NormalizeRequired(claim.ClaimType),
                    NormalizeRequired(claim.ClaimValue)))
                .Where(claim => !string.IsNullOrWhiteSpace(claim.ClaimType) && !string.IsNullOrWhiteSpace(claim.ClaimValue))
                .Distinct()
                .ToArray();
        }

        private static string[] NormalizeRoleNames(IReadOnlyList<string> roleNames)
        {
            return roleNames
                .Select(NormalizeRequired)
                .Where(roleName => !string.IsNullOrWhiteSpace(roleName))
                .Distinct(RoleNameComparer)
                .ToArray();
        }

        private static string[] NormalizeEntityIds(IReadOnlyList<string> entityIds)
        {
            return entityIds
                .Select(NormalizeRequired)
                .Where(entityId => !string.IsNullOrWhiteSpace(entityId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string NormalizeRequired(string? value)
            => value?.Trim() ?? string.Empty;

        private static string? NormalizeOptional(string? value)
        {
            var normalized = value?.Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static IdentityAdminCommandResult Failure(string message, params string[] errors)
            => new(false, message, Errors: errors.Length == 0 ? null : errors);

        private static IdentityAdminCommandResult FromIdentityResult(IdentityResult result, string message)
            => new(
                false,
                message,
                Errors: result.Errors.Select(error => error.Description).ToArray());

        private static string BuildUserSaveDatabaseFailureMessage(IdentityUserUpsertRequest request, DbUpdateException exception)
        {
            var detail = GetBaseExceptionMessage(exception);
            if (request.EmployeeId.HasValue &&
                detail.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase) &&
                detail.Contains("EmployeeId", StringComparison.OrdinalIgnoreCase))
            {
                return $"No se pudo guardar el usuario porque SQL Server rechazó EmployeeId {request.EmployeeId.Value}. El empleado debe existir en dbo.Capital_Humano.ID y la llave foránea auth.AspNetUsers.EmployeeId debe apuntar a Capital Humano. Detalle de base de datos: {detail}";
            }

            return $"No se pudo guardar el usuario por un error de base de datos. Detalle: {detail}";
        }

        private static void AddParameter(DbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        private static string? ReadNullableString(DbDataReader reader, string name)
        {
            var ordinal = reader.GetOrdinal(name);
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }

        private static string ReadRequiredString(DbDataReader reader, string name)
            => ReadNullableString(reader, name) ?? string.Empty;

        private static string GetBaseExceptionMessage(Exception exception)
            => exception.GetBaseException().Message;

        private static string? CreateTokenPreview(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (value.Length <= 4)
            {
                return "oculto";
            }

            return $"{value[..2]}...{value[^2..]}";
        }

        private readonly record struct ClaimSignature(string ClaimType, string ClaimValue);

        private sealed record EmployeeLinkTarget(int Id, string DisplayName, string? Rfc, string? Status)
        {
            public string Describe()
            {
                var parts = new List<string> { DisplayName, $"ID {Id}" };
                if (!string.IsNullOrWhiteSpace(Rfc))
                {
                    parts.Add($"RFC empresa {Rfc}");
                }

                if (!string.IsNullOrWhiteSpace(Status))
                {
                    parts.Add($"status {Status}");
                }

                return string.Join(", ", parts);
            }
        }

        private sealed record EmployeeIdForeignKey(
            string Name,
            string PrincipalSchema,
            string PrincipalTable,
            string PrincipalColumn)
        {
            public bool PointsToCapitalHumano =>
                string.Equals(PrincipalSchema, "dbo", StringComparison.OrdinalIgnoreCase)
                && string.Equals(PrincipalTable, "Capital_Humano", StringComparison.OrdinalIgnoreCase)
                && string.Equals(PrincipalColumn, "ID", StringComparison.OrdinalIgnoreCase);

            public string Target => $"{PrincipalSchema}.{PrincipalTable}.{PrincipalColumn}";
        }
    }
}
