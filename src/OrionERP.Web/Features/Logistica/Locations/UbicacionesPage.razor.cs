using System.IO;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using OrionERP.Application.Features.Logistica.Locations;
using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Logistica.Stock;
using OrionERP.Web.Services;
using OrionERP.Web.State;

namespace OrionERP.Web.Features.Logistica.Locations;

public partial class UbicacionesPage : ComponentBase, IDisposable
{
  private const int PageSize = 50;
  private const int QueryTake = PageSize + 1;
  private const string SuiteRoomType = "SUITE";

  private ElementReference MaterialPickerSearchInput;
  private bool _focusMaterialPickerSearchPending;

  protected static readonly string[] LocationTypes = ["Room", "Storage", "Disposal", "Service"];

  [Inject] private ILocationService LocationService { get; set; } = default!;
  [Inject] private IMaterialService MaterialService { get; set; } = default!;
  [Inject] private IStockService StockService { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;
  [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
  [Inject] private IJSRuntime Js { get; set; } = default!;
  [Inject] private IUserRfcState RfcState { get; set; } = default!;

  protected StockFilter StockFilter { get; set; } = new() { IncludeZeroBalances = true };
  protected MaterialFilter MaterialPickerFilter { get; set; } = new() { Status = "ACTIVO" };
  protected MaterialCatalogDto MaterialCatalog { get; set; } = new();
  protected List<LookupOptionDto> RoomOptions { get; set; } = [];
  protected List<LocationListItemDto> Locations { get; set; } = [];
  protected List<StockListItemDto> StockRows { get; set; } = [];
  protected List<MaterialListItemDto> MaterialPickerRows { get; set; } = [];
  protected List<StockTransactionDto> SelectedStockTransactions { get; set; } = [];
  protected List<LocationMaterialAttachmentDto> SelectedStockAttachments { get; set; } = [];
  protected Dictionary<int, string> MaterialThumbnailDataUrls { get; set; } = [];
  protected Dictionary<int, string> MaterialPickerThumbnailDataUrls { get; set; } = [];
  protected LocationUpsertRequest LocationEditor { get; set; } = CreateLocationEditor();
  protected StockThresholdUpdateRequest ThresholdEditor { get; set; } = CreateThresholdEditor();
  protected StockListItemDto? SelectedStock { get; set; }
  protected int? SelectedRoomId { get; set; }
  protected int? SelectedLocationId { get; set; }
  protected int? AddingMaterialId { get; set; }
  protected bool IsLocationListCollapsed { get; set; }
  protected bool HasExecutedStockSearch { get; set; }
  protected bool HasExecutedMaterialPickerSearch { get; set; }
  protected bool HasMoreStockRows { get; set; }
  protected bool HasMoreMaterialPickerRows { get; set; }
  protected bool IsLoadingLocations { get; set; }
  protected bool IsLoadingStock { get; set; }
  protected bool IsLoadingMoreStock { get; set; }
  protected bool IsLoadingMaterialPicker { get; set; }
  protected bool IsLoadingMoreMaterialPicker { get; set; }
  protected bool IsSavingLocation { get; set; }
  protected bool IsSavingThresholds { get; set; }
  protected bool IsSavingAttachment { get; set; }
  protected bool IsMutatingStock { get; set; }
  protected bool ShowAddMaterialDialog { get; set; }
  protected bool ShowMaterialImageModal { get; set; }
  protected bool IsLoadingMaterialImage { get; set; }
  protected string CurrentUserName { get; set; } = "Administrador";

  protected byte[]? PendingAttachmentBytes { get; set; }
  protected string? PendingAttachmentName { get; set; }
  protected string? PendingAttachmentContentType { get; set; }
  protected string? PendingAttachmentDescription { get; set; }
  protected string? MaterialImageModalTitle { get; set; }
  protected string? MaterialImageModalDataUrl { get; set; }

  protected bool HasPendingAttachment => PendingAttachmentBytes is { Length: > 0 };
  protected bool IsStockBusy => IsLoadingStock || IsLoadingMoreStock;
  protected bool IsMaterialPickerBusy => IsLoadingMaterialPicker || IsLoadingMoreMaterialPicker || AddingMaterialId.HasValue;
  protected bool CanEditSelectedStock => SelectedStock is { IsRemoved: false };
  protected bool HasImageOnly
  {
    get => MaterialPickerFilter.HasImage ?? false;
    set => MaterialPickerFilter.HasImage = value ? true : null;
  }

  protected LookupOptionDto? SelectedRoom => RoomOptions.FirstOrDefault(room => room.Id == SelectedRoomId);
  protected string SelectedRoomName => SelectedRoom?.Name ?? "Selecciona una suite";
  protected int InventoryEnabledLocationCount => Locations.Count(item => item.IsInventoryEnabled);
  protected int ActiveLocationCount => Locations.Count(item => item.IsActive);
  protected string LocationListToggleLabel => IsLocationListCollapsed ? "Mostrar ubicaciones" : "Ocultar ubicaciones";
  protected IReadOnlyList<LocationListItemDto> ParentLocationOptions => Locations
    .Where(item => item.Id != LocationEditor.Id)
    .OrderBy(item => item.LocationName, StringComparer.OrdinalIgnoreCase)
    .ThenBy(item => item.Id)
    .ToList();
  protected string SelectedLocationSummary
  {
    get
    {
      if (SelectedLocationId.HasValue)
      {
        var selectedLocation = Locations.FirstOrDefault(item => item.Id == SelectedLocationId.Value);
        if (selectedLocation is not null)
        {
          return string.IsNullOrWhiteSpace(selectedLocation.LocationCode)
            ? selectedLocation.LocationName
            : $"{selectedLocation.LocationCode} · {selectedLocation.LocationName}";
        }
      }

      return string.IsNullOrWhiteSpace(LocationEditor.LocationName)
        ? "Selecciona una ubicación"
        : LocationEditor.LocationName;
    }
  }

  protected override async Task OnInitializedAsync()
  {
    RfcState.Changed += HandleRfcChanged;
    CurrentUserName = await ResolveCurrentUserAsync();
    await LoadLookupsAsync();
  }

  protected override async Task OnAfterRenderAsync(bool firstRender)
  {
    if (!_focusMaterialPickerSearchPending || !ShowAddMaterialDialog || !SelectedLocationId.HasValue)
    {
      return;
    }

    _focusMaterialPickerSearchPending = false;
    try
    {
      await Js.InvokeVoidAsync("focusAndSelectTextInput", MaterialPickerSearchInput);
    }
    catch (InvalidOperationException)
    {
    }
    catch (JSDisconnectedException)
    {
    }
  }

  private void HandleRfcChanged() => _ = InvokeAsync(async () =>
  {
    SelectedRoomId = null;
    Locations = [];
    ClearSelectedLocationContext();
    await LoadLookupsAsync();
    StateHasChanged();
  });

  public void Dispose() => RfcState.Changed -= HandleRfcChanged;

  protected async Task OnRoomChangedAsync(ChangeEventArgs args)
  {
    SelectedRoomId = int.TryParse(args.Value?.ToString(), out var roomId) ? roomId : null;
    IsLocationListCollapsed = false;
    await LoadLocationsForSelectedRoomAsync();
  }

  protected async Task BuscarStockAsync()
  {
    if (!SelectedLocationId.HasValue || !StockFilter.LocationId.HasValue)
    {
      ClearStockResults();
      StateHasChanged();
      return;
    }

    HasExecutedStockSearch = true;
    IsLoadingStock = true;
    CloseMaterialImageModal();
    ResetStockSelection();
    StockRows = [];
    MaterialThumbnailDataUrls = [];
    HasMoreStockRows = false;
    try
    {
      var page = await GetStockPageAsync(0);
      StockRows = page.Items;
      HasMoreStockRows = page.HasMore;
      await CargarMiniaturasMaterialesAsync(page.Items, append: false);
    }
    catch (Exception ex)
    {
      MaterialThumbnailDataUrls = [];
      UiMessages.ShowError($"No se pudo cargar el inventario. {ex.Message}");
    }
    finally
    {
      IsLoadingStock = false;
      StateHasChanged();
    }
  }

  protected async Task BuscarMaterialesParaAgregarAsync()
  {
    if (IsMaterialPickerBusy)
    {
      return;
    }

    if (!SelectedLocationId.HasValue)
    {
      ClearMaterialPickerResults();
      StateHasChanged();
      return;
    }

    HasExecutedMaterialPickerSearch = true;
    IsLoadingMaterialPicker = true;
    MaterialPickerRows = [];
    MaterialPickerThumbnailDataUrls = [];
    HasMoreMaterialPickerRows = false;
    try
    {
      var page = await GetMaterialPickerPageAsync(0);
      MaterialPickerRows = page.Items;
      HasMoreMaterialPickerRows = page.HasMore;
      await CargarMiniaturasCatalogoAsync(page.Items, append: false);
    }
    catch (Exception ex)
    {
      MaterialPickerThumbnailDataUrls = [];
      UiMessages.ShowError($"No se pudo cargar el catálogo de materiales. {ex.Message}");
    }
    finally
    {
      IsLoadingMaterialPicker = false;
      RequestMaterialPickerSearchFocus();
      StateHasChanged();
    }
  }

  protected async Task HandleMaterialPickerSearchKeyDownAsync(KeyboardEventArgs args)
  {
    if (string.Equals(args.Key, "Enter", StringComparison.Ordinal))
    {
      await BuscarMaterialesParaAgregarAsync();
    }
  }

  protected async Task AbrirAgregarMaterialDialogAsync()
  {
    if (!SelectedLocationId.HasValue)
    {
      UiMessages.ShowWarning("Selecciona una ubicación antes de agregar materiales.");
      return;
    }

    ShowAddMaterialDialog = true;
    RequestMaterialPickerSearchFocus();

    if (!HasExecutedMaterialPickerSearch && !IsLoadingMaterialPicker)
    {
      await BuscarMaterialesParaAgregarAsync();
    }
  }

  protected void CerrarAgregarMaterialDialog()
  {
    ShowAddMaterialDialog = false;
    _focusMaterialPickerSearchPending = false;
  }

  protected async Task CargarMasMaterialesParaAgregarAsync()
  {
    if (IsMaterialPickerBusy || !HasMoreMaterialPickerRows)
    {
      return;
    }

    IsLoadingMoreMaterialPicker = true;
    try
    {
      var page = await GetMaterialPickerPageAsync(MaterialPickerRows.Count);
      MaterialPickerRows.AddRange(page.Items);
      HasMoreMaterialPickerRows = page.HasMore;
      await CargarMiniaturasCatalogoAsync(page.Items, append: true);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudieron cargar más materiales. {ex.Message}");
    }
    finally
    {
      IsLoadingMoreMaterialPicker = false;
      StateHasChanged();
    }
  }

  protected async Task AgregarMaterialAUbicacionAsync(MaterialListItemDto item)
  {
    if (!SelectedLocationId.HasValue || AddingMaterialId.HasValue)
    {
      return;
    }

    AddingMaterialId = item.Id;
    try
    {
      var result = await StockService.AddMaterialToLocationAsync(new LocationMaterialAddRequest
      {
        LocationId = SelectedLocationId.Value,
        MaterialId = item.Id,
        AddedBy = CurrentUserName
      });

      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      IncrementSelectedLocationMaterialCount();
      ResetStockFilterForAddedMaterial();
      await RefrescarStockAsync(result.EntityId);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo agregar el material a la ubicación. {ex.Message}");
    }
    finally
    {
      AddingMaterialId = null;
      RequestMaterialPickerSearchFocus();
      StateHasChanged();
    }
  }

  protected async Task CargarMasStockAsync()
  {
    if (IsStockBusy || !HasMoreStockRows)
    {
      return;
    }

    IsLoadingMoreStock = true;
    try
    {
      var page = await GetStockPageAsync(StockRows.Count);
      StockRows.AddRange(page.Items);
      HasMoreStockRows = page.HasMore;
      await CargarMiniaturasMaterialesAsync(page.Items, append: true);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudieron cargar más registros de inventario. {ex.Message}");
    }
    finally
    {
      IsLoadingMoreStock = false;
      StateHasChanged();
    }
  }

  protected async Task SeleccionarUbicacionAsync(int locationId)
  {
    try
    {
      var detail = await LocationService.GetLocationAsync(locationId);
      if (detail is null)
      {
        UiMessages.ShowWarning("La ubicación ya no existe.");
        return;
      }

      if (detail.RoomId.HasValue && detail.RoomId != SelectedRoomId)
      {
        SelectedRoomId = detail.RoomId;
      }

      SelectedLocationId = detail.Id;
      LocationEditor = new LocationUpsertRequest
      {
        Id = detail.Id,
        LocationCode = detail.LocationCode,
        LocationName = detail.LocationName,
        LocationType = detail.LocationType,
        ParentLocationId = detail.ParentLocationId,
        RoomId = detail.RoomId,
        Description = detail.Description,
        IsInventoryEnabled = detail.IsInventoryEnabled,
        IsActive = detail.IsActive
      };

      StockFilter.RoomId = detail.RoomId ?? SelectedRoomId;
      StockFilter.LocationId = detail.Id;
      IsLocationListCollapsed = true;
      await BuscarStockAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cargar la ubicación. {ex.Message}");
    }
  }

  protected void NuevaUbicacion()
  {
    if (!SelectedRoomId.HasValue)
    {
      UiMessages.ShowWarning("Selecciona una suite antes de crear una ubicación.");
      return;
    }

    ClearSelectedLocationContext();
    LocationEditor = CreateLocationEditor(SelectedRoomId);
    IsLocationListCollapsed = true;
  }

  protected async Task GuardarUbicacionAsync()
  {
    if (!SelectedRoomId.HasValue)
    {
      UiMessages.ShowWarning("Selecciona una suite antes de guardar la ubicación.");
      return;
    }

    IsSavingLocation = true;
    try
    {
      LocationEditor.RoomId = SelectedRoomId;
      var result = await LocationService.SaveLocationAsync(LocationEditor);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      await LoadLocationsForSelectedRoomAsync(result.EntityId);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo guardar la ubicación. {ex.Message}");
    }
    finally
    {
      IsSavingLocation = false;
    }
  }

  protected async Task RestablecerUbicacionAsync()
  {
    if (SelectedLocationId.HasValue)
    {
      await SeleccionarUbicacionAsync(SelectedLocationId.Value);
      return;
    }

    LocationEditor = CreateLocationEditor(SelectedRoomId);
  }

  protected void ToggleLocationList()
  {
    if (Locations.Count == 0)
    {
      return;
    }

    IsLocationListCollapsed = !IsLocationListCollapsed;
  }

  protected async Task SeleccionarStockAsync(StockListItemDto item)
  {
    SelectedStock = item;
    ThresholdEditor = CreateThresholdEditor(item);
    PendingAttachmentBytes = null;
    PendingAttachmentName = null;
    PendingAttachmentContentType = null;
    PendingAttachmentDescription = null;

    try
    {
      SelectedStockTransactions = (await StockService.GetStockTransactionsAsync(item.StockBalanceId)).ToList();
      SelectedStockAttachments = (await StockService.GetLocationMaterialAttachmentsAsync(item.LocationId, item.MaterialId, includeDeleted: item.IsRemoved)).ToList();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cargar el detalle de inventario. {ex.Message}");
    }
  }

  protected async Task GuardarThresholdsAsync()
  {
    if (SelectedStock is null)
    {
      return;
    }

    if (SelectedStock.IsRemoved)
    {
      UiMessages.ShowWarning("Reactiva el material antes de modificar sus parámetros.");
      return;
    }

    IsSavingThresholds = true;
    try
    {
      ThresholdEditor.StockBalanceId = SelectedStock.StockBalanceId;
      var result = await StockService.SaveStockThresholdsAsync(ThresholdEditor);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      ApplyThresholdsToSelection(ThresholdEditor.MinQuantity, ThresholdEditor.MaxQuantity);
      UiMessages.ShowSuccess(result.Message);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudieron guardar los parámetros de inventario. {ex.Message}");
    }
    finally
    {
      IsSavingThresholds = false;
    }
  }

  protected void RestablecerThresholdEditor()
  {
    ThresholdEditor = SelectedStock is null ? CreateThresholdEditor() : CreateThresholdEditor(SelectedStock);
  }

  protected async Task OnAttachmentSelectedAsync(InputFileChangeEventArgs args)
  {
    var file = args.File;
    if (file is null)
    {
      return;
    }

    await using var stream = file.OpenReadStream(long.MaxValue);
    using var ms = new MemoryStream();
    await stream.CopyToAsync(ms);
    PendingAttachmentBytes = ms.ToArray();
    PendingAttachmentName = file.Name;
    PendingAttachmentContentType = file.ContentType;
  }

  protected async Task GuardarAdjuntoAsync()
  {
    if (SelectedStock is null || PendingAttachmentBytes is not { Length: > 0 } || string.IsNullOrWhiteSpace(PendingAttachmentName))
    {
      return;
    }

    if (SelectedStock.IsRemoved)
    {
      UiMessages.ShowWarning("Reactiva el material antes de guardar adjuntos.");
      return;
    }

    IsSavingAttachment = true;
    try
    {
      var result = await StockService.SaveLocationMaterialAttachmentAsync(new LocationMaterialAttachmentCreateRequest
      {
        LocationId = SelectedStock.LocationId,
        MaterialId = SelectedStock.MaterialId,
        FileName = PendingAttachmentName,
        FileExtension = Path.GetExtension(PendingAttachmentName).TrimStart('.'),
        ContentType = PendingAttachmentContentType,
        Description = PendingAttachmentDescription,
        Bytes = PendingAttachmentBytes
      });

      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      await SeleccionarStockAsync(SelectedStock);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo guardar el adjunto. {ex.Message}");
    }
    finally
    {
      IsSavingAttachment = false;
    }
  }

  protected async Task QuitarMaterialAsync()
  {
    if (SelectedStock is null || IsMutatingStock)
    {
      return;
    }

    if (SelectedStock.IsRemoved)
    {
      UiMessages.ShowWarning("El material ya está eliminado de esta ubicación.");
      return;
    }

    if (SelectedStock.Quantity != 0)
    {
      UiMessages.ShowWarning("Solo puedes quitar materiales con cantidad 0. Ajusta el inventario antes de eliminarlo.");
      return;
    }

    var confirmed = await ConfirmAsync("¿Estás seguro que deseas quitar este material de la ubicación? Sus adjuntos se archivarán y podrás reactivarlo después.");
    if (!confirmed)
    {
      return;
    }

    IsMutatingStock = true;
    var stockBalanceId = SelectedStock.StockBalanceId;

    try
    {
      var result = await StockService.RemoveLocationMaterialAsync(stockBalanceId, CurrentUserName);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      await RefrescarStockAsync(StockFilter.IncludeRemoved ? stockBalanceId : null);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo quitar el material. {ex.Message}");
    }
    finally
    {
      IsMutatingStock = false;
      StateHasChanged();
    }
  }

  protected async Task ReactivarMaterialAsync()
  {
    if (SelectedStock is null || IsMutatingStock)
    {
      return;
    }

    if (!SelectedStock.IsRemoved)
    {
      UiMessages.ShowWarning("El material ya está activo en esta ubicación.");
      return;
    }

    var confirmed = await ConfirmAsync("¿Deseas reactivar este material y restaurar sus adjuntos archivados?");
    if (!confirmed)
    {
      return;
    }

    IsMutatingStock = true;
    var stockBalanceId = SelectedStock.StockBalanceId;

    try
    {
      var result = await StockService.ReactivateLocationMaterialAsync(stockBalanceId, CurrentUserName);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      await RefrescarStockAsync(stockBalanceId);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo reactivar el material. {ex.Message}");
    }
    finally
    {
      IsMutatingStock = false;
      StateHasChanged();
    }
  }

  protected async Task DescargarAdjuntoAsync(LocationMaterialAttachmentDto attachment)
  {
    try
    {
      var content = await StockService.GetLocationMaterialAttachmentContentAsync(attachment.Id);
      if (content is null)
      {
        UiMessages.ShowWarning("No se encontró el adjunto solicitado.");
        return;
      }

      var dataUrl = FormattableString.Invariant($"data:{content.ContentType};base64,{Convert.ToBase64String(content.Bytes)}");
      await Js.InvokeVoidAsync("triggerFileDownload", content.FileName, dataUrl);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo descargar el adjunto. {ex.Message}");
    }
  }

  protected async Task AbrirImagenMaterialAsync(StockListItemDto item)
  {
    if (SelectedStock?.StockBalanceId != item.StockBalanceId)
    {
      await SeleccionarStockAsync(item);
    }

    ShowMaterialImageModal = true;
    IsLoadingMaterialImage = true;
    MaterialImageModalTitle = string.IsNullOrWhiteSpace(item.MaterialDescription)
      ? item.MaterialCode
      : string.IsNullOrWhiteSpace(item.MaterialCode)
        ? item.MaterialDescription
        : $"{item.MaterialDescription} · {item.MaterialCode}";
    MaterialImageModalDataUrl = TryGetMaterialThumbnailDataUrl(item.MaterialId, out var thumbnailDataUrl)
      ? thumbnailDataUrl
      : null;

    try
    {
      var image = await MaterialService.GetMaterialImageAsync(CurrentRfc, item.MaterialId);
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

  protected async Task AbrirImagenMaterialAsync(MaterialListItemDto item)
  {
    ShowMaterialImageModal = true;
    IsLoadingMaterialImage = true;
    MaterialImageModalTitle = string.IsNullOrWhiteSpace(item.Description)
      ? item.MaterialCode
      : string.IsNullOrWhiteSpace(item.MaterialCode)
        ? item.Description
        : $"{item.Description} · {item.MaterialCode}";
    MaterialImageModalDataUrl = TryGetMaterialPickerThumbnailDataUrl(item.Id, out var thumbnailDataUrl)
      ? thumbnailDataUrl
      : null;

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

  private async Task LoadLookupsAsync()
  {
    RoomOptions = (await LocationService.GetRoomLookupAsync(roomType: SuiteRoomType)).ToList();
    MaterialCatalog = await MaterialService.GetCatalogAsync(CurrentRfc);
  }

  private async Task LoadLocationsForSelectedRoomAsync(int? preferredLocationId = null)
  {
    IsLoadingLocations = true;
    try
    {
      StockFilter.RoomId = SelectedRoomId;
      ClearSelectedLocationContext();
      LocationEditor = CreateLocationEditor(SelectedRoomId);
      Locations = [];
      IsLocationListCollapsed = false;

      if (!SelectedRoomId.HasValue)
      {
        return;
      }

      Locations = (await LocationService.GetLocationsAsync(new LocationFilter
      {
        RoomId = SelectedRoomId,
        IncludeInactive = true
      })).ToList();

      if (preferredLocationId.HasValue && Locations.Any(item => item.Id == preferredLocationId.Value))
      {
        await SeleccionarUbicacionAsync(preferredLocationId.Value);
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudieron cargar las ubicaciones. {ex.Message}");
    }
    finally
    {
      IsLoadingLocations = false;
      StateHasChanged();
    }
  }

  private async Task<(List<StockListItemDto> Items, bool HasMore)> GetStockPageAsync(int skip)
  {
    var rows = (await StockService.GetStockAsync(CreateQueryFilter(skip, QueryTake))).ToList();
    var hasMore = rows.Count > PageSize;
    if (hasMore)
    {
      rows = rows.Take(PageSize).ToList();
    }

    return (rows, hasMore);
  }

  private async Task<(List<MaterialListItemDto> Items, bool HasMore)> GetMaterialPickerPageAsync(int skip)
  {
    var rows = (await MaterialService.GetMaterialsAsync(CreateMaterialPickerQueryFilter(skip, QueryTake))).ToList();
    var hasMore = rows.Count > PageSize;
    if (hasMore)
    {
      rows = rows.Take(PageSize).ToList();
    }

    return (rows, hasMore);
  }

  private async Task CargarMiniaturasMaterialesAsync(IEnumerable<StockListItemDto> stockRows, bool append)
  {
    if (!append)
    {
      MaterialThumbnailDataUrls = [];
    }

    var materialIds = stockRows
      .Select(item => item.MaterialId)
      .Distinct()
      .Where(materialId => !append || !MaterialThumbnailDataUrls.ContainsKey(materialId))
      .ToList();

    if (materialIds.Count == 0)
    {
      return;
    }

    try
    {
      var thumbnails = await MaterialService.GetMaterialThumbnailsAsync(CurrentRfc, materialIds);
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
    catch (Exception ex)
    {
      if (!append)
      {
        MaterialThumbnailDataUrls = [];
      }

      UiMessages.ShowWarning($"No se pudieron cargar las miniaturas de los materiales. {ex.Message}");
    }
  }

  private async Task CargarMiniaturasCatalogoAsync(IEnumerable<MaterialListItemDto> materials, bool append)
  {
    if (!append)
    {
      MaterialPickerThumbnailDataUrls = [];
    }

    var materialIds = materials
      .Where(material => material.HasImage)
      .Select(material => material.Id)
      .Distinct()
      .Where(materialId => !append || !MaterialPickerThumbnailDataUrls.ContainsKey(materialId))
      .ToList();

    if (materialIds.Count == 0)
    {
      return;
    }

    try
    {
      var thumbnails = await MaterialService.GetMaterialThumbnailsAsync(CurrentRfc, materialIds);
      var thumbnailDataUrls = thumbnails
        .Where(thumbnail => thumbnail.Bytes.Length > 0)
        .ToDictionary(
          thumbnail => thumbnail.Id,
          thumbnail => BuildDataUrl(thumbnail.ContentType, thumbnail.Bytes));

      if (append)
      {
        foreach (var item in thumbnailDataUrls)
        {
          MaterialPickerThumbnailDataUrls[item.Key] = item.Value;
        }
      }
      else
      {
        MaterialPickerThumbnailDataUrls = thumbnailDataUrls;
      }
    }
    catch (Exception ex)
    {
      if (!append)
      {
        MaterialPickerThumbnailDataUrls = [];
      }

      UiMessages.ShowWarning($"No se pudieron cargar las miniaturas del catálogo. {ex.Message}");
    }
  }

  private StockFilter CreateQueryFilter(int skip, int take)
    => new()
    {
      SearchText = StockFilter.SearchText,
      RoomId = StockFilter.RoomId,
      LocationId = StockFilter.LocationId,
      LowStockOnly = StockFilter.LowStockOnly,
      CountDueOnly = StockFilter.CountDueOnly,
      IncludeZeroBalances = StockFilter.IncludeZeroBalances,
      IncludeRemoved = StockFilter.IncludeRemoved,
      Skip = skip,
      Take = take
    };

  private MaterialFilter CreateMaterialPickerQueryFilter(int skip, int take)
    => new()
    {
      Rfc = CurrentRfc,
      SearchText = MaterialPickerFilter.SearchText,
      CategoryId = MaterialPickerFilter.CategoryId,
      VendorId = MaterialPickerFilter.VendorId,
      MaterialClass = MaterialPickerFilter.MaterialClass,
      Status = MaterialPickerFilter.Status,
      HasImage = MaterialPickerFilter.HasImage,
      Skip = skip,
      Take = take
    };

  private string CurrentRfc => LogisticsRfc.Require(RfcState.CurrentRfc);

  private void ApplyThresholdsToSelection(decimal? minQuantity, decimal? maxQuantity)
  {
    if (SelectedStock is null)
    {
      ThresholdEditor = CreateThresholdEditor();
      return;
    }

    var isLowStock = minQuantity.HasValue && SelectedStock.Quantity <= minQuantity.Value;
    UpdateThresholds(SelectedStock, minQuantity, maxQuantity, isLowStock);

    var stockRow = StockRows.FirstOrDefault(item => item.StockBalanceId == SelectedStock.StockBalanceId);
    if (stockRow is not null && !ReferenceEquals(stockRow, SelectedStock))
    {
      UpdateThresholds(stockRow, minQuantity, maxQuantity, isLowStock);
    }

    if (StockFilter.LowStockOnly && !isLowStock)
    {
      StockRows.RemoveAll(item => item.StockBalanceId == SelectedStock.StockBalanceId);
      ResetStockSelection();
      return;
    }

    ThresholdEditor = CreateThresholdEditor(SelectedStock);
  }

  private void ResetStockSelection()
  {
    SelectedStock = null;
    SelectedStockTransactions = [];
    SelectedStockAttachments = [];
    ThresholdEditor = CreateThresholdEditor();
    PendingAttachmentBytes = null;
    PendingAttachmentName = null;
    PendingAttachmentContentType = null;
    PendingAttachmentDescription = null;
  }

  private void ClearSelectedLocationContext()
  {
    SelectedLocationId = null;
    StockFilter.LocationId = null;
    ClearStockResults();
    ClearMaterialPickerResults();
  }

  private void ClearStockResults()
  {
    CloseMaterialImageModal();
    HasExecutedStockSearch = false;
    HasMoreStockRows = false;
    StockRows = [];
    MaterialThumbnailDataUrls = [];
    ResetStockSelection();
  }

  private void ClearMaterialPickerResults()
  {
    ShowAddMaterialDialog = false;
    _focusMaterialPickerSearchPending = false;
    HasExecutedMaterialPickerSearch = false;
    HasMoreMaterialPickerRows = false;
    IsLoadingMaterialPicker = false;
    IsLoadingMoreMaterialPicker = false;
    AddingMaterialId = null;
    MaterialPickerRows = [];
    MaterialPickerThumbnailDataUrls = [];
  }

  private void RequestMaterialPickerSearchFocus()
  {
    if (ShowAddMaterialDialog)
    {
      _focusMaterialPickerSearchPending = true;
    }
  }

  private void ResetStockFilterForAddedMaterial()
  {
    StockFilter.SearchText = null;
    StockFilter.LowStockOnly = false;
    StockFilter.CountDueOnly = false;
    StockFilter.IncludeZeroBalances = true;
    StockFilter.IncludeRemoved = false;
  }

  private void IncrementSelectedLocationMaterialCount()
  {
    if (!SelectedLocationId.HasValue)
    {
      return;
    }

    var selectedLocation = Locations.FirstOrDefault(item => item.Id == SelectedLocationId.Value);
    if (selectedLocation is not null)
    {
      selectedLocation.MaterialCount++;
    }
  }

  private static void UpdateThresholds(StockListItemDto stock, decimal? minQuantity, decimal? maxQuantity, bool isLowStock)
  {
    stock.MinQuantity = minQuantity;
    stock.MaxQuantity = maxQuantity;
    stock.IsLowStock = isLowStock;
  }

  private bool TryGetMaterialThumbnailDataUrl(int materialId, out string dataUrl)
    => MaterialThumbnailDataUrls.TryGetValue(materialId, out dataUrl!);

  private bool TryGetMaterialPickerThumbnailDataUrl(int materialId, out string dataUrl)
    => MaterialPickerThumbnailDataUrls.TryGetValue(materialId, out dataUrl!);

  protected string? GetMaterialThumbnailDataUrl(int materialId)
    => TryGetMaterialThumbnailDataUrl(materialId, out var dataUrl) ? dataUrl : null;

  protected string? GetMaterialPickerThumbnailDataUrl(int materialId)
    => TryGetMaterialPickerThumbnailDataUrl(materialId, out var dataUrl) ? dataUrl : null;

  protected bool IsMaterialAlreadyVisibleInSelectedLocation(int materialId)
    => SelectedLocationId.HasValue
      && StockRows.Any(item => item.LocationId == SelectedLocationId.Value && item.MaterialId == materialId && !item.IsRemoved);

  protected static string FormatThresholdQuantity(decimal? quantity)
    => quantity.HasValue ? quantity.Value.ToString("N2") : "No definido";

  protected string GetThresholdStatusText()
  {
    if (SelectedStock is null)
    {
      return string.Empty;
    }

    if (SelectedStock.IsRemoved)
    {
      return "Eliminado";
    }

    if (ThresholdEditor.MinQuantity.HasValue && SelectedStock.Quantity <= ThresholdEditor.MinQuantity.Value)
    {
      return "Bajo mínimo";
    }

    if (ThresholdEditor.MaxQuantity.HasValue && SelectedStock.Quantity > ThresholdEditor.MaxQuantity.Value)
    {
      return "Sobre máximo";
    }

    return ThresholdEditor.MinQuantity.HasValue || ThresholdEditor.MaxQuantity.HasValue
      ? "Dentro de parámetro"
      : "Sin parámetros";
  }

  protected string GetLocationRowClass(LocationListItemDto item)
  {
    var classes = new List<string> { "ubicaciones-location-row" };
    if (item.Id == SelectedLocationId)
    {
      classes.Add("is-selected");
    }

    if (!item.IsActive)
    {
      classes.Add("is-inactive");
    }

    return string.Join(' ', classes);
  }

  protected string GetStockRowClass(StockListItemDto item)
  {
    var classes = new List<string>();
    if (SelectedStock?.StockBalanceId == item.StockBalanceId)
    {
      classes.Add("table-primary");
    }

    if (item.IsRemoved)
    {
      classes.Add("table-secondary");
    }

    return string.Join(' ', classes);
  }

  protected string GetSelectedStockRemovalSummary()
  {
    if (SelectedStock is not { IsRemoved: true })
    {
      return string.Empty;
    }

    var removedAtText = SelectedStock.RemovedAt.HasValue
      ? SelectedStock.RemovedAt.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
      : "sin fecha registrada";

    return string.IsNullOrWhiteSpace(SelectedStock.RemovedBy)
      ? $"Eliminado el {removedAtText}."
      : $"Eliminado por {SelectedStock.RemovedBy} el {removedAtText}.";
  }

  protected string GetAttachmentArchiveSummary(LocationMaterialAttachmentDto attachment)
  {
    if (!attachment.IsDeleted)
    {
      return string.Empty;
    }

    var deletedAtText = attachment.DeletedAt.HasValue
      ? attachment.DeletedAt.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
      : "sin fecha registrada";

    return string.IsNullOrWhiteSpace(attachment.DeletedBy)
      ? $"Archivado el {deletedAtText}."
      : $"Archivado por {attachment.DeletedBy} el {deletedAtText}.";
  }

  protected static string GetLocationInventoryBadgeClass(LocationListItemDto item)
    => item.IsInventoryEnabled ? "text-bg-success" : "text-bg-secondary";

  private static string BuildDataUrl(string? contentType, byte[] bytes)
  {
    var safeContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
    return FormattableString.Invariant($"data:{safeContentType};base64,{Convert.ToBase64String(bytes)}");
  }

  private async Task RefrescarStockAsync(int? stockBalanceIdToReselect = null)
  {
    await BuscarStockAsync();

    if (!stockBalanceIdToReselect.HasValue)
    {
      return;
    }

    var refreshedRow = StockRows.FirstOrDefault(item => item.StockBalanceId == stockBalanceIdToReselect.Value);
    if (refreshedRow is not null)
    {
      await SeleccionarStockAsync(refreshedRow);
    }
  }

  private async Task<bool> ConfirmAsync(string message)
  {
    try
    {
      return await Js.InvokeAsync<bool>("confirm", message);
    }
    catch
    {
      return true;
    }
  }

  private async Task<string> ResolveCurrentUserAsync()
  {
    var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
    return authState.User.Identity?.Name?.Trim() switch
    {
      { Length: > 0 } name => name,
      _ => "Administrador"
    };
  }

  private static StockThresholdUpdateRequest CreateThresholdEditor(StockListItemDto? item = null)
    => new()
    {
      StockBalanceId = item?.StockBalanceId ?? 0,
      MinQuantity = item?.MinQuantity,
      MaxQuantity = item?.MaxQuantity
    };

  private static LocationUpsertRequest CreateLocationEditor(int? roomId = null)
    => new()
    {
      LocationType = "Storage",
      RoomId = roomId,
      IsInventoryEnabled = true,
      IsActive = true
    };
}
