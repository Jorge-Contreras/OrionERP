using Microsoft.AspNetCore.Components;
using OrionERP.Application.Features.Platform;
using OrionERP.Web.Services;

namespace OrionERP.Web.Features.Platform;

public partial class PlatformAdministrationPage : ComponentBase
{
  [Inject] private IPlatformAdministrationReader Reader { get; set; } = default!;
  [Inject] private IPlatformAdministrationService Administration { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;
  [Inject] private TimeProvider Clock { get; set; } = default!;

  private PlatformAdministrationSnapshot? Snapshot { get; set; }
  private PlatformAdminTab ActiveTab { get; set; } = PlatformAdminTab.Company;
  private CompanyEditor CompanyForm { get; set; } = new();
  private SiteEditor SiteForm { get; set; } = SiteEditor.New();
  private ModuleEditor ModuleForm { get; set; } = new();
  private CapabilityEditor CapabilityForm { get; set; } = new();
  private PublicSiteEditor PublicSiteForm { get; set; } = new();
  private DeleteRequest? PendingDelete { get; set; }
  private string? LoadError { get; set; }
  private bool IsLoading { get; set; }
  private bool IsSaving { get; set; }
  private bool IsBusy => IsLoading || IsSaving;

  private IEnumerable<PlatformModuleStatus> AssignableModules
    => (Snapshot?.Modules ?? [])
      .Where(module => module.Assignment is not null
        && PlatformModuleCodes.PublicWebsiteModules.Contains(module.Module.ModuleCode));

  protected override async Task OnInitializedAsync()
    => await LoadSnapshotAsync(resetEditors: true);

  private async Task RefreshAsync()
    => await LoadSnapshotAsync(resetEditors: true);

  private async Task LoadSnapshotAsync(bool resetEditors)
  {
    IsLoading = true;
    LoadError = null;
    try
    {
      Snapshot = await Reader.GetCurrentCompanySnapshotAsync();
      CompanyForm = CompanyEditor.From(Snapshot.Company);
      if (resetEditors)
      {
        SiteForm = SiteEditor.New();
        ModuleForm = new();
        CapabilityForm = new();
        PublicSiteForm = new();
        PendingDelete = null;
      }
    }
    catch (Exception exception)
    {
      Snapshot = null;
      LoadError = exception.Message;
    }
    finally
    {
      IsLoading = false;
    }
  }

  private void SelectTab(PlatformAdminTab tab)
  {
    ActiveTab = tab;
    PendingDelete = null;
  }

  private string TabClass(PlatformAdminTab tab)
    => ActiveTab == tab ? "platform-tab is-active" : "platform-tab";

  private async Task SaveCompanyAsync()
  {
    if (await RunMutationAsync(
      () => Administration.UpdateCompanyMetadataAsync(new(
        CompanyForm.TaxRfc,
        CompanyForm.LegacyTenantKey,
        CompanyForm.ConcurrencyToken)),
      "La identidad de plataforma quedó actualizada."))
      CompanyForm = CompanyEditor.From(Snapshot!.Company);
  }

  private void NewSite()
  {
    SiteForm = SiteEditor.New();
    PendingDelete = null;
  }

  private void EditSite(PlatformSite site)
  {
    SiteForm = SiteEditor.From(site);
    PendingDelete = null;
  }

  private async Task SaveSiteAsync()
  {
    long siteId;
    if (SiteForm.SiteId.HasValue)
    {
      siteId = SiteForm.SiteId.Value;
      if (!await RunMutationAsync(
        () => Administration.UpdateSiteAsync(new(
          siteId,
          SiteForm.DisplayName,
          SiteForm.TimeZoneId,
          SiteForm.IsActive,
          SiteForm.ConcurrencyToken)),
        "La sede quedó actualizada."))
        return;
    }
    else
    {
      siteId = 0;
      if (!await RunMutationAsync(async () =>
        {
          siteId = await Administration.CreateSiteAsync(new(
            SiteForm.SiteKey,
            SiteForm.DisplayName,
            SiteForm.TimeZoneId,
            SiteForm.IsActive));
        }, "La sede quedó creada."))
        return;
    }

    var current = Snapshot!.Sites.SingleOrDefault(site => site.SiteId == siteId);
    if (current is not null)
      EditSite(current);
  }

  private void RequestSiteDelete()
  {
    if (SiteForm.SiteId.HasValue)
      PendingDelete = new(DeleteKind.Site, SiteForm.SiteId.Value, null, SiteForm.ConcurrencyToken, $"la sede “{SiteForm.DisplayName}”");
  }

  private void EditModule(PlatformModuleStatus item)
  {
    ModuleForm = ModuleEditor.From(item);
    PendingDelete = null;
  }

  private async Task SaveModuleAsync()
  {
    var moduleCode = ModuleForm.ModuleCode;
    if (!await RunMutationAsync(
      () => Administration.SetCompanyModuleAsync(new(
        moduleCode,
        ModuleForm.Status,
        ModuleForm.IsCore ? null : ModuleForm.EffectiveFromUtc,
        ModuleForm.IsCore ? null : ModuleForm.EffectiveToUtc,
        ModuleForm.Exists ? ModuleForm.ConcurrencyToken : null)),
      "La asignación del módulo quedó guardada."))
      return;

    var current = Snapshot!.Modules.SingleOrDefault(
      module => module.Module.ModuleCode == moduleCode);
    if (current is not null)
      EditModule(current);
  }

  private void RequestModuleDelete()
  {
    if (ModuleForm.Exists)
      PendingDelete = new(DeleteKind.Module, 0, ModuleForm.ModuleCode, ModuleForm.ConcurrencyToken, $"el módulo “{ModuleName(ModuleForm.ModuleCode)}”");
  }

  private void NewCapability()
  {
    CapabilityForm = new();
    PendingDelete = null;
  }

  private void EditCapability(PlatformSiteCapability capability)
  {
    CapabilityForm = CapabilityEditor.From(capability);
    PendingDelete = null;
  }

  private bool IsSelected(PlatformSiteCapability capability)
    => CapabilityForm.Exists
      && CapabilityForm.SiteId == capability.SiteId
      && CapabilityForm.ModuleCode == capability.ModuleCode;

  private async Task SaveCapabilityAsync()
  {
    var siteId = CapabilityForm.SiteId;
    var moduleCode = CapabilityForm.ModuleCode;
    if (!await RunMutationAsync(
      () => Administration.SetSiteCapabilityAsync(new(
        siteId,
        moduleCode,
        CapabilityForm.IsEnabled,
        CapabilityForm.Exists ? CapabilityForm.ConcurrencyToken : null)),
      "La capacidad de la sede quedó guardada."))
      return;

    var current = Snapshot!.SiteCapabilities.SingleOrDefault(
      capability => capability.SiteId == siteId && capability.ModuleCode == moduleCode);
    if (current is not null)
      EditCapability(current);
  }

  private void RequestCapabilityDelete()
  {
    if (CapabilityForm.Exists)
      PendingDelete = new(
        DeleteKind.Capability,
        CapabilityForm.SiteId,
        CapabilityForm.ModuleCode,
        CapabilityForm.ConcurrencyToken,
        $"la capacidad “{ModuleName(CapabilityForm.ModuleCode)}” de {SiteName(CapabilityForm.SiteId)}");
  }

  private void NewPublicSite()
  {
    PublicSiteForm = new();
    PendingDelete = null;
  }

  private void EditPublicSite(PlatformPublicSite publicSite)
  {
    PublicSiteForm = PublicSiteEditor.From(publicSite);
    PendingDelete = null;
  }

  private async Task SavePublicSiteAsync()
  {
    long publicSiteId;
    if (PublicSiteForm.PublicSiteId.HasValue)
    {
      publicSiteId = PublicSiteForm.PublicSiteId.Value;
      if (!await RunMutationAsync(
        () => Administration.UpdatePublicSiteAsync(new(
          publicSiteId,
          PublicSiteForm.CanonicalHost,
          PublicSiteForm.IsActive,
          PublicSiteForm.BrandingVersion,
          PublicSiteForm.ContentVersion,
          PublicSiteForm.ConcurrencyToken)),
        "El website quedó actualizado."))
        return;
    }
    else
    {
      publicSiteId = 0;
      if (!await RunMutationAsync(async () =>
        {
          publicSiteId = await Administration.CreatePublicSiteAsync(new(
            PublicSiteForm.PublicSiteKey,
            PublicSiteForm.SiteId,
            PublicSiteForm.ModuleCode,
            PublicSiteForm.CanonicalHost,
            PublicSiteForm.IsActive));
        }, "El website quedó creado."))
        return;
    }

    var current = Snapshot!.PublicSites.SingleOrDefault(site => site.PublicSiteId == publicSiteId);
    if (current is not null)
      EditPublicSite(current);
  }

  private async Task RollbackPublicSitePresentationAsync()
  {
    if (!PublicSiteForm.PublicSiteId.HasValue)
      return;
    var publicSiteId = PublicSiteForm.PublicSiteId.Value;
    if (!await RunMutationAsync(
      () => Administration.RollbackPublicSitePresentationAsync(
        publicSiteId,
        PublicSiteForm.ConcurrencyToken),
      "Se restauró la pareja anterior de marca y contenido."))
      return;

    var current = Snapshot!.PublicSites.SingleOrDefault(site => site.PublicSiteId == publicSiteId);
    if (current is not null)
      EditPublicSite(current);
  }

  private async Task FinalizePublicSitePresentationAsync()
  {
    if (!PublicSiteForm.PublicSiteId.HasValue)
      return;
    var publicSiteId = PublicSiteForm.PublicSiteId.Value;
    if (!await RunMutationAsync(
      () => Administration.FinalizePublicSitePresentationAsync(
        publicSiteId,
        PublicSiteForm.ConcurrencyToken),
      "La activación quedó finalizada y se cerró la reversión."))
      return;

    var current = Snapshot!.PublicSites.SingleOrDefault(site => site.PublicSiteId == publicSiteId);
    if (current is not null)
      EditPublicSite(current);
  }

  private void RequestPublicSiteDelete()
  {
    if (PublicSiteForm.PublicSiteId.HasValue)
      PendingDelete = new(
        DeleteKind.PublicSite,
        PublicSiteForm.PublicSiteId.Value,
        null,
        PublicSiteForm.ConcurrencyToken,
        $"el website “{PublicSiteForm.CanonicalHost}”");
  }

  private async Task ConfirmDeleteAsync()
  {
    var request = PendingDelete;
    if (request is null)
      return;

    var succeeded = await RunMutationAsync(
      request.Kind switch
      {
        DeleteKind.Site => () => Administration.DeleteSiteAsync(request.Id, request.ConcurrencyToken),
        DeleteKind.Module => () => Administration.DeleteCompanyModuleAsync(request.ModuleCode!, request.ConcurrencyToken),
        DeleteKind.Capability => () => Administration.DeleteSiteCapabilityAsync(request.Id, request.ModuleCode!, request.ConcurrencyToken),
        DeleteKind.PublicSite => () => Administration.DeletePublicSiteAsync(request.Id, request.ConcurrencyToken),
        _ => throw new InvalidOperationException("Tipo de eliminación no soportado.")
      },
      "La configuración quedó eliminada.");

    if (!succeeded)
      return;
    PendingDelete = null;
    switch (request.Kind)
    {
      case DeleteKind.Site:
        NewSite();
        break;
      case DeleteKind.Module:
        ModuleForm = new();
        break;
      case DeleteKind.Capability:
        NewCapability();
        break;
      case DeleteKind.PublicSite:
        NewPublicSite();
        break;
    }
  }

  private void CancelDelete()
    => PendingDelete = null;

  private async Task<bool> RunMutationAsync(Func<Task> action, string successMessage)
  {
    if (IsBusy)
      return false;
    IsSaving = true;
    try
    {
      await action();
      Snapshot = await Reader.GetCurrentCompanySnapshotAsync();
      CompanyForm = CompanyEditor.From(Snapshot.Company);
      UiMessages.ShowSuccess(successMessage, "Plataforma");
      return true;
    }
    catch (PlatformAdministrationConcurrencyException exception)
    {
      UiMessages.ShowWarning(exception.Message, "Configuración desactualizada");
      await LoadSnapshotAsync(resetEditors: true);
      return false;
    }
    catch (PlatformAdministrationValidationException exception)
    {
      UiMessages.ShowWarning(exception.Message, "Revisa la configuración");
      return false;
    }
    catch (UnauthorizedAccessException exception)
    {
      UiMessages.ShowError(exception.Message, "Acceso no autorizado");
      return false;
    }
    catch (Exception exception)
    {
      UiMessages.ShowError(exception.Message, "No se pudo guardar");
      return false;
    }
    finally
    {
      IsSaving = false;
    }
  }

  private string SiteName(long siteId)
    => Snapshot?.Sites.SingleOrDefault(site => site.SiteId == siteId)?.DisplayName ?? "Sede no disponible";

  private string ModuleName(string moduleCode)
    => Snapshot?.Modules.SingleOrDefault(module => module.Module.ModuleCode == moduleCode)?.Module.DisplayName
      ?? moduleCode;

  private static string FormatUtc(DateTime value)
    => $"{DateTime.SpecifyKind(value, DateTimeKind.Utc):dd/MM/yyyy HH:mm} UTC";

  private bool IsPresentationRollbackAvailable(DateTime? fallbackUntilUtc)
    => fallbackUntilUtc.HasValue
      && DateTime.SpecifyKind(fallbackUntilUtc.Value, DateTimeKind.Utc)
        > Clock.GetUtcNow().UtcDateTime;

  private static string ModuleStatusText(PlatformModuleStatus item)
    => item.Module.IsCore
      ? "Incluido"
      : item.Assignment?.Status switch
      {
        PlatformCompanyModuleStatuses.Enabled when item.Assignment.IsEffective => "Habilitado",
        PlatformCompanyModuleStatuses.Enabled => "Programado",
        PlatformCompanyModuleStatuses.Provisioning => "En preparación",
        PlatformCompanyModuleStatuses.Suspended => "Suspendido",
        _ => "Sin asignar"
      };

  private enum PlatformAdminTab
  {
    Company,
    Sites,
    Modules,
    Capabilities,
    PublicSites
  }

  private enum DeleteKind
  {
    Site,
    Module,
    Capability,
    PublicSite
  }

  private sealed record DeleteRequest(
    DeleteKind Kind,
    long Id,
    string? ModuleCode,
    string ConcurrencyToken,
    string Label);

  private sealed class CompanyEditor
  {
    public string TaxRfc { get; set; } = string.Empty;
    public string LegacyTenantKey { get; set; } = string.Empty;
    public string ConcurrencyToken { get; set; } = string.Empty;

    public static CompanyEditor From(PlatformCompany company)
      => new()
      {
        TaxRfc = company.TaxRfc ?? string.Empty,
        LegacyTenantKey = company.LegacyTenantKey ?? string.Empty,
        ConcurrencyToken = company.ConcurrencyToken
      };
  }

  private sealed class SiteEditor
  {
    public long? SiteId { get; set; }
    public string SiteKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string ConcurrencyToken { get; set; } = string.Empty;

    public static SiteEditor New()
      => new() { IsActive = true, TimeZoneId = "Central Standard Time (Mexico)" };

    public static SiteEditor From(PlatformSite site)
      => new()
      {
        SiteId = site.SiteId,
        SiteKey = site.SiteKey,
        DisplayName = site.DisplayName,
        TimeZoneId = site.TimeZoneId,
        IsActive = site.IsActive,
        ConcurrencyToken = site.ConcurrencyToken
      };
  }

  private sealed class ModuleEditor
  {
    public string ModuleCode { get; set; } = string.Empty;
    public string Status { get; set; } = PlatformCompanyModuleStatuses.Provisioning;
    public DateTime? EffectiveFromUtc { get; set; }
    public DateTime? EffectiveToUtc { get; set; }
    public string ConcurrencyToken { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public bool IsCore { get; set; }

    public static ModuleEditor From(PlatformModuleStatus item)
      => new()
      {
        ModuleCode = item.Module.ModuleCode,
        Status = item.Module.IsCore
          ? PlatformCompanyModuleStatuses.Enabled
          : item.Assignment?.Status ?? PlatformCompanyModuleStatuses.Provisioning,
        EffectiveFromUtc = item.Assignment?.EffectiveFromUtc,
        EffectiveToUtc = item.Assignment?.EffectiveToUtc,
        ConcurrencyToken = item.Assignment?.ConcurrencyToken ?? string.Empty,
        Exists = item.Assignment is not null,
        IsCore = item.Module.IsCore
      };
  }

  private sealed class CapabilityEditor
  {
    public long SiteId { get; set; }
    public string ModuleCode { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string ConcurrencyToken { get; set; } = string.Empty;
    public bool Exists { get; set; }

    public static CapabilityEditor From(PlatformSiteCapability capability)
      => new()
      {
        SiteId = capability.SiteId,
        ModuleCode = capability.ModuleCode,
        IsEnabled = capability.IsEnabled,
        ConcurrencyToken = capability.ConcurrencyToken,
        Exists = true
      };
  }

  private sealed class PublicSiteEditor
  {
    public long? PublicSiteId { get; set; }
    public string PublicSiteKey { get; set; } = string.Empty;
    public long SiteId { get; set; }
    public string ModuleCode { get; set; } = string.Empty;
    public string CanonicalHost { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public long BrandingVersion { get; set; } = 1;
    public long ContentVersion { get; set; } = 1;
    public long? FallbackBrandingVersion { get; set; }
    public long? FallbackContentVersion { get; set; }
    public DateTime? FallbackUntilUtc { get; set; }
    public string ConcurrencyToken { get; set; } = string.Empty;

    public static PublicSiteEditor From(PlatformPublicSite publicSite)
      => new()
      {
        PublicSiteId = publicSite.PublicSiteId,
        PublicSiteKey = publicSite.PublicSiteKey,
        SiteId = publicSite.SiteId,
        ModuleCode = publicSite.ModuleCode,
        CanonicalHost = publicSite.CanonicalHost,
        IsActive = publicSite.IsActive,
        BrandingVersion = publicSite.BrandingVersion,
        ContentVersion = publicSite.ContentVersion,
        FallbackBrandingVersion = publicSite.FallbackBrandingVersion,
        FallbackContentVersion = publicSite.FallbackContentVersion,
        FallbackUntilUtc = publicSite.FallbackUntilUtc,
        ConcurrencyToken = publicSite.ConcurrencyToken
      };
  }
}
