using OrionERP.Application.Common;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using OrionERP.Application.Features.Logistica.BusinessPartners;
using OrionERP.Web.Services;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Web.State;

namespace OrionERP.Web.Features.Logistica.Vendors;

public partial class ProveedoresLogisticaPage : ComponentBase, IDisposable
{
  [Inject] private IBusinessPartnerService BusinessPartnerService { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;
  [Inject] private ICurrentCompanyContext RfcState { get; set; } = default!;
  [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;

  protected BusinessPartnerFilter Filter { get; set; } = new() { VendorOnly = true };
  protected BusinessPartnerCatalogDto Catalog { get; set; } = new();
  protected List<BusinessPartnerListItemDto> Partners { get; set; } = [];
  protected BusinessPartnerUpsertRequest Editor { get; set; } = CreateNewEditor(string.Empty);
  protected VendorProfileUpsertRequest VendorProfile { get; set; } = CreateVendorProfile();
  protected HashSet<string> SelectedRoles { get; set; } = new(StringComparer.OrdinalIgnoreCase) { "Vendor" };
  protected int? SelectedPartnerId { get; set; }
  protected bool IsBusy { get; set; }
  protected bool IsSaving { get; set; }
  protected bool ShowLifecycleDialog { get; set; }
  protected bool IsLoadingLifecycleAssessment { get; set; }
  protected bool IsProcessingLifecycle { get; set; }
  protected bool IsAdministrator { get; set; }
  protected int? LifecycleTargetPartnerId { get; set; }
  protected VendorLifecycleAssessmentDto? LifecycleAssessment { get; set; }
  protected string? LifecycleAssessmentError { get; set; }
  protected string DeletionConfirmationText { get; set; } = string.Empty;
  protected string CurrentUserName { get; set; } = "OrionERP";

  protected bool DeletionConfirmationMatches
    => string.Equals(DeletionConfirmationText, "Delete", StringComparison.Ordinal);

  private CancellationTokenSource? _lifecycleAssessmentCts;

  protected override async Task OnInitializedAsync()
  {
    Editor = CreateNewEditor(CurrentRfc);
    var authenticationState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
    IsAdministrator = authenticationState.User.IsInRole("Administrador");
    CurrentUserName = authenticationState.User.Identity?.Name ?? CurrentUserName;
    Catalog = await BusinessPartnerService.GetCatalogAsync(CurrentRfc);
    await BuscarAsync();
  }

  protected async Task BuscarAsync()
  {
    IsBusy = true;
    try
    {
      Filter.OwnerRfc = CurrentRfc;
      Partners = (await BusinessPartnerService.GetPartnersAsync(Filter)).ToList();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cargar el maestro de socios. {ex.Message}");
    }
    finally
    {
      IsBusy = false;
      StateHasChanged();
    }
  }

  protected Task OnSearchKeyUpAsync(KeyboardEventArgs args)
    => args.Key == "Enter" ? BuscarAsync() : Task.CompletedTask;

  protected void NuevoRegistro()
  {
    ResetLifecycleReport();
    SelectedPartnerId = null;
    Editor = CreateNewEditor(CurrentRfc);
    VendorProfile = CreateVendorProfile();
    SelectedRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Vendor" };
  }

  protected async Task SeleccionarAsync(int businessPartnerId)
  {
    ResetLifecycleReport();
    try
    {
      var detail = await BusinessPartnerService.GetPartnerAsync(CurrentRfc, businessPartnerId);
      if (detail is null)
      {
        UiMessages.ShowWarning("El socio seleccionado ya no existe.");
        return;
      }

      SelectedPartnerId = detail.Id;
      Editor = new BusinessPartnerUpsertRequest
      {
        OwnerRfc = CurrentRfc,
        Id = detail.Id,
        LegacyProveedorId = detail.LegacyProveedorId,
        DisplayName = detail.DisplayName,
        Rfc = detail.Rfc,
        Email = detail.Email,
        Phone = detail.Phone,
        Street = detail.Street,
        Neighborhood = detail.Neighborhood,
        City = detail.City,
        State = detail.State,
        PostalCode = detail.PostalCode,
        BusinessLine = detail.BusinessLine,
        Notes = detail.Notes,
        IsActive = detail.IsActive
      };

      VendorProfile = new VendorProfileUpsertRequest
      {
        PaymentTerms = detail.VendorProfile?.PaymentTerms,
        DefaultLeadTimeDays = detail.VendorProfile?.DefaultLeadTimeDays,
        IsApproved = detail.VendorProfile?.IsApproved ?? true,
        Notes = detail.VendorProfile?.Notes
      };

      SelectedRoles = new HashSet<string>(detail.Roles, StringComparer.OrdinalIgnoreCase);
      if (SelectedRoles.Count == 0)
      {
        SelectedRoles.Add("Vendor");
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cargar el socio seleccionado. {ex.Message}");
    }
  }

  protected void ToggleRole(string roleCode, bool isChecked)
  {
    if (isChecked)
    {
      SelectedRoles.Add(roleCode);
      return;
    }

    SelectedRoles.Remove(roleCode);
  }

  protected async Task GuardarAsync()
  {
    IsSaving = true;
    try
    {
      Editor.Roles = SelectedRoles.ToArray();
      Editor.OwnerRfc = CurrentRfc;
      Editor.VendorProfile = VendorProfile;

      var result = await BusinessPartnerService.SavePartnerAsync(Editor);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      await BuscarAsync();

      if (result.EntityId.HasValue)
      {
        await SeleccionarAsync(result.EntityId.Value);
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo guardar el socio. {ex.Message}");
    }
    finally
    {
      IsSaving = false;
    }
  }

  protected async Task AbrirReporteRetiroAsync()
  {
    if (!Editor.Id.HasValue || Editor.Id.Value <= 0 || IsSaving || IsProcessingLifecycle)
    {
      return;
    }

    ResetLifecycleReport();
    LifecycleTargetPartnerId = Editor.Id.Value;
    ShowLifecycleDialog = true;
    await RefreshLifecycleAssessmentAsync();
  }

  protected async Task RefreshLifecycleAssessmentAsync()
  {
    if (!ShowLifecycleDialog || !LifecycleTargetPartnerId.HasValue)
    {
      return;
    }

    _lifecycleAssessmentCts?.Cancel();
    _lifecycleAssessmentCts?.Dispose();
    _lifecycleAssessmentCts = new CancellationTokenSource();
    var token = _lifecycleAssessmentCts.Token;
    var businessPartnerId = LifecycleTargetPartnerId.Value;

    IsLoadingLifecycleAssessment = true;
    LifecycleAssessmentError = null;
    LifecycleAssessment = null;
    DeletionConfirmationText = string.Empty;

    try
    {
      var assessment = await BusinessPartnerService.GetVendorLifecycleAssessmentAsync(CurrentRfc, businessPartnerId, token);
      token.ThrowIfCancellationRequested();
      if (!ShowLifecycleDialog || LifecycleTargetPartnerId != businessPartnerId)
      {
        return;
      }

      LifecycleAssessment = assessment;
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested)
    {
      // El diálogo se cerró, cambió la empresa o una revisión más reciente reemplazó a ésta.
    }
    catch (Exception ex)
    {
      LifecycleAssessmentError = $"No se pudo revisar el ciclo de vida del socio. {ex.Message}";
    }
    finally
    {
      if (_lifecycleAssessmentCts?.Token == token)
      {
        IsLoadingLifecycleAssessment = false;
        StateHasChanged();
      }
    }
  }

  protected void CerrarReporteRetiro()
  {
    if (!IsProcessingLifecycle)
    {
      ResetLifecycleReport();
    }
  }

  protected async Task EliminarSocioAsync()
  {
    if (IsProcessingLifecycle
        || LifecycleAssessment is not { CanDelete: true, Exists: true }
        || !LifecycleTargetPartnerId.HasValue
        || LifecycleAssessment.BusinessPartnerId != LifecycleTargetPartnerId.Value
        || !DeletionConfirmationMatches)
    {
      return;
    }

    var authenticationState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
    IsAdministrator = authenticationState.User.IsInRole("Administrador");
    CurrentUserName = authenticationState.User.Identity?.Name ?? CurrentUserName;
    if (!IsAdministrator)
    {
      UiMessages.ShowError("Solo un administrador puede eliminar socios de negocio permanentemente.");
      return;
    }

    IsProcessingLifecycle = true;
    try
    {
      var result = await BusinessPartnerService.DeleteVendorAsync(new VendorDeleteRequest
      {
        OwnerRfc = CurrentRfc,
        BusinessPartnerId = LifecycleTargetPartnerId.Value,
        ConfirmationText = DeletionConfirmationText,
        DeletedBy = CurrentUserName
      });

      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        IsProcessingLifecycle = false;
        await RefreshLifecycleAssessmentAsync();
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      ResetLifecycleReport();
      NuevoRegistro();
      await BuscarAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo eliminar el socio. {ex.Message}");
    }
    finally
    {
      IsProcessingLifecycle = false;
    }
  }

  protected IEnumerable<VendorDependencySection> GetDependencySections(VendorLifecycleAssessmentDto assessment)
  {
    if (assessment.OperationalBlockers.Count > 0)
    {
      yield return new VendorDependencySection(
        "danger",
        "Vínculos operativos por resolver",
        "Estas relaciones deben cerrarse, retirarse o reasignarse antes de continuar.",
        "bi-exclamation-diamond-fill",
        assessment.OperationalBlockers);
    }
    if (assessment.HistoricalReferences.Count > 0)
    {
      yield return new VendorDependencySection(
        "secondary",
        "Historial que debe conservarse",
        "Estas referencias son evidencia operativa y nunca se eliminan al retirar al socio.",
        "bi-clock-history",
        assessment.HistoricalReferences);
    }
    if (assessment.ConfigurationReferences.Count > 0)
    {
      yield return new VendorDependencySection(
        "warning",
        "Configuración desvinculable",
        "Esta configuración inactiva todavía apunta al socio y debe liberarse.",
        "bi-sliders",
        assessment.ConfigurationReferences);
    }
  }

  public void Dispose()
  {
    _lifecycleAssessmentCts?.Cancel();
    _lifecycleAssessmentCts?.Dispose();
    _lifecycleAssessmentCts = null;
  }

  private void ResetLifecycleReport()
  {
    _lifecycleAssessmentCts?.Cancel();
    _lifecycleAssessmentCts?.Dispose();
    _lifecycleAssessmentCts = null;
    ShowLifecycleDialog = false;
    IsLoadingLifecycleAssessment = false;
    LifecycleTargetPartnerId = null;
    LifecycleAssessment = null;
    LifecycleAssessmentError = null;
    DeletionConfirmationText = string.Empty;
  }

  protected sealed record VendorDependencySection(
    string ToneClass,
    string Title,
    string Description,
    string IconClass,
    IReadOnlyList<VendorDependencyDto> Dependencies);

  private static BusinessPartnerUpsertRequest CreateNewEditor(string ownerRfc)
    => new()
    {
      OwnerRfc = ownerRfc,
      IsActive = true,
      DisplayName = string.Empty
    };

  private static VendorProfileUpsertRequest CreateVendorProfile()
    => new()
    {
      IsApproved = true
    };

  private string CurrentRfc => RfcState.RequireRfc();
}
