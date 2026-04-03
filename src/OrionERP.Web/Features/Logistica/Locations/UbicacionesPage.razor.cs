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
  protected static readonly string[] LocationTypes = ["Room", "Storage", "Disposal", "Service"];

  [Inject] private ILocationService LocationService { get; set; } = default!;
  [Inject] private IMaterialService MaterialService { get; set; } = default!;
  [Inject] private IStockService StockService { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;
  [Inject] private IJSRuntime Js { get; set; } = default!;

  protected LocationFilter LocationFilter { get; set; } = new();
  protected StockFilter StockFilter { get; set; } = new() { IncludeZeroBalances = true };
  protected List<LookupOptionDto> RoomOptions { get; set; } = [];
  protected List<LookupOptionDto> LocationOptions { get; set; } = [];
  protected List<LookupOptionDto> InventoryLocationOptions { get; set; } = [];
  protected List<LocationTreeNodeDto> LocationTree { get; set; } = [];
  protected List<LocationListItemDto> Locations { get; set; } = [];
  protected List<StockListItemDto> StockRows { get; set; } = [];
  protected List<StockTransactionDto> SelectedStockTransactions { get; set; } = [];
  protected List<LocationMaterialAttachmentDto> SelectedStockAttachments { get; set; } = [];
  protected Dictionary<int, string> MaterialThumbnailDataUrls { get; set; } = [];
  protected LocationUpsertRequest LocationEditor { get; set; } = CreateLocationEditor();
  protected StockListItemDto? SelectedStock { get; set; }
  protected int? SelectedLocationId { get; set; }
  protected bool IsLoadingLocations { get; set; }
  protected bool IsLoadingStock { get; set; }
  protected bool IsSavingLocation { get; set; }
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

  protected override async Task OnInitializedAsync()
  {
    await LoadLookupsAsync();
    await BuscarUbicacionesAsync();
    await BuscarStockAsync();
  }

  protected async Task BuscarUbicacionesAsync()
  {
    IsLoadingLocations = true;
    try
    {
      LocationTree = (await LocationService.GetLocationTreeAsync()).ToList();
      Locations = (await LocationService.GetLocationsAsync(LocationFilter)).ToList();
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

  protected async Task BuscarStockAsync()
  {
    IsLoadingStock = true;
    CloseMaterialImageModal();
    try
    {
      StockRows = (await StockService.GetStockAsync(StockFilter)).ToList();
      await CargarMiniaturasMaterialesAsync();
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

      StockFilter.LocationId = detail.Id;
      await BuscarStockAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cargar la ubicación. {ex.Message}");
    }
  }

  protected void NuevaUbicacion()
  {
    SelectedLocationId = null;
    LocationEditor = CreateLocationEditor();
  }

  protected async Task GuardarUbicacionAsync()
  {
    IsSavingLocation = true;
    try
    {
      var result = await LocationService.SaveLocationAsync(LocationEditor);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      await LoadLookupsAsync();
      await BuscarUbicacionesAsync();

      if (result.EntityId.HasValue)
      {
        await SeleccionarUbicacionAsync(result.EntityId.Value);
      }
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

  protected async Task SeleccionarStockAsync(StockListItemDto item)
  {
    SelectedStock = item;
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
    RoomOptions = (await LocationService.GetRoomLookupAsync()).ToList();
    LocationOptions = (await LocationService.GetLocationLookupAsync()).ToList();
    InventoryLocationOptions = (await LocationService.GetLocationLookupAsync(inventoryOnly: true)).ToList();
  }

  private async Task CargarMiniaturasMaterialesAsync()
  {
    if (StockRows.Count == 0)
    {
      MaterialThumbnailDataUrls = [];
      return;
    }

    try
    {
      var thumbnails = await MaterialService.GetMaterialThumbnailsAsync(StockRows.Select(item => item.MaterialId).Distinct());
      MaterialThumbnailDataUrls = thumbnails
        .Where(thumbnail => thumbnail.Bytes.Length > 0)
        .ToDictionary(
          thumbnail => thumbnail.Id,
          thumbnail => BuildDataUrl(thumbnail.ContentType, thumbnail.Bytes));
    }
    catch (Exception ex)
    {
      MaterialThumbnailDataUrls = [];
      UiMessages.ShowWarning($"No se pudieron cargar las miniaturas de los materiales. {ex.Message}");
    }
  }

  private bool TryGetMaterialThumbnailDataUrl(int materialId, out string dataUrl)
    => MaterialThumbnailDataUrls.TryGetValue(materialId, out dataUrl!);

  protected string? GetMaterialThumbnailDataUrl(int materialId)
    => TryGetMaterialThumbnailDataUrl(materialId, out var dataUrl) ? dataUrl : null;

  private static string BuildDataUrl(string? contentType, byte[] bytes)
  {
    var safeContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
    return FormattableString.Invariant($"data:{safeContentType};base64,{Convert.ToBase64String(bytes)}");
  }

  private static LocationUpsertRequest CreateLocationEditor()
    => new()
    {
      LocationType = "Storage",
      IsInventoryEnabled = true,
      IsActive = true
    };
}
