using System.IO;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using OrionERP.Application.Features.Logistica.Locations;
using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Logistica.Stock;
using OrionERP.Web.Services;

namespace OrionERP.Web.Features.Logistica.Locations;

public partial class UbicacionesPage : ComponentBase
{
  private const int PageSize = 50;
  private const int QueryTake = PageSize + 1;
  private const string SuiteRoomType = "SUITE";

  protected static readonly string[] LocationTypes = ["Room", "Storage", "Disposal", "Service"];

  [Inject] private ILocationService LocationService { get; set; } = default!;
  [Inject] private IMaterialService MaterialService { get; set; } = default!;
  [Inject] private IStockService StockService { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;
  [Inject] private IJSRuntime Js { get; set; } = default!;

  protected StockFilter StockFilter { get; set; } = new() { IncludeZeroBalances = true };
  protected List<LookupOptionDto> RoomOptions { get; set; } = [];
  protected List<LocationListItemDto> Locations { get; set; } = [];
  protected List<StockListItemDto> StockRows { get; set; } = [];
  protected List<StockTransactionDto> SelectedStockTransactions { get; set; } = [];
  protected List<LocationMaterialAttachmentDto> SelectedStockAttachments { get; set; } = [];
  protected Dictionary<int, string> MaterialThumbnailDataUrls { get; set; } = [];
  protected LocationUpsertRequest LocationEditor { get; set; } = CreateLocationEditor();
  protected StockThresholdUpdateRequest ThresholdEditor { get; set; } = CreateThresholdEditor();
  protected StockListItemDto? SelectedStock { get; set; }
  protected int? SelectedRoomId { get; set; }
  protected int? SelectedLocationId { get; set; }
  protected bool IsLocationListCollapsed { get; set; }
  protected bool HasExecutedStockSearch { get; set; }
  protected bool HasMoreStockRows { get; set; }
  protected bool IsLoadingLocations { get; set; }
  protected bool IsLoadingStock { get; set; }
  protected bool IsLoadingMoreStock { get; set; }
  protected bool IsSavingLocation { get; set; }
  protected bool IsSavingThresholds { get; set; }
  protected bool IsSavingAttachment { get; set; }
  protected bool ShowMaterialImageModal { get; set; }
  protected bool IsLoadingMaterialImage { get; set; }

  protected byte[]? PendingAttachmentBytes { get; set; }
  protected string? PendingAttachmentName { get; set; }
  protected string? PendingAttachmentContentType { get; set; }
  protected string? PendingAttachmentDescription { get; set; }
  protected string? MaterialImageModalTitle { get; set; }
  protected string? MaterialImageModalDataUrl { get; set; }

  protected bool HasPendingAttachment => PendingAttachmentBytes is { Length: > 0 };
  protected bool IsStockBusy => IsLoadingStock || IsLoadingMoreStock;
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
    await LoadLookupsAsync();
  }

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
      SelectedStockAttachments = (await StockService.GetLocationMaterialAttachmentsAsync(item.LocationId, item.MaterialId)).ToList();
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
      var image = await MaterialService.GetMaterialImageAsync(item.MaterialId);
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
      var thumbnails = await MaterialService.GetMaterialThumbnailsAsync(materialIds);
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

  private StockFilter CreateQueryFilter(int skip, int take)
    => new()
    {
      SearchText = StockFilter.SearchText,
      RoomId = StockFilter.RoomId,
      LocationId = StockFilter.LocationId,
      LowStockOnly = StockFilter.LowStockOnly,
      CountDueOnly = StockFilter.CountDueOnly,
      IncludeZeroBalances = StockFilter.IncludeZeroBalances,
      Skip = skip,
      Take = take
    };

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

  private static void UpdateThresholds(StockListItemDto stock, decimal? minQuantity, decimal? maxQuantity, bool isLowStock)
  {
    stock.MinQuantity = minQuantity;
    stock.MaxQuantity = maxQuantity;
    stock.IsLowStock = isLowStock;
  }

  private bool TryGetMaterialThumbnailDataUrl(int materialId, out string dataUrl)
    => MaterialThumbnailDataUrls.TryGetValue(materialId, out dataUrl!);

  protected string? GetMaterialThumbnailDataUrl(int materialId)
    => TryGetMaterialThumbnailDataUrl(materialId, out var dataUrl) ? dataUrl : null;

  protected static string FormatThresholdQuantity(decimal? quantity)
    => quantity.HasValue ? quantity.Value.ToString("N2") : "No definido";

  protected string GetThresholdStatusText()
  {
    if (SelectedStock is null)
    {
      return string.Empty;
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

  protected static string GetLocationInventoryBadgeClass(LocationListItemDto item)
    => item.IsInventoryEnabled ? "text-bg-success" : "text-bg-secondary";

  private static string BuildDataUrl(string? contentType, byte[] bytes)
  {
    var safeContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
    return FormattableString.Invariant($"data:{safeContentType};base64,{Convert.ToBase64String(bytes)}");
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
