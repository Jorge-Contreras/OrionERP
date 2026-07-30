using Microsoft.AspNetCore.Components;
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
  private const int SearchDebounceMilliseconds = 320;
  private const long MaxImageBytes = 8 * 1024 * 1024;

  [Inject] private IMaterialService MaterialService { get; set; } = default!;
  [Inject] private IBusinessPartnerService BusinessPartnerService { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;
  [Inject] private IUserRfcState RfcState { get; set; } = default!;

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
  protected bool IsListBusy => IsBusy || IsLoadingMore;

  private CancellationTokenSource? _searchDebounceCts;
  private CancellationTokenSource? _listRequestCts;
  private CancellationTokenSource? _rfcReloadCts;
  private string? _catalogRfc;
  private bool _catalogRetryPending;
  private string? _catalogRecoveryRfc;

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
      if (Editor.Price.HasValue) score += 10;
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

  protected override async Task OnInitializedAsync()
  {
    RfcState.Changed += HandleRfcChanged;
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
    SelectedMaterialId = null;
    CurrentMaterialCode = null;
    SelectedImageFileName = null;
    ImagePreviewDataUrl = null;
    HasPersistedImage = false;
    ShowMoreFields = false;
    CloseMaterialImageModal();
    Editor = CreateNewEditor();
    Editor.Rfc = CurrentRfc;
    if (Catalog.Units.Count > 0)
    {
      Editor.BaseUnitId = Catalog.Units[0].Id;
    }
  }

  protected async Task SeleccionarMaterialAsync(int materialId)
  {
    if (IsLoadingEditor || SelectedMaterialId == materialId)
    {
      return;
    }

    IsLoadingEditor = true;
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
      Editor = new MaterialUpsertRequest
      {
        Rfc = CurrentRfc,
        Id = detail.Id,
        Description = detail.Description,
        BaseUnitId = detail.BaseUnitId,
        PurchaseQuantity = detail.PurchaseQuantity,
        PurchaseUnitId = detail.PurchaseUnitId,
        BusinessPartnerId = detail.BusinessPartnerId,
        Price = detail.Price,
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
        MaterialClass = detail.MaterialClass,
        IsActive = detail.IsActive
      };

      await LoadImageAsync(detail.Id);
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
        Editor.PurchaseUnitId = result.EntityId;
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

  private static MaterialUpsertRequest CreateNewEditor()
    => new()
    {
      PurchaseQuantity = 1m,
      MaterialClass = "Consumable",
      Status = "ACTIVO",
      IsActive = true
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
}
