using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using OrionERP.Application.Features.Arrendadores;
using OrionERP.Application.Features.Auth.AdminPortal;
using OrionERP.Web.Services;

namespace OrionERP.Web.Features.Auth.AdminPortal
{
    public partial class IdentityAdminPage : ComponentBase
    {
        private const string AdministratorRoleName = "Administrador";
        private const string PasswordPolicySummary = "Minimo 8 caracteres, al menos 1 digito y 1 minuscula.";
        private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        private const string PasswordLowerAlphabet = "abcdefghijkmnopqrstuvwxyz";
        private const string PasswordUpperAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        private const string PasswordDigitAlphabet = "23456789";
        private static readonly StringComparer RoleNameComparer = StringComparer.OrdinalIgnoreCase;

        [Inject] private IIdentityAdminService IdentityAdminService { get; set; } = default!;
        [Inject] private IArrendadoresEstadoCuentaService ArrendadoresService { get; set; } = default!;
        [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
        [Inject] private IUiMessageService UiMessages { get; set; } = default!;
        [Inject] private IJSRuntime JS { get; set; } = default!;

        private IdentityAdminPortalSnapshot? Snapshot { get; set; }
        private IReadOnlyList<ArrendadorListItemDto> ArrendadorOptions { get; set; } = Array.Empty<ArrendadorListItemDto>();
        private UserEditorModel UserForm { get; set; } = CreateEmptyUserModel();
        private RoleEditorModel RoleForm { get; set; } = CreateEmptyRoleModel();
        private IdentityAdminTab ActiveTab { get; set; } = IdentityAdminTab.Users;
        private string? SelectedUserId { get; set; }
        private string? SelectedRoleId { get; set; }
        private string UserSearch { get; set; } = string.Empty;
        private string RoleSearch { get; set; } = string.Empty;
        private string RoleMemberSearch { get; set; } = string.Empty;
        private string CurrentUserId { get; set; } = string.Empty;
        private bool IsRefreshing { get; set; }
        private bool IsSavingUser { get; set; }
        private bool IsResettingPassword { get; set; }
        private bool IsDeletingUser { get; set; }
        private bool IsSavingRole { get; set; }
        private bool IsDeletingRole { get; set; }
        private string? LoadError { get; set; }
        private DateTimeOffset? LastRefreshedAt { get; set; }

        private bool IsUserBusy => IsRefreshing || IsSavingUser || IsResettingPassword || IsDeletingUser;
        private bool IsRoleBusy => IsRefreshing || IsSavingRole || IsDeletingRole;
        private int AdministratorCount => Snapshot?.Users.Count(user => user.Roles.Contains(AdministratorRoleName, RoleNameComparer)) ?? 0;

        private IEnumerable<IdentityUserSummary> FilteredUsers =>
            (Snapshot?.Users ?? Array.Empty<IdentityUserSummary>())
            .Where(user => MatchesUserFilter(user, UserSearch));

        private IEnumerable<IdentityRoleSummary> FilteredRoles =>
            (Snapshot?.Roles ?? Array.Empty<IdentityRoleSummary>())
            .Where(role => MatchesRoleFilter(role, RoleSearch));

        private bool CanDeleteSelectedUser =>
            !UserForm.IsNew &&
            !IsUserBusy &&
            !string.Equals(UserForm.Id, CurrentUserId, StringComparison.OrdinalIgnoreCase) &&
            !(UserForm.AssignedRoles.Contains(AdministratorRoleName) && AdministratorCount <= 1);

        private bool CanDeleteSelectedRole =>
            !RoleForm.IsNew &&
            !IsRoleBusy &&
            !IsProtectedSelectedRole &&
            RoleForm.Users.Count == 0 &&
            RoleForm.AssignedUserIds.Count == 0;

        private bool IsProtectedSelectedRole =>
            !RoleForm.IsNew &&
            string.Equals(RoleForm.Name, AdministratorRoleName, StringComparison.OrdinalIgnoreCase);

        private IEnumerable<IdentityUserSummary> FilteredRoleUsers =>
            (Snapshot?.Users ?? Array.Empty<IdentityUserSummary>())
            .Where(user => MatchesUserFilter(user, RoleMemberSearch))
            .OrderByDescending(user => RoleForm.AssignedUserIds.Contains(user.Id))
            .ThenBy(user => user.UserName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(user => user.Email ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        private IEnumerable<IdentityUserSummary> SelectedRoleUsers =>
            (Snapshot?.Users ?? Array.Empty<IdentityUserSummary>())
            .Where(user => RoleForm.AssignedUserIds.Contains(user.Id))
            .OrderBy(user => user.UserName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(user => user.Email ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        protected override async Task OnInitializedAsync()
        {
            var authenticationState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            CurrentUserId = authenticationState.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
            await RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            await LoadPortalAsync(SelectedUserId, SelectedRoleId);
        }

        private async Task SetActiveTabAsync(IdentityAdminTab tab)
        {
            if (ActiveTab == tab)
            {
                return;
            }

            ActiveTab = tab;
            LoadError = null;

            if (tab == IdentityAdminTab.Users)
            {
                var targetUserId = ResolveExistingUserId(SelectedUserId) ?? ResolveDefaultUserId();
                if (targetUserId is null)
                {
                    CreateNewUser();
                    return;
                }

                await LoadUserAsync(targetUserId);
                return;
            }

            var targetRoleId = ResolveExistingRoleId(SelectedRoleId) ?? ResolveDefaultRoleId();
            if (targetRoleId is null)
            {
                CreateNewRole();
                return;
            }

            await LoadRoleAsync(targetRoleId);
        }

        private void CreateNewUser()
        {
            ActiveTab = IdentityAdminTab.Users;
            SelectedUserId = null;
            UserForm = CreateEmptyUserModel();
        }

        private void CreateNewRole()
        {
            ActiveTab = IdentityAdminTab.Roles;
            SelectedRoleId = null;
            RoleMemberSearch = string.Empty;
            RoleForm = CreateEmptyRoleModel();
        }

        private async Task SelectUserAsync(string userId)
        {
            if (string.Equals(userId, SelectedUserId, StringComparison.OrdinalIgnoreCase) && !UserForm.IsNew)
            {
                return;
            }

            ActiveTab = IdentityAdminTab.Users;
            await LoadUserAsync(userId);
        }

        private async Task SelectRoleAsync(string roleId)
        {
            if (string.Equals(roleId, SelectedRoleId, StringComparison.OrdinalIgnoreCase) && !RoleForm.IsNew)
            {
                return;
            }

            ActiveTab = IdentityAdminTab.Roles;
            await LoadRoleAsync(roleId);
        }

        private async Task SaveUserAsync()
        {
            if (IsUserBusy)
            {
                return;
            }

            IsSavingUser = true;
            LoadError = null;

            try
            {
                var result = await IdentityAdminService.SaveUserAsync(BuildUserRequest());
                if (!result.Succeeded)
                {
                    UiMessages.ShowError(BuildFailureMessage(result), "No se pudo guardar el usuario");
                    return;
                }

                UiMessages.ShowSuccess(result.Message, "Seguridad");
                await LoadPortalAsync(result.EntityId ?? SelectedUserId, SelectedRoleId);
            }
            catch (Exception ex)
            {
                UiMessages.ShowError(ex.Message, "No se pudo guardar el usuario");
            }
            finally
            {
                IsSavingUser = false;
            }
        }

        private async Task ResetUserPasswordAsync()
        {
            if (IsUserBusy)
            {
                return;
            }

            if (UserForm.IsNew || string.IsNullOrWhiteSpace(UserForm.Id))
            {
                UiMessages.ShowWarning("Guarda el usuario antes de restablecer su contrasena.", "Seguridad");
                return;
            }

            var newPassword = EmptyToNull(UserForm.PasswordResetInput);
            var confirmPassword = EmptyToNull(UserForm.PasswordResetConfirmInput);

            if (string.IsNullOrWhiteSpace(newPassword))
            {
                UiMessages.ShowWarning($"Captura una contrasena temporal. {PasswordPolicySummary}", "Seguridad");
                return;
            }

            if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
            {
                UiMessages.ShowWarning("La confirmacion de contrasena no coincide.", "Seguridad");
                return;
            }

            var confirmed = await JS.InvokeAsync<bool>(
                "confirm",
                $"Vas a restablecer manualmente la contrasena de '{UserForm.DisplayName}'. ¿Deseas continuar?");

            if (!confirmed)
            {
                return;
            }

            IsResettingPassword = true;
            LoadError = null;

            try
            {
                var result = await IdentityAdminService.ResetUserPasswordAsync(new IdentityAdminPasswordResetRequest(UserForm.Id, newPassword));
                if (!result.Succeeded)
                {
                    UiMessages.ShowError(BuildFailureMessage(result), "No se pudo restablecer la contrasena");
                    return;
                }

                UserForm.PasswordResetInput = string.Empty;
                UserForm.PasswordResetConfirmInput = string.Empty;

                UiMessages.ShowSuccess(result.Message, "Seguridad");
                await LoadPortalAsync(result.EntityId ?? SelectedUserId, SelectedRoleId);
            }
            catch (Exception ex)
            {
                UiMessages.ShowError(ex.Message, "No se pudo restablecer la contrasena");
            }
            finally
            {
                IsResettingPassword = false;
            }
        }

        private async Task DeleteUserAsync()
        {
            if (!CanDeleteSelectedUser || string.IsNullOrWhiteSpace(UserForm.Id))
            {
                return;
            }

            var confirmed = await JS.InvokeAsync<bool>(
                "confirm",
                $"Eliminar al usuario '{UserForm.DisplayName}' quitará sus claims, roles y accesos asociados. ¿Deseas continuar?");

            if (!confirmed)
            {
                return;
            }

            IsDeletingUser = true;
            LoadError = null;

            try
            {
                var result = await IdentityAdminService.DeleteUserAsync(UserForm.Id, CurrentUserId);
                if (!result.Succeeded)
                {
                    UiMessages.ShowError(BuildFailureMessage(result), "No se pudo eliminar el usuario");
                    return;
                }

                UiMessages.ShowSuccess(result.Message, "Seguridad");
                SelectedUserId = null;
                UserForm = CreateEmptyUserModel();
                await LoadPortalAsync(ResolveDefaultUserId(), SelectedRoleId);
            }
            catch (Exception ex)
            {
                UiMessages.ShowError(ex.Message, "No se pudo eliminar el usuario");
            }
            finally
            {
                IsDeletingUser = false;
            }
        }

        private async Task SaveRoleAsync()
        {
            if (IsRoleBusy)
            {
                return;
            }

            IsSavingRole = true;
            LoadError = null;

            try
            {
                var result = await IdentityAdminService.SaveRoleAsync(BuildRoleRequest());
                if (!result.Succeeded)
                {
                    UiMessages.ShowError(BuildFailureMessage(result), "No se pudo guardar el rol");
                    return;
                }

                UiMessages.ShowSuccess(result.Message, "Seguridad");
                await LoadPortalAsync(SelectedUserId, result.EntityId ?? SelectedRoleId);
            }
            catch (Exception ex)
            {
                UiMessages.ShowError(ex.Message, "No se pudo guardar el rol");
            }
            finally
            {
                IsSavingRole = false;
            }
        }

        private async Task DeleteRoleAsync()
        {
            if (!CanDeleteSelectedRole || string.IsNullOrWhiteSpace(RoleForm.Id))
            {
                return;
            }

            var confirmed = await JS.InvokeAsync<bool>(
                "confirm",
                $"Eliminar el rol '{RoleForm.DisplayName}' quitará sus claims asociados. ¿Deseas continuar?");

            if (!confirmed)
            {
                return;
            }

            IsDeletingRole = true;
            LoadError = null;

            try
            {
                var result = await IdentityAdminService.DeleteRoleAsync(RoleForm.Id);
                if (!result.Succeeded)
                {
                    UiMessages.ShowError(BuildFailureMessage(result), "No se pudo eliminar el rol");
                    return;
                }

                UiMessages.ShowSuccess(result.Message, "Seguridad");
                SelectedRoleId = null;
                RoleForm = CreateEmptyRoleModel();
                await LoadPortalAsync(SelectedUserId, ResolveDefaultRoleId());
            }
            catch (Exception ex)
            {
                UiMessages.ShowError(ex.Message, "No se pudo eliminar el rol");
            }
            finally
            {
                IsDeletingRole = false;
            }
        }

        private void ToggleUserRole(string roleName, ChangeEventArgs args)
        {
            if (GetCheckboxValue(args))
            {
                UserForm.AssignedRoles.Add(roleName);
                return;
            }

            UserForm.AssignedRoles.Remove(roleName);
        }

        private void AddUserClaim() => UserForm.Claims.Add(new ClaimRowModel());

        private void RemoveUserClaim(Guid claimKey)
        {
            var existingClaim = UserForm.Claims.FirstOrDefault(claim => claim.Key == claimKey);
            if (existingClaim is not null)
            {
                UserForm.Claims.Remove(existingClaim);
            }
        }

        private void AddRoleClaim() => RoleForm.Claims.Add(new ClaimRowModel());

        private void ToggleRoleUser(string userId, ChangeEventArgs args)
        {
            if (GetCheckboxValue(args))
            {
                RoleForm.AssignedUserIds.Add(userId);
                return;
            }

            RoleForm.AssignedUserIds.Remove(userId);
        }

        private void RemoveRoleClaim(Guid claimKey)
        {
            var existingClaim = RoleForm.Claims.FirstOrDefault(claim => claim.Key == claimKey);
            if (existingClaim is not null)
            {
                RoleForm.Claims.Remove(existingClaim);
            }
        }

        private async Task LoadPortalAsync(string? preferredUserId, string? preferredRoleId)
        {
            IsRefreshing = true;
            LoadError = null;

            try
            {
                Snapshot = await IdentityAdminService.GetPortalSnapshotAsync();
                ArrendadorOptions = await ArrendadoresService.GetArrendadoresAsync();
                LastRefreshedAt = DateTimeOffset.Now;

                preferredUserId = ResolveExistingUserId(preferredUserId) ?? ResolveDefaultUserId();
                preferredRoleId = ResolveExistingRoleId(preferredRoleId) ?? ResolveDefaultRoleId();

                if (ActiveTab == IdentityAdminTab.Users)
                {
                    if (preferredUserId is null)
                    {
                        CreateNewUser();
                    }
                    else
                    {
                        await LoadUserAsync(preferredUserId);
                    }

                    if (preferredRoleId is not null)
                    {
                        SelectedRoleId = preferredRoleId;
                    }
                }
                else
                {
                    if (preferredRoleId is null)
                    {
                        CreateNewRole();
                    }
                    else
                    {
                        await LoadRoleAsync(preferredRoleId);
                    }

                    if (preferredUserId is not null)
                    {
                        SelectedUserId = preferredUserId;
                    }
                }
            }
            catch (Exception ex)
            {
                LoadError = ex.Message;
                UiMessages.ShowError(ex.Message, "No se pudo cargar el portal de seguridad");
            }
            finally
            {
                IsRefreshing = false;
            }
        }

        private async Task LoadUserAsync(string userId)
        {
            var editor = await IdentityAdminService.GetUserAsync(userId);
            if (editor is null)
            {
                CreateNewUser();
                UiMessages.ShowWarning("El usuario seleccionado ya no existe.", "Seguridad");
                return;
            }

            SelectedUserId = userId;
            UserForm = MapUserEditor(editor);
        }

        private async Task LoadRoleAsync(string roleId)
        {
            var editor = await IdentityAdminService.GetRoleAsync(roleId);
            if (editor is null)
            {
                CreateNewRole();
                UiMessages.ShowWarning("El rol seleccionado ya no existe.", "Seguridad");
                return;
            }

            SelectedRoleId = roleId;
            RoleForm = MapRoleEditor(editor);
        }

        private string? ResolveExistingUserId(string? candidateUserId)
        {
            return Snapshot?.Users.Any(user => string.Equals(user.Id, candidateUserId, StringComparison.OrdinalIgnoreCase)) == true
                ? candidateUserId
                : null;
        }

        private string? ResolveExistingRoleId(string? candidateRoleId)
        {
            return Snapshot?.Roles.Any(role => string.Equals(role.Id, candidateRoleId, StringComparison.OrdinalIgnoreCase)) == true
                ? candidateRoleId
                : null;
        }

        private string? ResolveDefaultUserId()
        {
            return ResolveExistingUserId(CurrentUserId)
                ?? Snapshot?.Users.FirstOrDefault()?.Id;
        }

        private string? ResolveDefaultRoleId()
        {
            return Snapshot?.Roles
                .OrderByDescending(role => string.Equals(role.Name, AdministratorRoleName, StringComparison.OrdinalIgnoreCase))
                .ThenBy(role => role.Name, RoleNameComparer)
                .Select(role => role.Id)
                .FirstOrDefault();
        }

        private IdentityUserUpsertRequest BuildUserRequest()
        {
            return new IdentityUserUpsertRequest(
                UserForm.Id,
                CurrentUserId,
                UserForm.UserName.Trim(),
                EmptyToNull(UserForm.Email),
                EmptyToNull(UserForm.PhoneNumber),
                ParseNullableInt(UserForm.EmployeeIdInput),
                ParseNullableInt(UserForm.ArrendadorProveedorIdInput),
                UserForm.EmailConfirmed,
                UserForm.PhoneNumberConfirmed,
                UserForm.TwoFactorEnabled,
                UserForm.LockoutEnabled,
                ParseNullableDateTimeOffset(UserForm.LockoutEndInput, UserForm.LockoutEnabled),
                UserForm.IsNew ? EmptyToNull(UserForm.NewPassword) : null,
                UserForm.AssignedRoles.OrderBy(role => role, RoleNameComparer).ToArray(),
                UserForm.Claims
                    .Where(claim => !string.IsNullOrWhiteSpace(claim.ClaimType) && !string.IsNullOrWhiteSpace(claim.ClaimValue))
                    .Select(claim => new IdentityClaimInput(claim.ClaimType.Trim(), claim.ClaimValue.Trim()))
                    .ToArray());
        }

        private IdentityRoleUpsertRequest BuildRoleRequest()
        {
            return new IdentityRoleUpsertRequest(
                RoleForm.Id,
                CurrentUserId,
                RoleForm.Name.Trim(),
                RoleForm.AssignedUserIds.OrderBy(userId => userId, StringComparer.OrdinalIgnoreCase).ToArray(),
                RoleForm.Claims
                    .Where(claim => !string.IsNullOrWhiteSpace(claim.ClaimType) && !string.IsNullOrWhiteSpace(claim.ClaimValue))
                    .Select(claim => new IdentityClaimInput(claim.ClaimType.Trim(), claim.ClaimValue.Trim()))
                    .ToArray());
        }

        private void GenerateTemporaryPasswordForSelectedUser()
        {
            var temporaryPassword = GenerateTemporaryPassword();
            UserForm.PasswordResetInput = temporaryPassword;
            UserForm.PasswordResetConfirmInput = temporaryPassword;
        }

        private static UserEditorModel MapUserEditor(IdentityUserEditor editor)
        {
            return new UserEditorModel
            {
                Id = editor.Id,
                UserName = editor.UserName,
                Email = editor.Email ?? string.Empty,
                PhoneNumber = editor.PhoneNumber ?? string.Empty,
                EmployeeIdInput = editor.EmployeeId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ArrendadorProveedorIdInput = editor.ArrendadorProveedorId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                EmailConfirmed = editor.EmailConfirmed,
                PhoneNumberConfirmed = editor.PhoneNumberConfirmed,
                TwoFactorEnabled = editor.TwoFactorEnabled,
                LockoutEnabled = editor.LockoutEnabled,
                LockoutEndInput = editor.LockoutEnd?.LocalDateTime.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture) ?? string.Empty,
                AccessFailedCount = editor.AccessFailedCount,
                AssignedRoles = new HashSet<string>(editor.AssignedRoles, RoleNameComparer),
                Claims = editor.Claims
                    .Select(claim => new ClaimRowModel
                    {
                        ClaimType = claim.ClaimType,
                        ClaimValue = claim.ClaimValue
                    })
                    .ToList(),
                Logins = editor.Logins.ToList(),
                Tokens = editor.Tokens.ToList()
            };
        }

        private static RoleEditorModel MapRoleEditor(IdentityRoleEditor editor)
        {
            return new RoleEditorModel
            {
                Id = editor.Id,
                Name = editor.Name,
                AssignedUserIds = new HashSet<string>(editor.Users.Select(user => user.Id), StringComparer.OrdinalIgnoreCase),
                Claims = editor.Claims
                    .Select(claim => new ClaimRowModel
                    {
                        ClaimType = claim.ClaimType,
                        ClaimValue = claim.ClaimValue
                    })
                    .ToList(),
                Users = editor.Users.ToList()
            };
        }

        private static string GenerateTemporaryPassword()
        {
            var characters = new List<char>(12)
            {
                GetRandomCharacter(PasswordLowerAlphabet),
                GetRandomCharacter(PasswordUpperAlphabet),
                GetRandomCharacter(PasswordDigitAlphabet)
            };

            while (characters.Count < 12)
            {
                characters.Add(GetRandomCharacter(PasswordAlphabet));
            }

            for (var index = characters.Count - 1; index > 0; index--)
            {
                var swapIndex = RandomNumberGenerator.GetInt32(index + 1);
                (characters[index], characters[swapIndex]) = (characters[swapIndex], characters[index]);
            }

            return new string(characters.ToArray());
        }

        private static UserEditorModel CreateEmptyUserModel() => new();

        private static RoleEditorModel CreateEmptyRoleModel() => new();

        private static string BuildFailureMessage(IdentityAdminCommandResult result)
        {
            if (result.Errors is null || result.Errors.Count == 0)
            {
                return result.Message;
            }

            return $"{result.Message} {string.Join(" | ", result.Errors)}";
        }

        private static bool MatchesUserFilter(IdentityUserSummary user, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                return true;
            }

            var normalized = filter.Trim();
            return user.UserName.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                   || (user.Email?.Contains(normalized, StringComparison.OrdinalIgnoreCase) ?? false)
                   || (user.EmployeeId?.ToString(CultureInfo.InvariantCulture).Contains(normalized, StringComparison.OrdinalIgnoreCase) ?? false)
                   || (user.ArrendadorProveedorId?.ToString(CultureInfo.InvariantCulture).Contains(normalized, StringComparison.OrdinalIgnoreCase) ?? false)
                   || user.Roles.Any(role => role.Contains(normalized, StringComparison.OrdinalIgnoreCase));
        }

        private static bool MatchesRoleFilter(IdentityRoleSummary role, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                return true;
            }

            return role.Name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private string FormatArrendadorProveedor(int? proveedorId)
        {
            if (!proveedorId.HasValue)
            {
                return "Sin arrendador ligado";
            }

            var option = ArrendadorOptions.FirstOrDefault(arrendador => arrendador.Id == proveedorId.Value);
            return option is null
                ? $"Arrendador ID {proveedorId.Value.ToString(CultureInfo.InvariantCulture)}"
                : $"{option.RazonSocial} · ID {option.Id.ToString(CultureInfo.InvariantCulture)}";
        }

        private static bool GetCheckboxValue(ChangeEventArgs args)
        {
            return args.Value switch
            {
                bool value => value,
                string value when bool.TryParse(value, out var parsed) => parsed,
                _ => false
            };
        }

        private static int? ParseNullableInt(string? value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue)
                ? parsedValue
                : null;
        }

        private static DateTimeOffset? ParseNullableDateTimeOffset(string? value, bool isEnabled)
        {
            if (!isEnabled || string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsedValue)
                ? new DateTimeOffset(parsedValue)
                : null;
        }

        private static string? EmptyToNull(string? value)
        {
            var trimmedValue = value?.Trim();
            return string.IsNullOrWhiteSpace(trimmedValue) ? null : trimmedValue;
        }

        private static char GetRandomCharacter(string alphabet)
            => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];

        private sealed class UserEditorModel
        {
            public string? Id { get; set; }
            public string UserName { get; set; } = string.Empty;
            public string Email { get; set; } = string.Empty;
            public string PhoneNumber { get; set; } = string.Empty;
            public string EmployeeIdInput { get; set; } = string.Empty;
            public string ArrendadorProveedorIdInput { get; set; } = string.Empty;
            public bool EmailConfirmed { get; set; }
            public bool PhoneNumberConfirmed { get; set; }
            public bool TwoFactorEnabled { get; set; }
            public bool LockoutEnabled { get; set; } = true;
            public string LockoutEndInput { get; set; } = string.Empty;
            public string NewPassword { get; set; } = string.Empty;
            public string PasswordResetInput { get; set; } = string.Empty;
            public string PasswordResetConfirmInput { get; set; } = string.Empty;
            public int AccessFailedCount { get; set; }
            public HashSet<string> AssignedRoles { get; set; } = new(RoleNameComparer);
            public List<ClaimRowModel> Claims { get; set; } = new();
            public List<IdentityLoginRecord> Logins { get; set; } = new();
            public List<IdentityTokenRecord> Tokens { get; set; } = new();
            public bool IsNew => string.IsNullOrWhiteSpace(Id);
            public string DisplayName => string.IsNullOrWhiteSpace(UserName) ? "Nuevo usuario" : UserName;
        }

        private sealed class RoleEditorModel
        {
            public string? Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public HashSet<string> AssignedUserIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public List<ClaimRowModel> Claims { get; set; } = new();
            public List<IdentityUserReference> Users { get; set; } = new();
            public bool IsNew => string.IsNullOrWhiteSpace(Id);
            public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Nuevo rol" : Name;
        }

        private sealed class ClaimRowModel
        {
            public Guid Key { get; } = Guid.NewGuid();
            public string ClaimType { get; set; } = string.Empty;
            public string ClaimValue { get; set; } = string.Empty;
        }

        private enum IdentityAdminTab
        {
            Users,
            Roles
        }
    }
}
