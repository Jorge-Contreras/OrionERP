using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Web.Services;

namespace OrionERP.Web.Features.Logistica.Materials;

public partial class MaterialesPage : ComponentBase
{
  private const int ThumbnailMaxPixels = 240;

  [Inject] private IMaterialService MaterialService { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;

  protected MaterialFilter Filter { get; set; } = new();
  protected MaterialCatalogDto Catalog { get; set; } = new();
  protected List<MaterialListItemDto> Materials { get; set; } = [];
  protected MaterialUpsertRequest Editor { get; set; } = CreateNewEditor();
  protected int? SelectedMaterialId { get; set; }
  protected string? CurrentMaterialCode { get; set; }
  protected string? ImagePreviewDataUrl { get; set; }
  protected string? SelectedImageFileName { get; set; }
  protected bool IsBusy { get; set; }
  protected bool IsSaving { get; set; }

  protected bool HasImageOnly
  {
    get => Filter.HasImage ?? false;
    set => Filter.HasImage = value ? true : null;
  }

  protected override async Task OnInitializedAsync()
  {
    await LoadCatalogAsync();
    await BuscarAsync();
  }

  protected async Task BuscarAsync()
  {
    IsBusy = true;
    try
    {
      Materials = (await MaterialService.GetMaterialsAsync(Filter)).ToList();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cargar el catálogo de materiales. {ex.Message}");
    }
    finally
    {
      IsBusy = false;
      StateHasChanged();
    }
  }

  protected void NuevoMaterial()
  {
    SelectedMaterialId = null;
    CurrentMaterialCode = null;
    SelectedImageFileName = null;
    ImagePreviewDataUrl = null;
    Editor = CreateNewEditor();
  }

  protected async Task SeleccionarMaterialAsync(int materialId)
  {
    try
    {
      var detail = await MaterialService.GetMaterialAsync(materialId);
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

  private async Task LoadCatalogAsync()
  {
    Catalog = await MaterialService.GetCatalogAsync();
    if (Catalog.Units.Count > 0 && Editor.BaseUnitId == 0)
    {
      Editor.BaseUnitId = Catalog.Units[0].Id;
    }
  }

  private async Task LoadImageAsync(int materialId)
  {
    var image = await MaterialService.GetMaterialImageAsync(materialId);
    ImagePreviewDataUrl = image is null
      ? null
      : BuildDataUrl(image.ContentType, image.Bytes);
  }

  private static MaterialUpsertRequest CreateNewEditor()
    => new()
    {
      PurchaseQuantity = 1m,
      MaterialClass = "Consumable",
      Status = "ACTIVO",
      IsActive = true
    };

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
