using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using OrionERP.Application.Features.Logistica.BusinessPartners;
using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Web.Services;
using OrionERP.Web.State;

namespace OrionERP.Web.Features.Logistica.Materials;

public partial class MaterialesPage : ComponentBase, IDisposable
{
  private const int ThumbnailMaxPixels = 320;
  private const int PageSize = 50;
  private const int QueryTake = PageSize + 1;
  private const int MovementPageSize = 25;
  private const int MovementQueryTake = MovementPageSize + 1;
  private const int SearchDebounceMilliseconds = 320;
  private const long MaxImageBytes = 8 * 1024 * 1024;

  [Inject] private IMaterialService MaterialService { get; set; } = default!;
  [Inject] private IBusinessPartnerService BusinessPartnerService { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;
  [Inject] private IUserRfcState RfcState { get; set; } = default!;
  [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;

  protected MaterialFilter Filter { get; set; } = new();
  protected MaterialCatalogDto Catalog { get; set; } = new();
  protected List<MaterialListItemDto> Materials { get; set; } = [];
  protected MaterialUpsertRequest Editor { get; set; } = CreateNewEditor();
  protected MaterialCategoryCreateRequest CategoryDraft { get; set; } = new();
  protected UnitOfMeasureCreateRequest UnitDraft { get; set; } = new();
  protected BusinessPartnerUpsertRequest VendorDraft { get; set; } = CreateVendorDraft(string.Empty);
  protected Dictionary<int, string> MaterialThumbnailDataUrls { get; set; } = [];
  protected int? SelectedMaterialId { get; set; }
  protected string? CurrentMaterialCode { get; set; }
  protected string? ImagePreviewDataUrl { get; set; }
  protected string? SelectedImageFileName { get; set; }
  protected bool HasExecutedSearch { get; set; }
  protected bool HasMoreMaterials { get; set; }
  protected bool IsBusy { get; set; }
  protected bool IsLoadingMore { get; set; }
  protected bool IsSaving { get; set; }
  protected bool IsLoadingEditor { get; set; }
  protected bool IsSavingMasterData { get; set; }
  protected bool HasPersistedImage { get; set; }
  protected bool ShowAdvancedFilters { get; set; }
  protected bool ShowMoreFields { get; set; }
  protected bool ShowCategoryDialog { get; set; }
  protected bool ShowUnitDialog { get; set; }
  protected bool ShowVendorDialog { get; set; }
  protected UnitSelectionTarget PendingUnitTarget { get; set; }
  protected bool ShowMaterialImageModal { get; set; }
  protected bool IsLoadingMaterialImage { get; set; }
  protected string? MaterialImageModalTitle { get; set; }
  protected string? MaterialImageModalDataUrl { get; set; }
  protected bool ShowLifecycleDialog { get; set; }
  protected bool IsLoadingLifecycleAssessment { get; set; }
  protected bool IsProcessingLifecycle { get; set; }
  protected bool IsAdministrator { get; set; }
  protected bool ShowInactiveMaterials { get; set; }
  protected bool SelectedMaterialIsActive { get; set; } = true;
  protected int? LifecycleTargetMaterialId { get; set; }
  protected MaterialLifecycleAssessmentDto? LifecycleAssessment { get; set; }
  protected string? LifecycleAssessmentError { get; set; }
  protected string DeletionConfirmationText { get; set; } = string.Empty;
  protected string CurrentUserName { get; set; } = "OrionERP";
  protected bool IsListBusy => IsBusy || IsLoadingMore;
  protected bool DeletionConfirmationMatches
    => string.Equals(DeletionConfirmationText, "Delete", StringComparison.Ordinal);

  private CancellationTokenSource? _searchDebounceCts;
  private CancellationTokenSource? _listRequestCts;
  private CancellationTokenSource? _rfcReloadCts;
  private CancellationTokenSource? _lifecycleAssessmentCts;
  private CancellationTokenSource? _inventoryRequestCts;
  private CancellationTokenSource? _movementRequestCts;
  private string? _catalogRfc;
  private bool _catalogRetryPending;
  private string? _catalogRecoveryRfc;
  private decimal? _purchasePresentationPrice;
  private string _purchaseQuantityInputText = "1";
  private string _baseUnitPriceInputText = string.Empty;
  private string _purchasePresentationPriceInputText = string.Empty;

  protected int LoadedWithStockCount => Materials.Count(material => material.TotalQuantity > 0);
  protected int LoadedMissingVendorCount => Materials.Count(material => string.IsNullOrWhiteSpace(material.VendorName));
  protected int LoadedMissingImageCount => Materials.Count(material => !material.HasImage);

  protected int ActiveFilterCount
  {
    get
    {
      var count = 0;
      if (!string.IsNullOrWhiteSpace(Filter.SearchText)) count++;
      if (Filter.CategoryId.HasValue) count++;
      if (Filter.VendorId.HasValue) count++;
      if (!string.IsNullOrWhiteSpace(Filter.MaterialClass)) count++;
      if (!string.IsNullOrWhiteSpace(Filter.Status)) count++;
      if (Filter.HasImage.HasValue) count++;
      if (Filter.HasStock.HasValue) count++;
      if (Filter.NeedsAttention) count++;
      return count;
    }
  }

  protected int MaterialReadinessPercent
  {
    get
    {
      var score = 0;
      if (!string.IsNullOrWhiteSpace(Editor.Description)) score += 20;
      if (Editor.BaseUnitId > 0) score += 15;
      if (Editor.CategoryId.HasValue) score += 15;
      if (Editor.BusinessPartnerId.HasValue) score += 15;
      if (Editor.PurchaseUnitId.HasValue) score += 10;
      if (Editor.BaseUnitPrice.HasValue) score += 10;
      if (!string.IsNullOrWhiteSpace(Editor.Barcode)) score += 10;
      if (ImagePreviewDataUrl is not null) score += 5;
      return score;
    }
  }

  protected string MaterialReadinessLabel
    => MaterialReadinessPercent switch
    {
      >= 90 => "Lista para operar",
      >= 65 => "Ficha funcional",
      _ => "Completa los datos clave"
    };

  protected string PurchaseConversionSummary
  {
    get
    {
      var baseUnit = FindUnit(Editor.BaseUnitId);
      var purchaseUnit = Editor.PurchaseUnitId.HasValue ? FindUnit(Editor.PurchaseUnitId.Value) : null;
      if (baseUnit is null || purchaseUnit is null || Editor.PurchaseQuantity <= 0)
      {
        return "Define la presentación de compra para visualizar la conversión.";
      }

      return $"1 {GetUnitShortName(purchaseUnit)} = {Editor.PurchaseQuantity:N2} {GetUnitShortName(baseUnit)}";
    }
  }

  protected bool CanCalculatePurchasePresentationPrice
    => Editor.PurchaseUnitId.HasValue && Editor.PurchaseQuantity > 0m;

  protected string BaseUnitPriceLabel
  {
    get
    {
      var unit = FindUnit(Editor.BaseUnitId);
      return unit is null
        ? "Precio por unidad base"
        : $"Precio por unidad base ({GetUnitShortName(unit)})";
    }
  }

  protected string PurchasePresentationPriceLabel
  {
    get
    {
      var unit = Editor.PurchaseUnitId.HasValue ? FindUnit(Editor.PurchaseUnitId.Value) : null;
      return unit is null
        ? "Precio por presentación de compra"
        : $"Precio por presentación ({GetUnitShortName(unit)})";
    }
  }

  protected override async Task OnInitializedAsync()
  {
    RfcState.Changed += HandleRfcChanged;
    var authenticationState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
    IsAdministrator = authenticationState.User.IsInRole("Administrador");
    CurrentUserName = authenticationState.User.Identity?.Name ?? "OrionERP";
    Editor.Rfc = CurrentRfc;
    await LoadCatalogAsync();
    NuevoMaterial();
    await BuscarAsync();
  }

  protected override async Task OnAfterRenderAsync(bool firstRender)
  {
    if (!firstRender)
    {
      return;
    }

    var rfcChangedDuringInitialization = !string.Equals(
      _catalogRfc,
      CurrentRfc,
      StringComparison.OrdinalIgnoreCase);

    if (rfcChangedDuringInitialization)
    {
      await LoadCatalogAsync();
      NuevoMaterial();
    }

    // The persisted RFC can finish restoring while the first interactive circuit
    // is connecting. If that cancels the initial list request, recover once here
    // so users never need to press refresh just to see the catalog.
    if (rfcChangedDuringInitialization || Materials.Count == 0)
    {
      await BuscarAsync();
      StateHasChanged();
    }
  }

  protected async Task BuscarAsync()
  {
    await EnsureCatalogForCurrentRfcAsync();

    _listRequestCts?.Cancel();
    _listRequestCts?.Dispose();
    _listRequestCts = new CancellationTokenSource();
    var requestToken = _listRequestCts.Token;

    HasExecutedSearch = true;
    IsBusy = true;
    CloseMaterialImageModal();

    try
    {
      var page = await GetMaterialsPageAsync(0, requestToken);
      requestToken.ThrowIfCancellationRequested();
      await RecoverLinkedCatalogOptionsAsync(page.Items);
      Materials = page.Items;
      HasMoreMaterials = page.HasMore;
      await CargarMiniaturasMaterialesAsync(page.Items, append: false, requestToken);
    }
    catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
    {
      // A newer filter or search request replaced this one.
    }
    catch (Exception ex)
    {
      Materials = [];
      MaterialThumbnailDataUrls = [];
      HasMoreMaterials = false;
      UiMessages.ShowError($"No se pudo cargar el catálogo de materiales. {ex.Message}");
    }
    finally
    {
      if (_listRequestCts?.Token == requestToken)
      {
        IsBusy = false;
        StateHasChanged();
      }
    }
  }

  protected async Task OnSearchInputAsync(ChangeEventArgs args)
  {
    Filter.SearchText = args.Value?.ToString();
    _searchDebounceCts?.Cancel();
    _searchDebounceCts?.Dispose();
    _searchDebounceCts = new CancellationTokenSource();
    var token = _searchDebounceCts.Token;

    try
    {
      await Task.Delay(SearchDebounceMilliseconds, token);
      await BuscarAsync();
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested)
    {
      // The user is still typing.
    }
  }

  protected async Task ClearSearchAsync()
  {
    Filter.SearchText = null;
    await BuscarAsync();
  }

  protected async Task OnCategoryFilterChangedAsync(ChangeEventArgs args)
  {
    Filter.CategoryId = ParseNullableInt(args.Value);
    await BuscarAsync();
  }

  protected async Task OnMaterialClassFilterChangedAsync(ChangeEventArgs args)
  {
    Filter.MaterialClass = NullIfWhiteSpace(args.Value?.ToString());
    await BuscarAsync();
  }

  protected async Task OnStatusFilterChangedAsync(ChangeEventArgs args)
  {
    Filter.Status = NullIfWhiteSpace(args.Value?.ToString());
    await BuscarAsync();
  }

  protected async Task OnVendorFilterChangedAsync(int? vendorId)
  {
    Filter.VendorId = vendorId;
    await BuscarAsync();
  }

  protected async Task ToggleStatusFilterAsync(string status)
  {
    Filter.Status = string.Equals(Filter.Status, status, StringComparison.OrdinalIgnoreCase) ? null : status;
    await BuscarAsync();
  }

  protected async Task SetImageFilterAsync(bool? value)
  {
    Filter.HasImage = Filter.HasImage == value ? null : value;
    await BuscarAsync();
  }

  protected async Task SetStockFilterAsync(bool? value)
  {
    Filter.HasStock = Filter.HasStock == value ? null : value;
    await BuscarAsync();
  }

  protected async Task ToggleNeedsAttentionAsync()
  {
    Filter.NeedsAttention = !Filter.NeedsAttention;
    await BuscarAsync();
  }

  protected async Task ResetFiltersAsync()
  {
    _searchDebounceCts?.Cancel();
    Filter = new MaterialFilter();
    await BuscarAsync();
  }

  protected async Task ToggleInactiveMaterialsAsync()
  {
    if (!IsAdministrator) return;
    ShowInactiveMaterials = !ShowInactiveMaterials;
    if (!ShowInactiveMaterials && !SelectedMaterialIsActive)
    {
      NuevoMaterial();
    }
    if (!ShowInactiveMaterials && string.Equals(Filter.Status, "INACTIVO", StringComparison.OrdinalIgnoreCase))
    {
      Filter.Status = null;
    }
    await BuscarAsync();
  }

  protected void ToggleAdvancedFilters()
    => ShowAdvancedFilters = !ShowAdvancedFilters;

  protected async Task CargarMasAsync()
  {
    if (IsListBusy || !HasMoreMaterials)
    {
      return;
    }

    IsLoadingMore = true;
    try
    {
      var page = await GetMaterialsPageAsync(Materials.Count, CancellationToken.None);
      Materials.AddRange(page.Items);
      HasMoreMaterials = page.HasMore;
      await CargarMiniaturasMaterialesAsync(page.Items, append: true, CancellationToken.None);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudieron cargar más materiales. {ex.Message}");
    }
    finally
    {
      IsLoadingMore = false;
      StateHasChanged();
    }
  }

  protected void NuevoMaterial()
  {
    ResetLifecycleReport();
    ResetInventoryPanels();
    SelectedMaterialId = null;
    CurrentMaterialCode = null;
    SelectedImageFileName = null;
    ImagePreviewDataUrl = null;
    HasPersistedImage = false;
    SelectedMaterialIsActive = true;
    ShowMoreFields = false;
    CloseMaterialImageModal();
    Editor = CreateNewEditor();
    Editor.Rfc = CurrentRfc;
    if (Catalog.Units.Count > 0)
    {
      Editor.BaseUnitId = Catalog.Units[0].Id;
    }

    SyncPurchasePresentationPriceFromBase();
  }

  protected async Task SeleccionarMaterialAsync(int materialId)
  {
    if (IsLoadingEditor || SelectedMaterialId == materialId)
    {
      return;
    }

    IsLoadingEditor = true;
    ResetLifecycleReport();
    ResetInventoryPanels();
    try
    {
      var detail = await MaterialService.GetMaterialAsync(CurrentRfc, materialId);
      if (detail is null)
      {
        UiMessages.ShowWarning("El material seleccionado ya no existe.");
        return;
      }

      SelectedMaterialId = detail.Id;
      CurrentMaterialCode = detail.MaterialCode;
      SelectedImageFileName = detail.PrimaryImageFileName;
      HasPersistedImage = detail.HasImage;
      SelectedMaterialIsActive = detail.IsActive;
      Editor = new MaterialUpsertRequest
      {
        Rfc = CurrentRfc,
        Id = detail.Id,
        Description = detail.Description,
        BaseUnitId = detail.BaseUnitId,
        PurchaseQuantity = detail.PurchaseQuantity,
        PurchaseUnitId = detail.PurchaseUnitId,
        BusinessPartnerId = detail.BusinessPartnerId,
        BaseUnitPrice = detail.BaseUnitPrice,
        Brand = detail.Brand,
        Model = detail.Model,
        IsPerishable = detail.IsPerishable,
        ShelfLifeDays = detail.ShelfLifeDays,
        RequiresRefrigeration = detail.RequiresRefrigeration,
        Status = detail.Status,
        CategoryId = detail.CategoryId,
        Barcode = detail.Barcode,
        VendorCode = detail.VendorCode,
        PurchaseLink = detail.PurchaseLink,
        MaterialClass = detail.MaterialClass
      };

      SyncPurchasePresentationPriceFromBase();

      await LoadImageAsync(detail.Id);
      await LoadInventoryAsync(detail.Id);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cargar el material. {ex.Message}");
    }
    finally
    {
      IsLoadingEditor = false;
    }
  }

  protected async Task GuardarAsync()
  {
    IsSaving = true;
    try
    {
      Editor.Rfc = CurrentRfc;
      var result = await MaterialService.SaveMaterialAsync(Editor);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      await BuscarAsync();

      if (result.EntityId.HasValue)
      {
        SelectedMaterialId = null;
        await SeleccionarMaterialAsync(result.EntityId.Value);
      }
      else
      {
        NuevoMaterial();
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo guardar el material. {ex.Message}");
    }
    finally
    {
      IsSaving = false;
    }
  }

  protected async Task OpenLifecycleReportAsync()
  {
    if (!Editor.Id.HasValue || Editor.Id.Value <= 0 || IsSaving || IsProcessingLifecycle)
    {
      return;
    }

    ResetLifecycleReport();
    LifecycleTargetMaterialId = Editor.Id.Value;
    ShowLifecycleDialog = true;
    await RefreshLifecycleAssessmentAsync();
  }

  protected async Task RefreshLifecycleAssessmentAsync()
  {
    if (!ShowLifecycleDialog || !LifecycleTargetMaterialId.HasValue)
    {
      return;
    }

    _lifecycleAssessmentCts?.Cancel();
    _lifecycleAssessmentCts?.Dispose();
    _lifecycleAssessmentCts = new CancellationTokenSource();
    var token = _lifecycleAssessmentCts.Token;
    var materialId = LifecycleTargetMaterialId.Value;

    IsLoadingLifecycleAssessment = true;
    LifecycleAssessmentError = null;
    LifecycleAssessment = null;
    DeletionConfirmationText = string.Empty;

    try
    {
      var assessment = await MaterialService.GetMaterialLifecycleAssessmentAsync(CurrentRfc, materialId, token);
      token.ThrowIfCancellationRequested();
      if (!ShowLifecycleDialog || LifecycleTargetMaterialId != materialId)
      {
        return;
      }

      LifecycleAssessment = assessment;
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested)
    {
      // The dialog closed, the RFC changed, or a newer assessment replaced this one.
    }
    catch (Exception ex)
    {
      LifecycleAssessmentError = $"No se pudo revisar el ciclo de vida del material. {ex.Message}";
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

  protected void CloseLifecycleReport()
  {
    if (!IsProcessingLifecycle)
    {
      ResetLifecycleReport();
    }
  }

  protected async Task DeleteMaterialAsync()
  {
    if (IsProcessingLifecycle
        || LifecycleAssessment is not { CanDelete: true, Exists: true }
        || !LifecycleTargetMaterialId.HasValue
        || LifecycleAssessment.MaterialId != LifecycleTargetMaterialId.Value
        || !DeletionConfirmationMatches)
    {
      return;
    }

    var authenticationState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
    IsAdministrator = authenticationState.User.IsInRole("Administrador");
    CurrentUserName = authenticationState.User.Identity?.Name ?? CurrentUserName;
    if (!IsAdministrator)
    {
      UiMessages.ShowError("Solo un administrador puede eliminar materiales permanentemente.");
      return;
    }

    IsProcessingLifecycle = true;
    try
    {
      var result = await MaterialService.DeleteMaterialAsync(new MaterialDeleteRequest
      {
        Rfc = CurrentRfc,
        MaterialId = LifecycleTargetMaterialId.Value,
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
      NuevoMaterial();
      await BuscarAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo eliminar el material. {ex.Message}");
    }
    finally
    {
      IsProcessingLifecycle = false;
    }
  }

  protected async Task DeactivateMaterialAsync()
  {
    if (IsProcessingLifecycle
        || LifecycleAssessment is not { CanDeactivate: true, Exists: true }
        || !LifecycleTargetMaterialId.HasValue
        || LifecycleAssessment.MaterialId != LifecycleTargetMaterialId.Value)
    {
      return;
    }

    if (!await RefreshAdministratorStateAsync("Solo un administrador puede desactivar materiales.")) return;
    IsProcessingLifecycle = true;
    try
    {
      var result = await MaterialService.DeactivateMaterialAsync(new MaterialDeactivateRequest
      {
        Rfc = CurrentRfc,
        MaterialId = LifecycleTargetMaterialId.Value,
        DeactivatedBy = CurrentUserName
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
      NuevoMaterial();
      await BuscarAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo desactivar el material. {ex.Message}");
    }
    finally
    {
      IsProcessingLifecycle = false;
    }
  }

  protected async Task ReactivateMaterialAsync()
  {
    if (IsProcessingLifecycle
        || LifecycleAssessment is not { CanReactivate: true, Exists: true }
        || !LifecycleTargetMaterialId.HasValue
        || LifecycleAssessment.MaterialId != LifecycleTargetMaterialId.Value)
    {
      return;
    }

    if (!await RefreshAdministratorStateAsync("Solo un administrador puede reactivar materiales.")) return;
    IsProcessingLifecycle = true;
    try
    {
      var result = await MaterialService.ReactivateMaterialAsync(new MaterialReactivateRequest
      {
        Rfc = CurrentRfc,
        MaterialId = LifecycleTargetMaterialId.Value,
        ReactivatedBy = CurrentUserName
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
      NuevoMaterial();
      await BuscarAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo reactivar el material. {ex.Message}");
    }
    finally
    {
      IsProcessingLifecycle = false;
    }
  }

  protected async Task OnImageSelectedAsync(InputFileChangeEventArgs args)
  {
    var file = args.File;
    if (file is null)
    {
      return;
    }

    if (file.Size > MaxImageBytes)
    {
      UiMessages.ShowWarning("La imagen supera 8 MB. Elige una imagen más ligera.");
      return;
    }

    if (!file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
    {
      UiMessages.ShowWarning("El archivo seleccionado no es una imagen compatible.");
      return;
    }

    await using var stream = file.OpenReadStream(MaxImageBytes);
    using var ms = new MemoryStream();
    await stream.CopyToAsync(ms);
    Editor.PrimaryImageBytes = ms.ToArray();
    Editor.PrimaryImageFileName = file.Name;
    Editor.PrimaryImageContentType = file.ContentType;
    var thumbnail = await BuildThumbnailAsync(file);
    Editor.PrimaryImageThumbnailBytes = thumbnail.Bytes ?? Editor.PrimaryImageBytes;
    Editor.PrimaryImageThumbnailContentType = thumbnail.ContentType ?? Editor.PrimaryImageContentType;
    Editor.RemovePrimaryImage = false;
    SelectedImageFileName = file.Name;
    ImagePreviewDataUrl = BuildDataUrl(Editor.PrimaryImageContentType, Editor.PrimaryImageBytes);
  }

  protected void RemoveImage()
  {
    Editor.PrimaryImageBytes = null;
    Editor.PrimaryImageFileName = null;
    Editor.PrimaryImageContentType = null;
    Editor.PrimaryImageThumbnailBytes = null;
    Editor.PrimaryImageThumbnailContentType = null;
    Editor.RemovePrimaryImage = Editor.Id.HasValue && HasPersistedImage;
    HasPersistedImage = false;
    SelectedImageFileName = null;
    ImagePreviewDataUrl = null;
  }

  protected void OpenCategoryDialog()
  {
    CategoryDraft = new MaterialCategoryCreateRequest { Rfc = CurrentRfc };
    ShowCategoryDialog = true;
  }

  protected void CloseCategoryDialog()
    => ShowCategoryDialog = false;

  protected async Task SaveCategoryAsync()
  {
    IsSavingMasterData = true;
    try
    {
      CategoryDraft.Rfc = CurrentRfc;
      var result = await MaterialService.CreateCategoryAsync(CategoryDraft);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      await LoadCatalogAsync();
      Editor.CategoryId = result.EntityId;
      ShowCategoryDialog = false;
      UiMessages.ShowSuccess(result.Message);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo crear la categoría. {ex.Message}");
    }
    finally
    {
      IsSavingMasterData = false;
    }
  }

  protected void OpenUnitDialog(UnitSelectionTarget target)
  {
    PendingUnitTarget = target;
    UnitDraft = new UnitOfMeasureCreateRequest();
    ShowUnitDialog = true;
  }

  protected void CloseUnitDialog()
    => ShowUnitDialog = false;

  protected async Task SaveUnitAsync()
  {
    IsSavingMasterData = true;
    try
    {
      var result = await MaterialService.CreateUnitAsync(UnitDraft);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      await LoadCatalogAsync();
      if (PendingUnitTarget == UnitSelectionTarget.Base)
      {
        Editor.BaseUnitId = result.EntityId ?? Editor.BaseUnitId;
      }
      else
      {
        OnPurchaseUnitChanged(result.EntityId);
      }

      ShowUnitDialog = false;
      UiMessages.ShowSuccess(result.Message);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo crear la unidad. {ex.Message}");
    }
    finally
    {
      IsSavingMasterData = false;
    }
  }

  protected void OpenVendorDialog()
  {
    VendorDraft = CreateVendorDraft(CurrentRfc);
    ShowVendorDialog = true;
  }

  protected void CloseVendorDialog()
    => ShowVendorDialog = false;

  protected async Task SaveVendorAsync()
  {
    IsSavingMasterData = true;
    try
    {
      VendorDraft.OwnerRfc = CurrentRfc;
      VendorDraft.Roles = ["Vendor"];
      VendorDraft.VendorProfile = new VendorProfileUpsertRequest { IsApproved = true };
      var result = await BusinessPartnerService.SavePartnerAsync(VendorDraft);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      await LoadCatalogAsync();
      Editor.BusinessPartnerId = result.EntityId;
      ShowVendorDialog = false;
      UiMessages.ShowSuccess(result.Message);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo crear el proveedor. {ex.Message}");
    }
    finally
    {
      IsSavingMasterData = false;
    }
  }

  protected Task OnEditorVendorChangedAsync(int? vendorId)
  {
    Editor.BusinessPartnerId = vendorId;
    return Task.CompletedTask;
  }

  protected void OnPurchaseUnitChanged(int? purchaseUnitId)
  {
    Editor.PurchaseUnitId = purchaseUnitId;
    SyncPurchasePresentationPriceFromBase();
  }

  protected void OnPurchaseQuantityChanged(ChangeEventArgs args)
  {
    _purchaseQuantityInputText = Convert.ToString(args.Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
    if (decimal.TryParse(_purchaseQuantityInputText, NumberStyles.Number, CultureInfo.InvariantCulture, out var purchaseQuantity))
    {
      Editor.PurchaseQuantity = purchaseQuantity;
      UpdatePurchasePresentationPriceFromBase();
      return;
    }

    Editor.PurchaseQuantity = 0m;
    _purchasePresentationPrice = null;
    _purchasePresentationPriceInputText = string.Empty;
  }

  protected void OnBaseUnitPriceChanged(ChangeEventArgs args)
  {
    _baseUnitPriceInputText = Convert.ToString(args.Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
    if (string.IsNullOrEmpty(_baseUnitPriceInputText))
    {
      Editor.BaseUnitPrice = null;
      _purchasePresentationPrice = null;
      _purchasePresentationPriceInputText = string.Empty;
      return;
    }

    if (decimal.TryParse(_baseUnitPriceInputText, NumberStyles.Number, CultureInfo.InvariantCulture, out var baseUnitPrice))
    {
      Editor.BaseUnitPrice = MaterialPriceCalculator.NormalizeBaseUnitPrice(baseUnitPrice);
      UpdatePurchasePresentationPriceFromBase();
    }
  }

  protected void OnPurchasePresentationPriceChanged(ChangeEventArgs args)
  {
    _purchasePresentationPriceInputText = Convert.ToString(args.Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
    if (string.IsNullOrEmpty(_purchasePresentationPriceInputText))
    {
      _purchasePresentationPrice = null;
      Editor.BaseUnitPrice = null;
      _baseUnitPriceInputText = string.Empty;
      return;
    }

    if (!decimal.TryParse(_purchasePresentationPriceInputText, NumberStyles.Number, CultureInfo.InvariantCulture, out var purchasePresentationPrice))
    {
      return;
    }

    _purchasePresentationPrice = decimal.Round(purchasePresentationPrice, MaterialPriceCalculator.PurchasePresentationPriceScale, MidpointRounding.AwayFromZero);
    Editor.BaseUnitPrice = MaterialPriceCalculator.CalculateBaseUnitPrice(
      _purchasePresentationPrice,
      Editor.PurchaseQuantity);
    _baseUnitPriceInputText = FormatBaseUnitPriceInput(Editor.BaseUnitPrice);
  }

  protected async Task AbrirImagenMaterialAsync(MaterialListItemDto item)
  {
    MaterialImageModalTitle = string.IsNullOrWhiteSpace(item.Description)
      ? item.MaterialCode
      : string.IsNullOrWhiteSpace(item.MaterialCode)
        ? item.Description
        : $"{item.Description} · {item.MaterialCode}";
    MaterialImageModalDataUrl = TryGetMaterialThumbnailDataUrl(item.Id, out var thumbnailDataUrl)
      ? thumbnailDataUrl
      : null;
    ShowMaterialImageModal = true;
    IsLoadingMaterialImage = true;

    try
    {
      var image = await MaterialService.GetMaterialImageAsync(CurrentRfc, item.Id);
      if (image is null)
      {
        if (MaterialImageModalDataUrl is null)
        {
          UiMessages.ShowWarning("El material seleccionado no tiene imagen disponible.");
        }

        return;
      }

      MaterialImageModalDataUrl = BuildDataUrl(image.ContentType, image.Bytes);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cargar la imagen del material. {ex.Message}");
    }
    finally
    {
      IsLoadingMaterialImage = false;
    }
  }

  protected void CloseMaterialImageModal()
  {
    ShowMaterialImageModal = false;
    IsLoadingMaterialImage = false;
    MaterialImageModalTitle = null;
    MaterialImageModalDataUrl = null;
  }

  protected string GetMaterialClassLabel(string? materialClass)
    => materialClass switch
    {
      "Consumable" => "Consumible",
      "Reusable" => "Reutilizable",
      "Installed" => "Instalado",
      "AssetLike" => "Equipo / activo",
      _ => string.IsNullOrWhiteSpace(materialClass) ? "Sin clase" : materialClass
    };

  protected string GetStatusLabel(string? status)
    => status switch
    {
      "ACTIVO" => "Activo",
      "INACTIVO" => "Inactivo",
      "OBSOLETO" => "Obsoleto",
      _ => string.IsNullOrWhiteSpace(status) ? "Sin estado" : status
    };

  protected string GetStatusBadgeClass(string? status)
    => status switch
    {
      "ACTIVO" => "is-active",
      "OBSOLETO" => "is-obsolete",
      _ => "is-inactive"
    };

  protected string GetMaterialStatusLabel(MaterialListItemDto material)
    => material.IsActive ? GetStatusLabel(material.Status) : "Desactivado";

  protected string GetMaterialStatusBadgeClass(MaterialListItemDto material)
    => material.IsActive ? GetStatusBadgeClass(material.Status) : "is-inactive";

  protected IEnumerable<string> EditableStatusOptions
    => SelectedMaterialIsActive
      ? Catalog.Statuses.Where(status => !string.Equals(status, "INACTIVO", StringComparison.OrdinalIgnoreCase))
      : Catalog.Statuses.Where(status => string.Equals(status, "INACTIVO", StringComparison.OrdinalIgnoreCase));

  protected IEnumerable<MaterialDependencySection> GetDependencySections(MaterialLifecycleAssessmentDto assessment)
  {
    if (assessment.OperationalBlockers.Count > 0)
    {
      yield return new MaterialDependencySection(
        "operational",
        "Vínculos operativos por resolver",
        "Estas relaciones deben cerrarse, retirarse o desactivarse antes de continuar.",
        "bi-exclamation-diamond-fill",
        assessment.OperationalBlockers);
    }
    if (assessment.HistoricalReferences.Count > 0)
    {
      yield return new MaterialDependencySection(
        "historical",
        "Historial que debe conservarse",
        "Estas referencias son evidencia operativa. Nunca se eliminan desde el retiro del material.",
        "bi-clock-history",
        assessment.HistoricalReferences);
    }
    if (assessment.ConfigurationReferences.Count > 0)
    {
      yield return new MaterialDependencySection(
        "configuration",
        "Configuración desvinculable",
        "Esta configuración inactiva todavía impide una eliminación física, pero no una baja respaldada por historial.",
        "bi-sliders",
        assessment.ConfigurationReferences);
    }
  }

  protected string? GetResolutionPermissionNote(MaterialDependencyDto dependency)
    => dependency.ResolutionUrl?.StartsWith("/restaurante/", StringComparison.OrdinalIgnoreCase) == true
      ? "Requiere permisos de administración de Restaurante."
      : null;

  protected string GetFilterChipClass(bool active)
    => active ? "materiales-filter-chip is-active" : "materiales-filter-chip";

  protected string GetUnitDisplay(LookupOptionDto option)
    => string.IsNullOrWhiteSpace(option.Code) ? option.Name : $"{option.Name} ({option.Code})";

  protected string GetMaterialAriaLabel(MaterialListItemDto item)
    => $"Editar {item.Description}, código {item.MaterialCode}";

  protected string GetMaterialRowClass(MaterialListItemDto item)
    => item.Id == SelectedMaterialId ? "materiales-result-card is-selected" : "materiales-result-card";

  protected bool MaterialNeedsAttention(MaterialListItemDto item)
    => string.IsNullOrWhiteSpace(item.VendorName)
       || string.IsNullOrWhiteSpace(item.CategoryName)
       || string.IsNullOrWhiteSpace(item.Barcode)
       || !item.HasImage;

  protected string? GetMaterialThumbnailDataUrl(int materialId)
    => TryGetMaterialThumbnailDataUrl(materialId, out var dataUrl) ? dataUrl : null;

  protected MaterialInventorySnapshotDto? Inventory { get; set; }
  protected bool IsLoadingInventory { get; set; }
  protected string? InventoryError { get; set; }
  protected bool ShowRemovedStockLocations { get; set; }

  protected List<MaterialMovementDto> Movements { get; set; } = [];
  protected bool IsLoadingMovements { get; set; }
  protected bool IsLoadingMoreMovements { get; set; }
  protected bool HasMoreMovements { get; set; }
  protected string? MovementsError { get; set; }
  protected bool ShowMovementFilters { get; set; }
  protected int? MovementLocationFilter { get; set; }
  protected string? MovementTypeFilter { get; set; }
  protected DateTime? MovementFromDate { get; set; }
  protected DateTime? MovementToDate { get; set; }
  protected string MovementSearchText { get; set; } = string.Empty;

  protected bool HasInventoryContext => SelectedMaterialId.HasValue;

  protected int ActiveMovementFilterCount
  {
    get
    {
      var count = 0;
      if (MovementLocationFilter.HasValue) count++;
      if (!string.IsNullOrWhiteSpace(MovementTypeFilter)) count++;
      if (MovementFromDate.HasValue) count++;
      if (MovementToDate.HasValue) count++;
      if (!string.IsNullOrWhiteSpace(MovementSearchText)) count++;
      return count;
    }
  }

  protected string BaseUnitShortName
  {
    get
    {
      var unit = FindUnit(Editor.BaseUnitId);
      return unit is null ? "unid." : GetUnitShortName(unit);
    }
  }

  protected IReadOnlyList<MaterialStockLocationDto> VisibleStockLocations
    => Inventory is null
      ? Array.Empty<MaterialStockLocationDto>()
      : ShowRemovedStockLocations
        ? Inventory.Locations
        : Inventory.StoredLocations;

  protected async Task RefreshInventoryAsync()
  {
    if (!SelectedMaterialId.HasValue)
    {
      return;
    }

    await LoadInventoryAsync(SelectedMaterialId.Value, resetFilters: false);
  }

  protected void ToggleRemovedStockLocations()
    => ShowRemovedStockLocations = !ShowRemovedStockLocations;

  protected void ToggleMovementFilters()
    => ShowMovementFilters = !ShowMovementFilters;

  protected Task ApplyMovementFiltersAsync()
    => LoadMovementsAsync(reset: true);

  protected async Task ResetMovementFiltersAsync()
  {
    ResetMovementFilters();
    await LoadMovementsAsync(reset: true);
  }

  protected async Task FilterMovementsByLocationAsync(int locationId)
  {
    MovementLocationFilter = MovementLocationFilter == locationId ? null : locationId;
    ShowMovementFilters = true;
    await LoadMovementsAsync(reset: true);
  }

  protected async Task LoadMoreMovementsAsync()
  {
    if (IsLoadingMovements || IsLoadingMoreMovements || !HasMoreMovements)
    {
      return;
    }

    await LoadMovementsAsync(reset: false);
  }

  private async Task LoadInventoryAsync(int materialId, bool resetFilters = true)
  {
    _inventoryRequestCts?.Cancel();
    _inventoryRequestCts?.Dispose();
    _inventoryRequestCts = new CancellationTokenSource();
    var inventoryToken = _inventoryRequestCts.Token;

    if (resetFilters)
    {
      ResetMovementFilters();
      ShowRemovedStockLocations = false;
    }

    IsLoadingInventory = true;
    InventoryError = null;

    try
    {
      Inventory = await MaterialService.GetMaterialInventoryAsync(CurrentRfc, materialId, inventoryToken);
      DropUnavailableMovementFilters();
    }
    catch (OperationCanceledException) when (inventoryToken.IsCancellationRequested)
    {
      return;
    }
    catch (Exception ex)
    {
      Inventory = null;
      InventoryError = $"No se pudo cargar el inventario del material. {ex.Message}";
    }
    finally
    {
      IsLoadingInventory = false;
    }

    await LoadMovementsAsync(reset: true);
  }

  private async Task LoadMovementsAsync(bool reset)
  {
    if (!SelectedMaterialId.HasValue)
    {
      Movements = [];
      HasMoreMovements = false;
      return;
    }

    _movementRequestCts?.Cancel();
    _movementRequestCts?.Dispose();
    _movementRequestCts = new CancellationTokenSource();
    var movementToken = _movementRequestCts.Token;

    if (reset)
    {
      Movements = [];
      HasMoreMovements = false;
      IsLoadingMovements = true;
    }
    else
    {
      IsLoadingMoreMovements = true;
    }

    MovementsError = null;

    try
    {
      var page = await MaterialService.GetMaterialMovementsAsync(
        BuildMovementFilter(Movements.Count),
        movementToken);

      HasMoreMovements = page.Count > MovementPageSize;
      Movements.AddRange(page.Take(MovementPageSize));
    }
    catch (OperationCanceledException) when (movementToken.IsCancellationRequested)
    {
      // A newer history request replaced this one.
    }
    catch (Exception ex)
    {
      MovementsError = $"No se pudo cargar el historial de movimientos. {ex.Message}";
    }
    finally
    {
      IsLoadingMovements = false;
      IsLoadingMoreMovements = false;
    }
  }

  private MaterialMovementFilter BuildMovementFilter(int skip)
    => new()
    {
      Rfc = CurrentRfc,
      MaterialId = SelectedMaterialId ?? 0,
      LocationId = MovementLocationFilter,
      TransactionType = NullIfWhiteSpace(MovementTypeFilter),
      OccurredFromUtc = ToUtcRangeStart(MovementFromDate),
      OccurredToUtc = ToUtcRangeEndExclusive(MovementToDate),
      SearchText = NullIfWhiteSpace(MovementSearchText),
      Skip = skip,
      Take = MovementQueryTake
    };

  private void DropUnavailableMovementFilters()
  {
    if (Inventory is null)
    {
      return;
    }

    if (MovementLocationFilter.HasValue
        && !Inventory.Locations.Any(location => location.LocationId == MovementLocationFilter.Value))
    {
      MovementLocationFilter = null;
    }

    if (!string.IsNullOrWhiteSpace(MovementTypeFilter)
        && !Inventory.MovementTypes.Any(option =>
          string.Equals(option.TransactionType, MovementTypeFilter, StringComparison.OrdinalIgnoreCase)))
    {
      MovementTypeFilter = null;
    }
  }

  private void ResetMovementFilters()
  {
    MovementLocationFilter = null;
    MovementTypeFilter = null;
    MovementFromDate = null;
    MovementToDate = null;
    MovementSearchText = string.Empty;
  }

  private void ResetInventoryPanels()
  {
    _inventoryRequestCts?.Cancel();
    _inventoryRequestCts?.Dispose();
    _inventoryRequestCts = null;
    _movementRequestCts?.Cancel();
    _movementRequestCts?.Dispose();
    _movementRequestCts = null;
    Inventory = null;
    InventoryError = null;
    IsLoadingInventory = false;
    Movements = [];
    MovementsError = null;
    IsLoadingMovements = false;
    IsLoadingMoreMovements = false;
    HasMoreMovements = false;
    ShowRemovedStockLocations = false;
    ShowMovementFilters = false;
    ResetMovementFilters();
  }

  private static DateTime? ToUtcRangeStart(DateTime? localDate)
    => localDate.HasValue
      ? DateTime.SpecifyKind(localDate.Value.Date, DateTimeKind.Local).ToUniversalTime()
      : null;

  private static DateTime? ToUtcRangeEndExclusive(DateTime? localDate)
    => localDate.HasValue
      ? DateTime.SpecifyKind(localDate.Value.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime()
      : null;

  protected string FormatQuantity(decimal value)
    => value.ToString("N2", CultureInfo.CurrentCulture);

  protected string FormatQuantityWithUnit(decimal value)
    => $"{FormatQuantity(value)} {BaseUnitShortName}";

  protected string FormatOptionalQuantity(decimal? value)
    => value.HasValue ? FormatQuantity(value.Value) : "—";

  protected string FormatSignedQuantity(decimal value)
    => value > 0m
      ? $"+{FormatQuantity(value)}"
      : FormatQuantity(value);

  protected static string FormatMoney(decimal value)
    => value.ToString("C2", CultureInfo.CurrentCulture);

  protected static string FormatMoment(DateTime? value)
    => value.HasValue ? value.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : "—";

  protected static string FormatDay(DateTime? value)
    => value.HasValue ? value.Value.ToString("dd/MM/yyyy") : "—";

  protected static string FormatRelativeMoment(DateTime? value)
  {
    if (!value.HasValue)
    {
      return "Sin registro";
    }

    var elapsed = DateTime.UtcNow - DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
    return elapsed switch
    {
      { TotalMinutes: < 1 } => "Hace un momento",
      { TotalMinutes: < 60 } => $"Hace {(int)elapsed.TotalMinutes} min",
      { TotalHours: < 24 } => $"Hace {(int)elapsed.TotalHours} h",
      { TotalDays: < 31 } => $"Hace {(int)elapsed.TotalDays} d",
      _ => FormatMoment(value)
    };
  }

  protected string GetStockLocationTitle(MaterialStockLocationDto location)
    => string.IsNullOrWhiteSpace(location.LocationName)
      ? location.LocationCode
      : location.LocationName;

  protected string GetStockLocationPath(MaterialStockLocationDto location)
  {
    var segments = new List<string>();
    if (!string.IsNullOrWhiteSpace(location.RoomName)) segments.Add(location.RoomName!.Trim());
    if (!string.IsNullOrWhiteSpace(location.ParentLocationName)) segments.Add(location.ParentLocationName!.Trim());
    return segments.Count == 0 ? "Sin agrupación" : string.Join(" › ", segments);
  }

  protected static string GetLocationTypeLabel(string? locationType)
    => locationType switch
    {
      "Warehouse" => "Almacén",
      "Storage" => "Almacenaje",
      "Disposal" => "Baja / desecho",
      "Room" => "Habitación",
      "Area" => "Área",
      "Shelf" => "Estante",
      "Rack" => "Rack",
      "Bin" => "Contenedor",
      "Kitchen" => "Cocina",
      "Bar" => "Barra",
      _ => string.IsNullOrWhiteSpace(locationType) ? "Ubicación" : locationType
    };

  protected static string GetCoverageLabel(MaterialStockLocationDto location)
    => location.CoverageState switch
    {
      "low" => "Bajo mínimo",
      "over" => "Sobre máximo",
      "ok" => "En rango",
      _ => "Sin parámetros"
    };

  protected static string GetCoverageCssClass(MaterialStockLocationDto location)
    => $"is-{location.CoverageState}";

  protected static int GetStockFillPercent(MaterialStockLocationDto location)
  {
    var reference = location.MaxQuantity ?? 0m;
    if (reference <= 0m)
    {
      return location.Quantity > 0m ? 100 : 0;
    }

    return ClampPercent(location.Quantity / reference * 100m);
  }

  protected static int GetStockMinimumMarkerPercent(MaterialStockLocationDto location)
  {
    var reference = location.MaxQuantity ?? 0m;
    var minimum = location.MinQuantity ?? 0m;
    if (reference <= 0m || minimum <= 0m)
    {
      return 0;
    }

    return ClampPercent(minimum / reference * 100m);
  }

  private static int ClampPercent(decimal value)
    => (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0m, 100m);

  protected static string GetMovementTypeLabel(string? transactionType)
    => transactionType switch
    {
      "OpeningBalance" => "Saldo inicial",
      "Added" => "Alta en ubicación",
      "Removed" => "Retiro de ubicación",
      "Reactivated" => "Reactivación",
      "PurchaseReceipt" => "Recepción de compra",
      "CountAdjustment" => "Ajuste por conteo",
      "TransferIn" => "Traspaso entrante",
      "TransferOut" => "Traspaso saliente",
      "Transfer" => "Traspaso",
      "Adjustment" => "Ajuste de inventario",
      "Waste" => "Merma",
      "RestaurantConsumption" => "Consumo de restaurante",
      "ProductionConsumption" => "Consumo de producción",
      "ProductionOutput" => "Producción terminada",
      _ => string.IsNullOrWhiteSpace(transactionType) ? "Movimiento" : transactionType
    };

  protected static string GetMovementTypeIcon(string? transactionType)
    => transactionType switch
    {
      "OpeningBalance" => "bi-flag",
      "Added" => "bi-plus-circle",
      "Removed" => "bi-dash-circle",
      "Reactivated" => "bi-arrow-counterclockwise",
      "PurchaseReceipt" => "bi-box-arrow-in-down",
      "CountAdjustment" => "bi-clipboard-check",
      "TransferIn" => "bi-box-arrow-in-right",
      "TransferOut" => "bi-box-arrow-right",
      "Transfer" => "bi-arrow-left-right",
      "Adjustment" => "bi-sliders",
      "Waste" => "bi-trash3",
      "RestaurantConsumption" => "bi-cup-hot",
      "ProductionConsumption" => "bi-gear",
      "ProductionOutput" => "bi-boxes",
      _ => "bi-arrow-left-right"
    };

  protected static string GetMovementDirectionCssClass(MaterialMovementDto movement)
    => movement.IsInbound ? "is-in" : movement.IsOutbound ? "is-out" : "is-flat";

  protected static string GetMovementLocationLabel(MaterialMovementDto movement)
  {
    var name = string.IsNullOrWhiteSpace(movement.LocationName) ? movement.LocationCode : movement.LocationName;
    if (string.IsNullOrWhiteSpace(name))
    {
      return $"Ubicación #{movement.LocationId}";
    }

    return string.IsNullOrWhiteSpace(movement.RoomName) ? name!.Trim() : $"{movement.RoomName!.Trim()} › {name!.Trim()}";
  }

  protected static string? GetMovementReferenceLabel(MaterialMovementDto movement)
  {
    var referenceType = GetReferenceTypeLabel(movement.ReferenceType);
    if (referenceType is null)
    {
      return movement.ReferenceId.HasValue ? $"#{movement.ReferenceId.Value}" : null;
    }

    return movement.ReferenceId.HasValue ? $"{referenceType} #{movement.ReferenceId.Value}" : referenceType;
  }

  private static string? GetReferenceTypeLabel(string? referenceType)
  {
    var normalized = referenceType?.Trim();
    if (string.IsNullOrEmpty(normalized))
    {
      return null;
    }

    return normalized switch
    {
      "PurchaseReceipt" => "Recepción",
      "InventoryReservation" => "Reserva",
      "LegacyInventory" => "Inventario heredado",
      "StockBalance" => "Asignación de ubicación",
      "PurchaseOrder" => "Orden de compra",
      "PhysicalCountSession" => "Conteo físico",
      "InventoryTransfer" => "Traspaso",
      "InventoryAdjustment" => "Ajuste",
      "RestaurantOrder" => "Comanda",
      "ProductionOrder" => "Orden de producción",
      _ => normalized
    };
  }

  private async Task LoadCatalogAsync(bool allowEmptyRetry = true)
  {
    var rfc = CurrentRfc;
    Catalog = await MaterialService.GetCatalogAsync(rfc);
    _catalogRfc = rfc;
    _catalogRetryPending = allowEmptyRetry
      && Catalog.Categories.Count == 0
      && Catalog.Vendors.Count == 0;
    if (Catalog.Units.Count > 0 && Editor.BaseUnitId == 0)
    {
      Editor.BaseUnitId = Catalog.Units[0].Id;
    }
  }

  private async Task EnsureCatalogForCurrentRfcAsync()
  {
    if (!string.Equals(_catalogRfc, CurrentRfc, StringComparison.OrdinalIgnoreCase))
    {
      await LoadCatalogAsync();
      return;
    }

    if (_catalogRetryPending)
    {
      _catalogRetryPending = false;
      await LoadCatalogAsync(allowEmptyRetry: false);
    }
  }

  private async Task RecoverLinkedCatalogOptionsAsync(IReadOnlyList<MaterialListItemDto> materials)
  {
    var needsCategories = Catalog.Categories.Count == 0
      && materials.Any(material => !string.IsNullOrWhiteSpace(material.CategoryName));
    var needsVendors = Catalog.Vendors.Count == 0
      && materials.Any(material => !string.IsNullOrWhiteSpace(material.VendorName));

    if ((!needsCategories && !needsVendors)
        || string.Equals(_catalogRecoveryRfc, CurrentRfc, StringComparison.OrdinalIgnoreCase))
    {
      return;
    }

    _catalogRecoveryRfc = CurrentRfc;
    await LoadCatalogAsync(allowEmptyRetry: false);
  }

  private async Task LoadImageAsync(int materialId)
  {
    var image = await MaterialService.GetMaterialImageAsync(CurrentRfc, materialId);
    ImagePreviewDataUrl = image is null
      ? null
      : BuildDataUrl(image.ContentType, image.Bytes);
    HasPersistedImage = image is not null;
  }

  private async Task<(List<MaterialListItemDto> Items, bool HasMore)> GetMaterialsPageAsync(int skip, CancellationToken ct)
  {
    var rows = (await MaterialService.GetMaterialsAsync(CreateQueryFilter(skip, QueryTake), ct)).ToList();
    var hasMore = rows.Count > PageSize;
    if (hasMore)
    {
      rows = rows.Take(PageSize).ToList();
    }

    return (rows, hasMore);
  }

  private async Task CargarMiniaturasMaterialesAsync(
    IEnumerable<MaterialListItemDto> materials,
    bool append,
    CancellationToken ct)
  {
    if (!append)
    {
      MaterialThumbnailDataUrls = [];
    }

    var materialIds = materials
      .Where(material => material.HasImage)
      .Select(material => material.Id)
      .Distinct()
      .Where(materialId => !append || !MaterialThumbnailDataUrls.ContainsKey(materialId))
      .ToList();

    if (materialIds.Count == 0)
    {
      return;
    }

    try
    {
      var thumbnails = await MaterialService.GetMaterialThumbnailsAsync(CurrentRfc, materialIds, ct);
      var thumbnailDataUrls = thumbnails
        .Where(thumbnail => thumbnail.Bytes.Length > 0)
        .ToDictionary(
          thumbnail => thumbnail.Id,
          thumbnail => BuildDataUrl(thumbnail.ContentType, thumbnail.Bytes));

      if (append)
      {
        foreach (var item in thumbnailDataUrls)
        {
          MaterialThumbnailDataUrls[item.Key] = item.Value;
        }
      }
      else
      {
        MaterialThumbnailDataUrls = thumbnailDataUrls;
      }
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception ex)
    {
      if (!append)
      {
        MaterialThumbnailDataUrls = [];
      }

      UiMessages.ShowWarning($"No se pudieron cargar las miniaturas de los materiales. {ex.Message}");
    }
  }

  private MaterialFilter CreateQueryFilter(int skip, int take)
    => new()
    {
      Rfc = CurrentRfc,
      SearchText = Filter.SearchText,
      CategoryId = Filter.CategoryId,
      VendorId = Filter.VendorId,
      MaterialClass = Filter.MaterialClass,
      Status = Filter.Status,
      IncludeInactive = IsAdministrator && ShowInactiveMaterials,
      HasImage = Filter.HasImage,
      HasStock = Filter.HasStock,
      NeedsAttention = Filter.NeedsAttention,
      Skip = skip,
      Take = take
    };

  private bool TryGetMaterialThumbnailDataUrl(int materialId, out string dataUrl)
    => MaterialThumbnailDataUrls.TryGetValue(materialId, out dataUrl!);

  private LookupOptionDto? FindUnit(int unitId)
    => Catalog.Units.FirstOrDefault(unit => unit.Id == unitId);

  private static string GetUnitShortName(LookupOptionDto unit)
    => string.IsNullOrWhiteSpace(unit.Code) ? unit.Name : unit.Code;

  private void SyncPurchasePresentationPriceFromBase()
  {
    _purchaseQuantityInputText = Editor.PurchaseQuantity.ToString("0.####", CultureInfo.InvariantCulture);
    _baseUnitPriceInputText = FormatBaseUnitPriceInput(Editor.BaseUnitPrice);
    UpdatePurchasePresentationPriceFromBase();
  }

  private void UpdatePurchasePresentationPriceFromBase()
  {
    _purchasePresentationPrice = CanCalculatePurchasePresentationPrice
      ? MaterialPriceCalculator.CalculatePurchasePresentationPrice(Editor.BaseUnitPrice, Editor.PurchaseQuantity)
      : null;
    _purchasePresentationPriceInputText = _purchasePresentationPrice?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty;
  }

  private static string FormatBaseUnitPriceInput(decimal? baseUnitPrice)
    => baseUnitPrice?.ToString("0.######", CultureInfo.InvariantCulture) ?? string.Empty;

  private static MaterialUpsertRequest CreateNewEditor()
    => new()
    {
      PurchaseQuantity = 1m,
      MaterialClass = "Consumable",
      Status = "ACTIVO"
    };

  private static BusinessPartnerUpsertRequest CreateVendorDraft(string ownerRfc)
    => new()
    {
      OwnerRfc = ownerRfc,
      DisplayName = string.Empty,
      IsActive = true,
      Roles = ["Vendor"],
      VendorProfile = new VendorProfileUpsertRequest { IsApproved = true }
    };

  private string CurrentRfc => LogisticsRfc.Require(RfcState.CurrentRfc);

  private void HandleRfcChanged()
  {
    ResetLifecycleReport();
    ShowInactiveMaterials = false;
    _rfcReloadCts?.Cancel();
    _rfcReloadCts?.Dispose();
    _rfcReloadCts = new CancellationTokenSource();
    var reloadToken = _rfcReloadCts.Token;
    _ = InvokeAsync(() => ReloadForRfcChangeAsync(reloadToken));
  }

  private async Task ReloadForRfcChangeAsync(CancellationToken reloadToken)
  {
    try
    {
      // RfcStateInitializer can publish the claims default and the persisted
      // selection back-to-back. Coalesce them so only the final RFC wins.
      await Task.Delay(120, reloadToken);
      reloadToken.ThrowIfCancellationRequested();

      _searchDebounceCts?.Cancel();
      _listRequestCts?.Cancel();
      Materials = [];
      MaterialThumbnailDataUrls = [];
      HasExecutedSearch = false;
      Filter = new MaterialFilter();
      ShowInactiveMaterials = false;
      SelectedMaterialId = null;
      _catalogRecoveryRfc = null;
      await LoadCatalogAsync();
      reloadToken.ThrowIfCancellationRequested();
      NuevoMaterial();
      await BuscarAsync();
      reloadToken.ThrowIfCancellationRequested();
      StateHasChanged();
    }
    catch (OperationCanceledException) when (reloadToken.IsCancellationRequested)
    {
      // A newer RFC selection replaced this reload.
    }
  }

  public void Dispose()
  {
    RfcState.Changed -= HandleRfcChanged;
    _searchDebounceCts?.Cancel();
    _searchDebounceCts?.Dispose();
    _listRequestCts?.Cancel();
    _listRequestCts?.Dispose();
    _rfcReloadCts?.Cancel();
    _rfcReloadCts?.Dispose();
    _lifecycleAssessmentCts?.Cancel();
    _lifecycleAssessmentCts?.Dispose();
    _inventoryRequestCts?.Cancel();
    _inventoryRequestCts?.Dispose();
    _movementRequestCts?.Cancel();
    _movementRequestCts?.Dispose();
  }

  private void ResetLifecycleReport()
  {
    _lifecycleAssessmentCts?.Cancel();
    _lifecycleAssessmentCts?.Dispose();
    _lifecycleAssessmentCts = null;
    ShowLifecycleDialog = false;
    IsLoadingLifecycleAssessment = false;
    LifecycleTargetMaterialId = null;
    LifecycleAssessment = null;
    LifecycleAssessmentError = null;
    DeletionConfirmationText = string.Empty;
  }

  private async Task<bool> RefreshAdministratorStateAsync(string failureMessage)
  {
    var authenticationState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
    IsAdministrator = authenticationState.User.IsInRole("Administrador");
    CurrentUserName = authenticationState.User.Identity?.Name ?? CurrentUserName;
    if (IsAdministrator) return true;
    UiMessages.ShowError(failureMessage);
    return false;
  }

  private static int? ParseNullableInt(object? value)
    => int.TryParse(value?.ToString(), out var parsed) ? parsed : null;

  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private static string BuildDataUrl(string? contentType, byte[] bytes)
  {
    var safeContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
    return FormattableString.Invariant($"data:{safeContentType};base64,{Convert.ToBase64String(bytes)}");
  }

  private static async Task<(byte[]? Bytes, string? ContentType)> BuildThumbnailAsync(IBrowserFile file)
  {
    try
    {
      var thumbnailFile = await file.RequestImageFileAsync("image/jpeg", ThumbnailMaxPixels, ThumbnailMaxPixels);
      await using var thumbnailStream = thumbnailFile.OpenReadStream(MaxImageBytes);
      using var ms = new MemoryStream();
      await thumbnailStream.CopyToAsync(ms);
      return (ms.ToArray(), thumbnailFile.ContentType);
    }
    catch
    {
      return (null, null);
    }
  }

  protected enum UnitSelectionTarget
  {
    Base,
    Purchase
  }

  protected sealed record MaterialDependencySection(
    string CssClass,
    string Title,
    string Description,
    string IconClass,
    IReadOnlyList<MaterialDependencyDto> Dependencies);
}
