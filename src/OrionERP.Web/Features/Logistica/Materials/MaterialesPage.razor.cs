using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Web.Services;
using OrionERP.Web.State;

namespace OrionERP.Web.Features.Logistica.Materials;

public partial class MaterialesPage : ComponentBase, IDisposable
{
  private const int ThumbnailMaxPixels = 240;
  private const int PageSize = 50;
  private const int QueryTake = PageSize + 1;

  [Inject] private IMaterialService MaterialService { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;
  [Inject] private IUserRfcState RfcState { get; set; } = default!;

  protected MaterialFilter Filter { get; set; } = new();
  protected MaterialCatalogDto Catalog { get; set; } = new();
  protected List<MaterialListItemDto> Materials { get; set; } = [];
  protected MaterialUpsertRequest Editor { get; set; } = CreateNewEditor();
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
  protected bool ShowMaterialImageModal { get; set; }
  protected bool IsLoadingMaterialImage { get; set; }
  protected string? MaterialImageModalTitle { get; set; }
  protected string? MaterialImageModalDataUrl { get; set; }
  protected bool IsListBusy => IsBusy || IsLoadingMore;

  protected bool HasImageOnly
  {
    get => Filter.HasImage ?? false;
    set => Filter.HasImage = value ? true : null;
  }

  protected override async Task OnInitializedAsync()
  {
    RfcState.Changed += HandleRfcChanged;
    Editor.Rfc = CurrentRfc;
    await LoadCatalogAsync();
  }

  protected async Task BuscarAsync()
  {
    HasExecutedSearch = true;
    IsBusy = true;
    CloseMaterialImageModal();
    Materials = [];
    MaterialThumbnailDataUrls = [];
    HasMoreMaterials = false;
    try
    {
      var page = await GetMaterialsPageAsync(0);
      Materials = page.Items;
      HasMoreMaterials = page.HasMore;
      await CargarMiniaturasMaterialesAsync(page.Items, append: false);
    }
    catch (Exception ex)
    {
      MaterialThumbnailDataUrls = [];
      UiMessages.ShowError($"No se pudo cargar el catálogo de materiales. {ex.Message}");
    }
    finally
    {
      IsBusy = false;
      StateHasChanged();
    }
  }

  protected async Task CargarMasAsync()
  {
    if (IsListBusy || !HasMoreMaterials)
    {
      return;
    }

    IsLoadingMore = true;
    try
    {
      var page = await GetMaterialsPageAsync(Materials.Count);
      Materials.AddRange(page.Items);
      HasMoreMaterials = page.HasMore;
      await CargarMiniaturasMaterialesAsync(page.Items, append: true);
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
    CloseMaterialImageModal();
    Editor = CreateNewEditor();
    Editor.Rfc = CurrentRfc;
  }

  protected async Task SeleccionarMaterialAsync(int materialId)
  {
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
      if (HasExecutedSearch)
      {
        await BuscarAsync();
      }

      if (result.EntityId.HasValue)
      {
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

    await using var stream = file.OpenReadStream(long.MaxValue);
    using var ms = new MemoryStream();
    await stream.CopyToAsync(ms);
    Editor.PrimaryImageBytes = ms.ToArray();
    Editor.PrimaryImageFileName = file.Name;
    Editor.PrimaryImageContentType = file.ContentType;
    var thumbnail = await BuildThumbnailAsync(file);
    Editor.PrimaryImageThumbnailBytes = thumbnail.Bytes ?? Editor.PrimaryImageBytes;
    Editor.PrimaryImageThumbnailContentType = thumbnail.ContentType ?? Editor.PrimaryImageContentType;
    SelectedImageFileName = file.Name;
    ImagePreviewDataUrl = BuildDataUrl(Editor.PrimaryImageContentType, Editor.PrimaryImageBytes);
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
      if (SelectedMaterialId != item.Id)
      {
        await SeleccionarMaterialAsync(item.Id);
      }

      if (SelectedMaterialId == item.Id && ImagePreviewDataUrl is not null)
      {
        MaterialImageModalDataUrl = ImagePreviewDataUrl;
        return;
      }

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

  private async Task LoadCatalogAsync()
  {
    Catalog = await MaterialService.GetCatalogAsync(CurrentRfc);
    if (Catalog.Units.Count > 0 && Editor.BaseUnitId == 0)
    {
      Editor.BaseUnitId = Catalog.Units[0].Id;
    }
  }

  private async Task LoadImageAsync(int materialId)
  {
    var image = await MaterialService.GetMaterialImageAsync(CurrentRfc, materialId);
    ImagePreviewDataUrl = image is null
      ? null
      : BuildDataUrl(image.ContentType, image.Bytes);
  }

  private async Task<(List<MaterialListItemDto> Items, bool HasMore)> GetMaterialsPageAsync(int skip)
  {
    var rows = (await MaterialService.GetMaterialsAsync(CreateQueryFilter(skip, QueryTake))).ToList();
    var hasMore = rows.Count > PageSize;
    if (hasMore)
    {
      rows = rows.Take(PageSize).ToList();
    }

    return (rows, hasMore);
  }

  private async Task CargarMiniaturasMaterialesAsync(IEnumerable<MaterialListItemDto> materials, bool append)
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
      Skip = skip,
      Take = take
    };

  private bool TryGetMaterialThumbnailDataUrl(int materialId, out string dataUrl)
    => MaterialThumbnailDataUrls.TryGetValue(materialId, out dataUrl!);

  protected string? GetMaterialThumbnailDataUrl(int materialId)
    => TryGetMaterialThumbnailDataUrl(materialId, out var dataUrl) ? dataUrl : null;

  private static MaterialUpsertRequest CreateNewEditor()
    => new()
    {
      PurchaseQuantity = 1m,
      MaterialClass = "Consumable",
      Status = "ACTIVO",
      IsActive = true
    };

  private string CurrentRfc => LogisticsRfc.Require(RfcState.CurrentRfc);

  private void HandleRfcChanged()
    => _ = InvokeAsync(async () =>
    {
      Materials = [];
      MaterialThumbnailDataUrls = [];
      HasExecutedSearch = false;
      SelectedMaterialId = null;
      NuevoMaterial();
      await LoadCatalogAsync();
      StateHasChanged();
    });

  public void Dispose()
    => RfcState.Changed -= HandleRfcChanged;

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
      await using var thumbnailStream = thumbnailFile.OpenReadStream(long.MaxValue);
      using var ms = new MemoryStream();
      await thumbnailStream.CopyToAsync(ms);
      return (ms.ToArray(), thumbnailFile.ContentType);
    }
    catch
    {
      return (null, null);
    }
  }
}
