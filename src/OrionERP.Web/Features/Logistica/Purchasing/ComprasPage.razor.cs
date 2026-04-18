using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using System.Globalization;
using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Application.Features.Logistica.Purchasing;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Web.Services;

namespace OrionERP.Web.Features.Logistica.Purchasing;

public partial class ComprasPage : ComponentBase
{
  private const int MaterialSearchTake = 25;

  [Inject] private IPurchaseOrderService PurchaseOrderService { get; set; } = default!;
  [Inject] private IMaterialService MaterialService { get; set; } = default!;
  [Inject] private IPurchaseMaterialThumbnailHydrator ThumbnailHydrator { get; set; } = default!;
  [Inject] private IPurchaseOrderPdfService PurchaseOrderPdfService { get; set; } = default!;
  [Inject] private IPurchaseOrderPdfDocumentFactory PurchaseOrderPdfDocumentFactory { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;
  [Inject] private IJSRuntime Js { get; set; } = default!;
  [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;

  protected PurchaseOrderFilter Filter { get; set; } = new() { OpenOnly = true };
  protected PurchaseOrderCatalogDto Catalog { get; set; } = new();
  protected List<PurchaseOrderListItemDto> Orders { get; set; } = [];
  protected PurchaseOrderUpsertRequest Editor { get; set; } = CreateEditor();
  protected AutoPurchaseOrderCreateRequest AutoPoRequest { get; set; } = CreateAutoPoRequest();
  protected List<EditablePurchaseLine> Lines { get; set; } = [];
  protected PurchaseOrderDetailDto? SelectedPurchaseOrder { get; set; }
  protected EditablePurchaseLine? SelectedLine { get; set; }
  protected List<MaterialListItemDto> MaterialSearchResults { get; set; } = [];
  protected List<ReceiveAllocationInput> ReceiveItems { get; set; } = [];
  protected Dictionary<int, string> MaterialThumbnailDataUrls { get; set; } = [];
  protected string MaterialSearchText { get; set; } = string.Empty;
  protected string? ReceiptNotes { get; set; }
  protected DateTime ReceiptDate { get; set; } = DateTime.Today;
  protected int PendingAllocationLocationId { get; set; }
  protected decimal PendingAllocationQuantity { get; set; } = 1m;
  protected string CurrentUserName { get; set; } = "OrionERP";
  protected bool HasExecutedMaterialSearch { get; set; }
  protected bool IsLoadingOrders { get; set; }
  protected bool IsLoadingOrder { get; set; }
  protected bool IsSavingDraft { get; set; }
  protected bool IsIssuing { get; set; }
  protected bool IsSearchingMaterials { get; set; }
  protected bool IsReceiving { get; set; }
  protected bool IsCompleting { get; set; }
  protected bool IsCancelling { get; set; }
  protected bool IsPrinting { get; set; }
  protected bool IsCreatingAutoPo { get; set; }
  protected bool ShowAutoPoModal { get; set; }

  protected bool IsDraftMode => SelectedPurchaseOrder is null || string.Equals(SelectedPurchaseOrder.Status, PurchaseOrderStatuses.Draft, StringComparison.OrdinalIgnoreCase);
  protected bool CanEditVendor => IsDraftMode && Lines.Count == 0;
  protected bool CanSearchMaterials => IsDraftMode && Editor.BusinessPartnerId > 0;
  protected bool CanIssue => SelectedPurchaseOrder is not null
    && string.Equals(SelectedPurchaseOrder.Status, PurchaseOrderStatuses.Draft, StringComparison.OrdinalIgnoreCase)
    && !IsMutating;
  protected bool CanReceive => SelectedPurchaseOrder is not null
    && PurchaseOrderStatuses.Open.Contains(SelectedPurchaseOrder.Status, StringComparer.OrdinalIgnoreCase)
    && ReceiveItems.Count > 0
    && !IsMutating;
  protected bool CanComplete => SelectedPurchaseOrder is not null
    && string.Equals(SelectedPurchaseOrder.Status, PurchaseOrderStatuses.PartiallyReceived, StringComparison.OrdinalIgnoreCase)
    && !IsMutating;
  protected bool CanCancel => SelectedPurchaseOrder is not null
    && (string.Equals(SelectedPurchaseOrder.Status, PurchaseOrderStatuses.Draft, StringComparison.OrdinalIgnoreCase)
        || string.Equals(SelectedPurchaseOrder.Status, PurchaseOrderStatuses.Issued, StringComparison.OrdinalIgnoreCase))
    && !IsMutating;
  protected bool CanPrint => SelectedPurchaseOrder is not null && !IsMutating;
  protected bool IsMutating => IsSavingDraft || IsIssuing || IsReceiving || IsCompleting || IsCancelling || IsPrinting || IsCreatingAutoPo;
  protected string CurrentOrderCode => SelectedPurchaseOrder?.PurchaseOrderCode ?? "Se asignará al guardar";
  protected string CurrentStatusLabel => GetStatusLabel(SelectedPurchaseOrder?.Status ?? PurchaseOrderStatuses.Draft);
  protected string CurrentStatusBadgeClass => GetStatusBadgeClass(SelectedPurchaseOrder?.Status ?? PurchaseOrderStatuses.Draft);
  protected decimal CurrentOrderedQuantity => Lines.Sum(line => line.OrderedQuantity);
  protected decimal CurrentReceivedQuantity => Lines.Sum(line => line.ReceivedQuantity);
  protected decimal CurrentRemainingQuantity => Lines.Sum(line => line.RemainingQuantity);
  protected IReadOnlyList<LookupOptionDto> VendorOptions => Catalog.Vendors;
  protected IReadOnlyList<LookupOptionDto> LocationOptions => Catalog.Locations;
  protected IReadOnlyList<string> StatusOptions => Catalog.Statuses;
  protected IReadOnlyList<PurchaseReceiptLineHistoryDto> ReceiptHistory => SelectedPurchaseOrder?.ReceiptHistory ?? Array.Empty<PurchaseReceiptLineHistoryDto>();

  protected override async Task OnInitializedAsync()
  {
    CurrentUserName = await ResolveCurrentUserAsync();
    Catalog = await PurchaseOrderService.GetCatalogAsync();
    AutoPoRequest = CreateAutoPoRequest();
    await LoadOrdersAsync();
    NuevaOrden();
  }

  protected async Task BuscarOrdenesAsync()
  {
    await LoadOrdersAsync();
  }

  protected void NuevaOrden()
  {
    SelectedPurchaseOrder = null;
    Editor = CreateEditor();
    AutoPoRequest = CreateAutoPoRequest(GetPreferredVendorId());
    Lines = [];
    SelectedLine = null;
    MaterialSearchText = string.Empty;
    MaterialSearchResults = [];
    ReceiveItems = [];
    ReceiptDate = DateTime.Today;
    ReceiptNotes = null;
    PendingAllocationLocationId = 0;
    PendingAllocationQuantity = 1m;
    HasExecutedMaterialSearch = false;
    MaterialThumbnailDataUrls = [];
    ShowAutoPoModal = false;
  }

  protected async Task SeleccionarOrdenAsync(int purchaseOrderId)
  {
    IsLoadingOrder = true;
    try
    {
      var detail = await PurchaseOrderService.GetPurchaseOrderAsync(purchaseOrderId);
      if (detail is null)
      {
        UiMessages.ShowWarning("La orden de compra seleccionada ya no existe.");
        return;
      }

      SelectedPurchaseOrder = detail;
      Editor = new PurchaseOrderUpsertRequest
      {
        Id = detail.Id,
        BusinessPartnerId = detail.BusinessPartnerId,
        OrderDate = detail.OrderDate,
        ExpectedDate = detail.ExpectedDate,
        Notes = detail.Notes
      };

      Lines = detail.Lines
        .Select(line => new EditablePurchaseLine
        {
          Id = line.Id,
          MaterialId = line.MaterialId,
          MaterialCode = line.MaterialCode,
          MaterialDescription = line.MaterialDescription,
          VendorCode = line.VendorCode,
          BaseUnitName = line.BaseUnitName,
          PurchaseQuantity = NormalizePurchaseQuantity(line.PurchaseQuantity),
          PurchaseUnitName = line.PurchaseUnitName,
          UnitPrice = line.UnitPrice,
          ReceivedQuantity = line.ReceivedQuantity,
          Allocations = line.Allocations
            .Select(allocation => new EditablePurchaseAllocation
            {
              Id = allocation.Id,
              LocationId = allocation.LocationId,
              LocationName = allocation.LocationName,
              LocationCode = allocation.LocationCode,
              PlannedQuantity = allocation.PlannedQuantity,
              ReceivedQuantity = allocation.ReceivedQuantity
            })
            .ToList()
        })
        .ToList();

      SelectedLine = SelectedLine is not null
        ? Lines.FirstOrDefault(line => line.MaterialId == SelectedLine.MaterialId)
        : Lines.FirstOrDefault();

      ReceiveItems = detail.Lines
        .SelectMany(line => line.Allocations
          .Where(allocation => allocation.RemainingQuantity > 0)
          .Select(allocation => new ReceiveAllocationInput
          {
            AllocationId = allocation.Id,
            PurchaseOrderLineId = line.Id,
            MaterialId = line.MaterialId,
            MaterialCode = line.MaterialCode,
            MaterialDescription = line.MaterialDescription,
            BaseUnitName = line.BaseUnitName,
            LocationId = allocation.LocationId,
            LocationName = allocation.LocationName,
            LocationCode = allocation.LocationCode,
            PlannedQuantity = allocation.PlannedQuantity,
            ReceivedQuantity = allocation.ReceivedQuantity
          }))
        .OrderBy(item => item.MaterialCode, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.MaterialDescription, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.LocationName, StringComparer.OrdinalIgnoreCase)
        .ToList();

      ReceiptDate = DateTime.Today;
      ReceiptNotes = null;
      MaterialSearchResults = [];
      MaterialSearchText = string.Empty;
      HasExecutedMaterialSearch = false;
      PendingAllocationLocationId = 0;
      PendingAllocationQuantity = 1m;
      await RefreshThumbnailsAsync();
      AutoPoRequest = CreateAutoPoRequest(Editor.BusinessPartnerId);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cargar la orden de compra. {ex.Message}");
    }
    finally
    {
      IsLoadingOrder = false;
      StateHasChanged();
    }
  }

  protected async Task BuscarMaterialesAsync()
  {
    if (!CanSearchMaterials)
    {
      UiMessages.ShowWarning("Selecciona un proveedor antes de buscar materiales.");
      return;
    }

    IsSearchingMaterials = true;
    HasExecutedMaterialSearch = true;
    try
    {
      MaterialSearchResults = (await MaterialService.GetMaterialsAsync(new MaterialFilter
      {
        VendorId = Editor.BusinessPartnerId,
        SearchText = MaterialSearchText,
        Status = "ACTIVO",
        Skip = 0,
        Take = MaterialSearchTake
      })).ToList();

      await RefreshThumbnailsAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudieron cargar los materiales del proveedor. {ex.Message}");
    }
    finally
    {
      IsSearchingMaterials = false;
    }
  }

  protected async Task AgregarMaterialAsync(MaterialListItemDto item)
  {
    if (!IsDraftMode)
    {
      return;
    }

    if (Lines.Any(line => line.MaterialId == item.Id))
    {
      UiMessages.ShowWarning("Ese material ya está incluido en la orden.");
      return;
    }

    try
    {
      var detail = await MaterialService.GetMaterialAsync(item.Id);
      if (detail is null)
      {
        UiMessages.ShowWarning("El material seleccionado ya no existe.");
        return;
      }

      var line = new EditablePurchaseLine
      {
        MaterialId = item.Id,
        MaterialCode = item.MaterialCode,
        MaterialDescription = item.Description,
        VendorCode = detail.VendorCode,
        BaseUnitName = item.BaseUnitName,
        PurchaseQuantity = NormalizePurchaseQuantity(detail.PurchaseQuantity),
        PurchaseUnitName = detail.PurchaseUnitName,
        UnitPrice = detail.Price,
        ReceivedQuantity = 0
      };

      Lines.Add(line);
      Lines = Lines
        .OrderBy(current => current.MaterialCode, StringComparer.OrdinalIgnoreCase)
        .ThenBy(current => current.MaterialDescription, StringComparer.OrdinalIgnoreCase)
        .ToList();
      SelectedLine = Lines.FirstOrDefault(current => current.MaterialId == item.Id);
      PendingAllocationLocationId = 0;
      PendingAllocationQuantity = 1m;
      await RefreshThumbnailsAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo agregar el material. {ex.Message}");
    }
  }

  protected void SeleccionarLinea(EditablePurchaseLine line)
  {
    SelectedLine = line;
    PendingAllocationLocationId = 0;
    PendingAllocationQuantity = 1m;
  }

  protected void AbrirAutoPoModal()
  {
    AutoPoRequest = CreateAutoPoRequest(GetPreferredVendorId());
    ShowAutoPoModal = true;
  }

  protected void CerrarAutoPoModal()
  {
    ShowAutoPoModal = false;
  }

  protected async Task CrearAutoPoAsync()
  {
    if (AutoPoRequest.BusinessPartnerId <= 0)
    {
      UiMessages.ShowWarning("Selecciona un proveedor para generar el Auto PO.");
      return;
    }

    IsCreatingAutoPo = true;
    try
    {
      var result = await PurchaseOrderService.CreateAutoDraftAsync(AutoPoRequest, CurrentUserName);
      if (!result.Success)
      {
        if (string.Equals(result.Message, "No hay materiales por reordenar para el proveedor seleccionado.", StringComparison.Ordinal))
        {
          UiMessages.ShowWarning(result.Message);
        }
        else
        {
          UiMessages.ShowError(result.Message);
        }

        return;
      }

      if (string.Equals(result.Message, "El proveedor ya tiene un borrador abierto. Se abrirá ese documento para revisión.", StringComparison.Ordinal))
      {
        UiMessages.ShowInfo(result.Message);
      }
      else
      {
        UiMessages.ShowSuccess(result.Message);
      }

      ShowAutoPoModal = false;
      await LoadOrdersAsync();
      if (result.EntityId.HasValue)
      {
        await SeleccionarOrdenAsync(result.EntityId.Value);
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo generar el Auto PO. {ex.Message}");
    }
    finally
    {
      IsCreatingAutoPo = false;
    }
  }

  protected async Task QuitarMaterialAsync(EditablePurchaseLine line)
  {
    if (!IsDraftMode)
    {
      return;
    }

    var confirmed = await ConfirmAsync("¿Deseas quitar este material de la orden de compra?");
    if (!confirmed)
    {
      return;
    }

    Lines.Remove(line);
    if (ReferenceEquals(SelectedLine, line))
    {
      SelectedLine = Lines.FirstOrDefault();
    }

    await RefreshThumbnailsAsync();
  }

  protected void AgregarAsignacion()
  {
    if (SelectedLine is null)
    {
      UiMessages.ShowWarning("Selecciona un material antes de agregar una ubicación.");
      return;
    }

    if (PendingAllocationLocationId <= 0)
    {
      UiMessages.ShowWarning("Selecciona una ubicación para agregar la asignación.");
      return;
    }

    if (PendingAllocationQuantity <= 0)
    {
      UiMessages.ShowWarning("La cantidad planeada debe ser mayor a 0.");
      return;
    }

    if (SelectedLine.Allocations.Any(allocation => allocation.LocationId == PendingAllocationLocationId))
    {
      UiMessages.ShowWarning("Esa ubicación ya existe para el material seleccionado.");
      return;
    }

    var location = Catalog.Locations.FirstOrDefault(item => item.Id == PendingAllocationLocationId);
    if (location is null)
    {
      UiMessages.ShowWarning("La ubicación seleccionada ya no existe.");
      return;
    }

    SelectedLine.Allocations.Add(new EditablePurchaseAllocation
    {
      LocationId = location.Id,
      LocationName = location.Name,
      LocationCode = location.Code,
      PlannedQuantity = PendingAllocationQuantity,
      ReceivedQuantity = 0
    });

    SelectedLine.Allocations = SelectedLine.Allocations
      .OrderBy(allocation => allocation.LocationName, StringComparer.OrdinalIgnoreCase)
      .ThenBy(allocation => allocation.LocationId)
      .ToList();

    PendingAllocationLocationId = 0;
    PendingAllocationQuantity = 1m;
  }

  protected void QuitarAsignacion(EditablePurchaseAllocation allocation)
  {
    if (SelectedLine is null)
    {
      return;
    }

    SelectedLine.Allocations.Remove(allocation);
  }

  protected async Task GuardarBorradorAsync()
  {
    IsSavingDraft = true;
    try
    {
      var result = await PurchaseOrderService.SaveDraftAsync(BuildDraftRequest(), CurrentUserName);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      await LoadOrdersAsync();
      if (result.EntityId.HasValue)
      {
        await SeleccionarOrdenAsync(result.EntityId.Value);
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo guardar la orden de compra. {ex.Message}");
    }
    finally
    {
      IsSavingDraft = false;
    }
  }

  protected async Task EmitirOrdenAsync()
  {
    if (SelectedPurchaseOrder is null)
    {
      return;
    }

    IsIssuing = true;
    try
    {
      var result = await PurchaseOrderService.IssueAsync(SelectedPurchaseOrder.Id, CurrentUserName);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      await LoadOrdersAsync();
      await SeleccionarOrdenAsync(SelectedPurchaseOrder.Id);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo emitir la orden de compra. {ex.Message}");
    }
    finally
    {
      IsIssuing = false;
    }
  }

  protected async Task RegistrarRecepcionAsync()
  {
    if (SelectedPurchaseOrder is null)
    {
      return;
    }

    var lines = ReceiveItems
      .Where(item => item.ReceiveNowQuantity > 0)
      .Select(item => new PurchaseReceiptLineCreateRequest
      {
        PurchaseOrderLineAllocationId = item.AllocationId,
        Quantity = item.ReceiveNowQuantity
      })
      .ToList();

    if (lines.Count == 0)
    {
      UiMessages.ShowWarning("Captura al menos una cantidad para registrar la recepción.");
      return;
    }

    IsReceiving = true;
    try
    {
      var result = await PurchaseOrderService.ReceiveAsync(new PurchaseReceiptCreateRequest
      {
        PurchaseOrderId = SelectedPurchaseOrder.Id,
        ReceiptDate = ReceiptDate,
        Notes = ReceiptNotes,
        Lines = lines
      }, CurrentUserName);

      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      await LoadOrdersAsync();
      await SeleccionarOrdenAsync(SelectedPurchaseOrder.Id);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo registrar la recepción. {ex.Message}");
    }
    finally
    {
      IsReceiving = false;
    }
  }

  protected async Task CerrarPendienteAsync()
  {
    if (SelectedPurchaseOrder is null)
    {
      return;
    }

    var confirmed = await ConfirmAsync("¿Deseas cerrar manualmente la cantidad pendiente de esta orden?");
    if (!confirmed)
    {
      return;
    }

    IsCompleting = true;
    try
    {
      var result = await PurchaseOrderService.CompleteAsync(SelectedPurchaseOrder.Id, CurrentUserName);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      await LoadOrdersAsync();
      await SeleccionarOrdenAsync(SelectedPurchaseOrder.Id);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cerrar la orden de compra. {ex.Message}");
    }
    finally
    {
      IsCompleting = false;
    }
  }

  protected async Task CancelarOrdenAsync()
  {
    if (SelectedPurchaseOrder is null)
    {
      return;
    }

    var confirmed = await ConfirmAsync("¿Deseas cancelar esta orden de compra?");
    if (!confirmed)
    {
      return;
    }

    IsCancelling = true;
    try
    {
      var result = await PurchaseOrderService.CancelAsync(SelectedPurchaseOrder.Id, CurrentUserName);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      await LoadOrdersAsync();
      await SeleccionarOrdenAsync(SelectedPurchaseOrder.Id);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cancelar la orden de compra. {ex.Message}");
    }
    finally
    {
      IsCancelling = false;
    }
  }

  protected async Task ImprimirOrdenAsync()
  {
    if (SelectedPurchaseOrder is null)
    {
      return;
    }

    IsPrinting = true;
    try
    {
      var detail = await PurchaseOrderService.GetPurchaseOrderAsync(SelectedPurchaseOrder.Id);
      if (detail is null)
      {
        UiMessages.ShowWarning("La orden de compra ya no existe.");
        return;
      }

      var document = await PurchaseOrderPdfDocumentFactory.CreateFromDetailAsync(detail);
      var pdfBytes = PurchaseOrderPdfService.Generate(document);
      var fileName = $"{detail.PurchaseOrderCode}.pdf";
      var dataUrl = $"data:application/pdf;base64,{Convert.ToBase64String(pdfBytes)}";
      await Js.InvokeVoidAsync("triggerFileDownload", fileName, dataUrl);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo generar el PDF de la orden de compra. {ex.Message}");
    }
    finally
    {
      IsPrinting = false;
    }
  }

  protected string GetOrderRowClass(PurchaseOrderListItemDto order)
    => SelectedPurchaseOrder?.Id == order.Id ? "table-primary" : string.Empty;

  protected string GetStatusBadgeClass(string? status)
    => NormalizeStatus(status) switch
    {
      PurchaseOrderStatuses.Draft => "badge rounded-pill text-bg-secondary",
      PurchaseOrderStatuses.Issued => "badge rounded-pill text-bg-primary",
      PurchaseOrderStatuses.PartiallyReceived => "badge rounded-pill text-bg-warning",
      PurchaseOrderStatuses.Completed => "badge rounded-pill text-bg-success",
      PurchaseOrderStatuses.Cancelled => "badge rounded-pill text-bg-dark",
      _ => "badge rounded-pill text-bg-secondary"
    };

  protected string GetStatusLabel(string? status)
    => NormalizeStatus(status) switch
    {
      PurchaseOrderStatuses.Draft => "Borrador",
      PurchaseOrderStatuses.Issued => "Emitida",
      PurchaseOrderStatuses.PartiallyReceived => "Recibida parcial",
      PurchaseOrderStatuses.Completed => "Completada",
      PurchaseOrderStatuses.Cancelled => "Cancelada",
      _ => string.IsNullOrWhiteSpace(status) ? "Sin status" : status.Trim()
    };

  protected string? GetMaterialThumbnailDataUrl(int materialId)
    => MaterialThumbnailDataUrls.TryGetValue(materialId, out var dataUrl) ? dataUrl : null;

  protected string GetMaterialFallbackText(string? materialCode, string? description)
    => !string.IsNullOrWhiteSpace(materialCode)
      ? materialCode.Trim()
      : string.IsNullOrWhiteSpace(description)
        ? "Sin foto"
        : description.Trim();

  protected string? GetPurchasePresentationSummary(EditablePurchaseLine line)
  {
    if (line.PurchaseQuantity <= 1m
        || string.IsNullOrWhiteSpace(line.PurchaseUnitName)
        || string.IsNullOrWhiteSpace(line.BaseUnitName))
    {
      return null;
    }

    var orderedPurchaseUnits = line.OrderedQuantity / line.PurchaseQuantity;
    return $"{FormatQuantity(orderedPurchaseUnits)} {line.PurchaseUnitName} x {FormatQuantity(line.PurchaseQuantity)} {line.BaseUnitName} = {FormatQuantity(line.OrderedQuantity)} {line.BaseUnitName}";
  }

  protected bool HasInvalidPurchaseMultiple(EditablePurchaseLine line)
    => line.PurchaseQuantity > 1m && !IsWholePurchaseMultiple(line.OrderedQuantity, line.PurchaseQuantity);

  protected bool HasInvalidPurchaseAllocationMultiple(EditablePurchaseLine line)
    => line.PurchaseQuantity > 1m
      && line.Allocations.Any(allocation => !IsWholePurchaseMultiple(allocation.PlannedQuantity, line.PurchaseQuantity));

  protected bool HasInvalidPurchaseAllocationMultiple(EditablePurchaseLine line, EditablePurchaseAllocation allocation)
    => line.PurchaseQuantity > 1m && !IsWholePurchaseMultiple(allocation.PlannedQuantity, line.PurchaseQuantity);

  protected bool HasInvalidPurchasePackConfiguration(EditablePurchaseLine line)
    => HasInvalidPurchaseMultiple(line) || HasInvalidPurchaseAllocationMultiple(line);

  protected string? GetPurchaseAllocationValidationMessage(EditablePurchaseLine line, EditablePurchaseAllocation allocation)
  {
    if (!HasInvalidPurchaseAllocationMultiple(line, allocation))
    {
      return null;
    }

    var baseUnitName = string.IsNullOrWhiteSpace(line.BaseUnitName)
      ? "unidad base"
      : line.BaseUnitName.Trim();
    var purchaseUnitName = string.IsNullOrWhiteSpace(line.PurchaseUnitName)
      ? "unidad de compra"
      : line.PurchaseUnitName.Trim();
    var locationLabel = string.IsNullOrWhiteSpace(allocation.LocationCode)
      ? allocation.LocationName
      : allocation.LocationCode;

    return $"{locationLabel}: usa múltiplos de {FormatQuantity(line.PurchaseQuantity)} {baseUnitName} por {purchaseUnitName}.";
  }

  private async Task LoadOrdersAsync()
  {
    IsLoadingOrders = true;
    try
    {
      Orders = (await PurchaseOrderService.GetPurchaseOrdersAsync(Filter)).ToList();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudieron cargar las órdenes de compra. {ex.Message}");
    }
    finally
    {
      IsLoadingOrders = false;
      StateHasChanged();
    }
  }

  private PurchaseOrderUpsertRequest BuildDraftRequest()
    => new()
    {
      Id = SelectedPurchaseOrder?.Id,
      BusinessPartnerId = Editor.BusinessPartnerId,
      OrderDate = Editor.OrderDate,
      ExpectedDate = Editor.ExpectedDate,
      Notes = Editor.Notes,
      Lines = Lines
        .Select(line => new PurchaseOrderLineUpsertRequest
        {
          Id = line.Id,
          MaterialId = line.MaterialId,
          UnitPrice = line.UnitPrice,
          PurchaseQuantitySnapshot = NormalizePurchaseQuantity(line.PurchaseQuantity),
          PurchaseUnitNameSnapshot = line.PurchaseUnitName,
          Allocations = line.Allocations
            .Select(allocation => new PurchaseOrderAllocationUpsertRequest
            {
              Id = allocation.Id,
              LocationId = allocation.LocationId,
              PlannedQuantity = allocation.PlannedQuantity
            })
            .ToList()
        })
        .ToList()
    };

  private async Task RefreshThumbnailsAsync()
  {
    var materialIds = MaterialSearchResults.Select(item => item.Id)
      .Concat(Lines.Select(line => line.MaterialId))
      .Concat(ReceiptHistory.Select(item => item.MaterialId))
      .Distinct()
      .ToArray();

    MaterialThumbnailDataUrls = materialIds.Length == 0
      ? []
      : new Dictionary<int, string>(await ThumbnailHydrator.GetDataUrlsAsync(materialIds));
  }

  private async Task<bool> ConfirmAsync(string message)
    => await Js.InvokeAsync<bool>("confirm", message);

  private async Task<string> ResolveCurrentUserAsync()
  {
    var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
    var user = authState.User;
    if (user.Identity?.IsAuthenticated != true)
    {
      return "OrionERP";
    }

    return user.Identity.Name
      ?? user.Claims.FirstOrDefault(claim => claim.Type is "name" or "preferred_username")?.Value
      ?? "OrionERP";
  }

  private static string NormalizeStatus(string? status)
    => string.IsNullOrWhiteSpace(status) ? string.Empty : status.Trim();

  private int? GetPreferredVendorId()
    => Editor.BusinessPartnerId > 0
      ? Editor.BusinessPartnerId
      : Filter.VendorId;

  private static decimal NormalizePurchaseQuantity(decimal value)
    => value > 0m ? value : 1m;

  private static bool IsWholePurchaseMultiple(decimal quantity, decimal purchaseQuantity)
  {
    var normalizedPurchaseQuantity = NormalizePurchaseQuantity(purchaseQuantity);
    if (normalizedPurchaseQuantity <= 1m)
    {
      return true;
    }

    var quotient = quantity / normalizedPurchaseQuantity;
    return quotient == decimal.Truncate(quotient);
  }

  private static string FormatQuantity(decimal value)
    => value.ToString("N2", CultureInfo.CurrentCulture);

  private static PurchaseOrderUpsertRequest CreateEditor()
    => new()
    {
      OrderDate = DateTime.Today
    };

  private static AutoPurchaseOrderCreateRequest CreateAutoPoRequest(int? businessPartnerId = null)
    => new()
    {
      BusinessPartnerId = businessPartnerId.GetValueOrDefault(),
      OrderDate = DateTime.Today
    };

  protected sealed class EditablePurchaseLine
  {
    public int? Id { get; set; }
    public int MaterialId { get; set; }
    public string MaterialCode { get; set; } = string.Empty;
    public string MaterialDescription { get; set; } = string.Empty;
    public string? VendorCode { get; set; }
    public string? BaseUnitName { get; set; }
    public decimal PurchaseQuantity { get; set; } = 1m;
    public string? PurchaseUnitName { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal ReceivedQuantity { get; set; }
    public List<EditablePurchaseAllocation> Allocations { get; set; } = [];
    public decimal OrderedQuantity => Allocations.Sum(allocation => allocation.PlannedQuantity);
    public decimal RemainingQuantity => Math.Max(OrderedQuantity - ReceivedQuantity, 0m);
  }

  protected sealed class EditablePurchaseAllocation
  {
    public int? Id { get; set; }
    public int LocationId { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public string? LocationCode { get; set; }
    public decimal PlannedQuantity { get; set; }
    public decimal ReceivedQuantity { get; set; }
    public decimal RemainingQuantity => Math.Max(PlannedQuantity - ReceivedQuantity, 0m);
  }

  protected sealed class ReceiveAllocationInput
  {
    public int AllocationId { get; set; }
    public int PurchaseOrderLineId { get; set; }
    public int MaterialId { get; set; }
    public string MaterialCode { get; set; } = string.Empty;
    public string MaterialDescription { get; set; } = string.Empty;
    public string? BaseUnitName { get; set; }
    public int LocationId { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public string? LocationCode { get; set; }
    public decimal PlannedQuantity { get; set; }
    public decimal ReceivedQuantity { get; set; }
    public decimal RemainingQuantity => Math.Max(PlannedQuantity - ReceivedQuantity, 0m);
    public decimal ReceiveNowQuantity { get; set; }
  }
}
