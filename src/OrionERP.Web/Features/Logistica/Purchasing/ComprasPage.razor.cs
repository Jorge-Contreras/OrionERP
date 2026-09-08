using OrionERP.Application.Common;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using System.Globalization;
using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Application.Features.Logistica.Purchasing;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Web.Services;
using OrionERP.Web.State;

namespace OrionERP.Web.Features.Logistica.Purchasing;

public partial class ComprasPage : ComponentBase
{
  private const int MaterialSearchTake = 25;
  private const int VendorMaterialSearchTake = 100;

  [Inject] private IPurchaseOrderService PurchaseOrderService { get; set; } = default!;
  [Inject] private IMaterialService MaterialService { get; set; } = default!;
  [Inject] private IPurchaseMaterialThumbnailHydrator ThumbnailHydrator { get; set; } = default!;
  [Inject] private IPurchaseOrderPdfService PurchaseOrderPdfService { get; set; } = default!;
  [Inject] private IPurchaseOrderPdfDocumentFactory PurchaseOrderPdfDocumentFactory { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;
  [Inject] private IOperationErrorPresenter Errors { get; set; } = default!;
  [Inject] private IJSRuntime Js { get; set; } = default!;
  [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
  [Inject] private ICurrentCompanyContext RfcState { get; set; } = default!;

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
  protected HashSet<int> AutoPoSelectedRoomIds { get; set; } = [];
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
  protected bool ShowOrderBrowser { get; set; }

  protected bool IsDraftMode => SelectedPurchaseOrder is null || string.Equals(SelectedPurchaseOrder.Status, PurchaseOrderStatuses.Draft, StringComparison.OrdinalIgnoreCase);
  protected bool CanEditVendor => IsDraftMode && Lines.Count == 0;
  /// <summary>
  /// La búsqueda arranca acotada a lo que surte el proveedor de la orden. Abrir el catálogo
  /// completo sigue disponible porque cuando el proveedor habitual no tiene el producto hay que
  /// poder comprarlo con otro sin reasignar el material, pero es una decisión del usuario.
  /// </summary>
  protected bool CanSearchMaterials => IsDraftMode;

  /// <summary>Sin proveedor no hay catálogo que acotar: la búsqueda recorre todo el inventario.</summary>
  protected bool HasVendorSelected => Editor.BusinessPartnerId > 0;

  /// <summary>Lo enciende el usuario cuando el proveedor no surte lo que necesita comprar.</summary>
  protected bool SearchOutsideVendorCatalog { get; set; }

  /// <summary>Alcance con el que saldría la próxima búsqueda.</summary>
  protected bool IsVendorScopedSearch => HasVendorSelected && !SearchOutsideVendorCatalog;

  /// <summary>Alcance con el que se trajo <see cref="MaterialSearchResults"/>.</summary>
  protected bool LastSearchWasVendorScoped { get; private set; }

  /// <summary>La búsqueda llenó el tope de renglones: quedaron materiales fuera de la lista.</summary>
  protected bool MaterialSearchHitLimit { get; private set; }

  /// <summary>Tope de renglones que aplicó la última búsqueda.</summary>
  protected int MaterialSearchLimit { get; private set; } = MaterialSearchTake;

  /// <summary>Materiales agregados a la orden que este proveedor no surte de costumbre.</summary>
  protected List<string> UnlinkedMaterialNames { get; } = [];

  /// <summary>Registra al proveedor de la orden en los materiales de <see cref="UnlinkedMaterialNames"/>.</summary>
  protected bool LinkMaterialsToVendor { get; set; } = true;

  protected string CurrentVendorName
    => VendorOptions.FirstOrDefault(option => option.Id == Editor.BusinessPartnerId)?.Name ?? "este proveedor";
  protected bool CanIssue => SelectedPurchaseOrder is not null
    && string.Equals(SelectedPurchaseOrder.Status, PurchaseOrderStatuses.Draft, StringComparison.OrdinalIgnoreCase)
    && !IsMutating;
  protected bool CanEnterReceipt => SelectedPurchaseOrder is not null
    && PurchaseOrderStatuses.Open.Contains(SelectedPurchaseOrder.Status, StringComparer.OrdinalIgnoreCase)
    && ReceiveItems.Count > 0
    && !IsMutating;
  protected bool CanReceive => CanEnterReceipt
    && ReceiveItems.Any(item => item.ReceiveNowQuantity > 0m)
    && ReceiveItems
      .Where(item => item.ReceiveNowQuantity > 0m)
      .All(item => item.TotalAmount.GetValueOrDefault() > 0m);
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
  protected int CurrentMaterialCount => Lines.Count;
  protected int CurrentAllocationCount => Lines.Sum(line => line.Allocations.Count);
  protected int CurrentPendingAllocationCount => Lines.Sum(line => line.Allocations.Count(allocation => allocation.RemainingQuantity > 0m));
  protected int ReceiptCapturedItemCount => ReceiveItems.Count(item => item.ReceiveNowQuantity > 0m);
  protected int ReceiptMissingAmountCount => ReceiveItems.Count(item => item.ReceiveNowQuantity > 0m && item.TotalAmount.GetValueOrDefault() <= 0m);
  protected PurchaseReceiptAmounts CurrentReceiptAmounts
  {
    get
    {
      var selectedItems = ReceiveItems
        .Where(item => item.ReceiveNowQuantity > 0m && item.TotalAmount.GetValueOrDefault() > 0m)
        .Select(item => PurchaseReceiptAmountCalculator.Calculate(item.TotalAmount!.Value, item.IncludesIva))
        .ToList();

      return new PurchaseReceiptAmounts(
        selectedItems.Sum(item => item.SubtotalAmount),
        selectedItems.Sum(item => item.IvaAmount),
        selectedItems.Sum(item => item.TotalAmount));
    }
  }
  protected int CurrentProcessStep
    => NormalizeStatus(SelectedPurchaseOrder?.Status ?? PurchaseOrderStatuses.Draft) switch
    {
      PurchaseOrderStatuses.Issued => 3,
      PurchaseOrderStatuses.PartiallyReceived => 3,
      PurchaseOrderStatuses.Completed => 4,
      _ => 1
    };
  protected string ProcessGuidance
    => NormalizeStatus(SelectedPurchaseOrder?.Status ?? PurchaseOrderStatuses.Draft) switch
    {
      PurchaseOrderStatuses.Draft => "Elige proveedor y materiales, asigna sus ubicaciones y guarda la compra.",
      PurchaseOrderStatuses.Issued => "Compara la entrega con el ticket y captura cantidad, total e IVA de cada artículo.",
      PurchaseOrderStatuses.PartiallyReceived => "Continúa con lo que llegó hoy o cierra lo que ya no entregará el proveedor.",
      PurchaseOrderStatuses.Completed => "Compra terminada. Los precios de Materiales ya reflejan los importes recibidos.",
      PurchaseOrderStatuses.Cancelled => "Esta compra está cancelada y ya no admite cambios.",
      _ => "Sigue el paso marcado para continuar."
    };
  protected IReadOnlyList<LookupOptionDto> VendorOptions => Catalog.Vendors;
  protected IReadOnlyList<LookupOptionDto> LocationOptions => Catalog.Locations;
  protected IReadOnlyList<LookupOptionDto> AutoPoRoomOptions => Catalog.Rooms;
  protected IReadOnlyList<string> StatusOptions => Catalog.Statuses;
  protected IReadOnlyList<PurchaseReceiptLineHistoryDto> ReceiptHistory => SelectedPurchaseOrder?.ReceiptHistory ?? Array.Empty<PurchaseReceiptLineHistoryDto>();
  protected IReadOnlyList<LookupOptionDto> SelectedPurchaseOrderRoomScope => SelectedPurchaseOrder?.RoomScope ?? Array.Empty<LookupOptionDto>();
  protected IReadOnlyList<LookupOptionDto> AutoPoSelectedRooms => AutoPoRoomOptions
    .Where(room => AutoPoSelectedRoomIds.Contains(room.Id))
    .OrderBy(room => room.Name, StringComparer.OrdinalIgnoreCase)
    .ThenBy(room => room.Id)
    .ToList();
  protected string AutoPoRoomSelectionSummary
    => AutoPoSelectedRooms.Count switch
    {
      0 => "Todas las suites",
      1 => AutoPoSelectedRooms[0].Name,
      _ => $"{AutoPoSelectedRooms.Count} suites seleccionadas"
    };
  protected string SelectedPurchaseOrderRoomScopeSummary
    => SelectedPurchaseOrderRoomScope.Count switch
    {
      0 => "Todas las suites",
      1 => SelectedPurchaseOrderRoomScope[0].Name,
      _ => $"{SelectedPurchaseOrderRoomScope.Count} suites"
    };

  protected override async Task OnInitializedAsync()
  {
    CurrentUserName = await ResolveCurrentUserAsync();
    Catalog = await PurchaseOrderService.GetCatalogAsync();
    ResetAutoPoRequest();
    await LoadOrdersAsync();
    NuevaOrden();
    ShowOrderBrowser = true;
  }

  protected async Task BuscarOrdenesAsync()
  {
    await LoadOrdersAsync();
  }

  protected Task OnOrderSearchKeyUpAsync(KeyboardEventArgs args)
    => args.Key == "Enter" ? BuscarOrdenesAsync() : Task.CompletedTask;

  protected void NuevaOrden()
  {
    SelectedPurchaseOrder = null;
    Editor = CreateEditor();
    ResetAutoPoRequest(GetPreferredVendorId());
    Lines = [];
    SelectedLine = null;
    ResetMaterialSearch();
    ReceiveItems = [];
    ReceiptDate = DateTime.Today;
    ReceiptNotes = null;
    PendingAllocationLocationId = 0;
    PendingAllocationQuantity = 1m;
    MaterialThumbnailDataUrls = [];
    UnlinkedMaterialNames.Clear();
    LinkMaterialsToVendor = true;
    ShowAutoPoModal = false;
    ShowOrderBrowser = false;
  }

  protected void AlternarExploradorOrdenes()
    => ShowOrderBrowser = !ShowOrderBrowser;

  protected async Task SeleccionarOrdenAsync(int purchaseOrderId)
  {
    // El aviso de proveedores por registrar pertenece a lo que se capturó en esta pantalla; al
    // abrir otra orden se descarta.
    UnlinkedMaterialNames.Clear();
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
      ShowOrderBrowser = false;
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
          PurchaseIncrement = MaterialPurchaseIncrement.Normalize(line.PurchaseIncrement),
          BaseUnitPrice = line.BaseUnitPrice,
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
            PurchaseQuantity = NormalizePurchaseQuantity(line.PurchaseQuantity),
            PurchaseUnitName = line.PurchaseUnitName,
            BaseUnitPrice = line.BaseUnitPrice,
            LocationId = allocation.LocationId,
            LocationName = allocation.LocationName,
            LocationCode = allocation.LocationCode,
            PlannedQuantity = allocation.PlannedQuantity,
            ReceivedQuantity = allocation.ReceivedQuantity
          }))
        .OrderBy(item => item.MaterialDescription, MaterialSortOrder.Comparer)
        .ThenBy(item => item.MaterialCode, MaterialSortOrder.Comparer)
        .ThenBy(item => item.LocationName, StringComparer.OrdinalIgnoreCase)
        .ToList();

      ReceiptDate = DateTime.Today;
      ReceiptNotes = null;
      ResetMaterialSearch();
      PendingAllocationLocationId = 0;
      PendingAllocationQuantity = GetDefaultPendingAllocationBaseQuantity(SelectedLine);
      await RefreshThumbnailsAsync();
      ResetAutoPoRequest(Editor.BusinessPartnerId);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError(Errors.ToUserMessage(ex, "cargar la orden de compra", new { purchaseOrderId }));
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
      UiMessages.ShowWarning("Guarda o abre una orden en borrador para agregar materiales.");
      return;
    }

    // El alcance se congela al disparar la consulta: la lista y sus avisos tienen que describir
    // lo que se trajo, no lo que el usuario alcance a cambiar después.
    var vendorScoped = IsVendorScopedSearch;
    var take = vendorScoped ? VendorMaterialSearchTake : MaterialSearchTake;

    IsSearchingMaterials = true;
    HasExecutedMaterialSearch = true;
    try
    {
      MaterialSearchResults = (await MaterialService.GetMaterialsAsync(new MaterialFilter
      {
        Rfc = CurrentRfc,
        VendorId = vendorScoped ? Editor.BusinessPartnerId : null,
        HighlightVendorId = HasVendorSelected ? Editor.BusinessPartnerId : null,
        SearchText = MaterialSearchText,
        Status = "ACTIVO",
        Skip = 0,
        Take = take
      })).ToList();

      LastSearchWasVendorScoped = vendorScoped;
      MaterialSearchLimit = take;
      MaterialSearchHitLimit = MaterialSearchResults.Count >= take;

      await RefreshThumbnailsAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError(Errors.ToUserMessage(ex, "cargar los materiales", new { MaterialSearchText, Editor.BusinessPartnerId, vendorScoped }));
    }
    finally
    {
      IsSearchingMaterials = false;
    }
  }

  protected Task OnMaterialSearchKeyUpAsync(KeyboardEventArgs args)
    => args.Key == "Enter" ? BuscarMaterialesAsync() : Task.CompletedTask;

  /// <summary>Regresa la búsqueda al catálogo del proveedor de la orden.</summary>
  protected Task BuscarSoloDelProveedorAsync()
  {
    SearchOutsideVendorCatalog = false;
    return BuscarMaterialesAsync();
  }

  /// <summary>Abre la búsqueda a todo el catálogo cuando el proveedor no surte lo que se necesita.</summary>
  protected Task BuscarEnTodoElCatalogoAsync()
  {
    SearchOutsideVendorCatalog = true;
    return BuscarMaterialesAsync();
  }

  /// <summary>Cambiar de proveedor invalida la lista: era el catálogo del proveedor anterior.</summary>
  protected void OnVendorChanged() => ResetMaterialSearch();

  /// <summary>La caja de búsqueda entrega el id elegido; el editor guarda 0 cuando no hay proveedor.</summary>
  protected Task OnEditorVendorSelectedAsync(int vendorId)
  {
    Editor.BusinessPartnerId = vendorId;
    OnVendorChanged();

    return Task.CompletedTask;
  }

  /// <summary>El filtro usa nulo para "todos"; la caja de búsqueda usa 0.</summary>
  protected int FilterVendorId
  {
    get => Filter.VendorId ?? 0;
    set => Filter.VendorId = value == 0 ? null : value;
  }

  /// <summary>Deja la búsqueda en su alcance de arranque: sólo lo que surte el proveedor.</summary>
  private void ResetMaterialSearch()
  {
    MaterialSearchText = string.Empty;
    MaterialSearchResults = [];
    HasExecutedMaterialSearch = false;
    SearchOutsideVendorCatalog = false;
    LastSearchWasVendorScoped = false;
    MaterialSearchHitLimit = false;
    MaterialSearchLimit = MaterialSearchTake;
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
      var detail = await MaterialService.GetMaterialAsync(CurrentRfc, item.Id);
      if (detail is null)
      {
        UiMessages.ShowWarning("El material seleccionado ya no existe.");
        return;
      }

      // Cuando el proveedor de la orden ya surte el material, manda su ficha: su SKU, su
      // presentación y el último precio que le pagamos.
      var vendorLink = detail.Vendors.FirstOrDefault(vendor => vendor.BusinessPartnerId == Editor.BusinessPartnerId);

      var line = new EditablePurchaseLine
      {
        MaterialId = item.Id,
        MaterialCode = item.MaterialCode,
        MaterialDescription = item.Description,
        VendorCode = vendorLink?.VendorCode ?? detail.VendorCode,
        BaseUnitName = detail.BaseUnitName ?? item.BaseUnitName,
        PurchaseQuantity = NormalizePurchaseQuantity(vendorLink?.PurchaseQuantity ?? detail.PurchaseQuantity),
        PurchaseUnitName = vendorLink?.PurchaseUnitName ?? detail.PurchaseUnitName,
        PurchaseIncrement = MaterialPurchaseIncrement.Normalize(vendorLink?.PurchaseIncrement ?? detail.PurchaseIncrement),
        BaseUnitPrice = vendorLink?.LastUnitPrice ?? detail.BaseUnitPrice,
        ReceivedQuantity = 0
      };

      if (vendorLink is null && Editor.BusinessPartnerId > 0)
      {
        var materialName = string.IsNullOrWhiteSpace(item.Description) ? item.MaterialCode : item.Description;
        if (!UnlinkedMaterialNames.Contains(materialName))
        {
          UnlinkedMaterialNames.Add(materialName);
        }
      }

      Lines.Add(line);
      Lines = Lines
        .OrderBy(current => current.MaterialDescription, MaterialSortOrder.Comparer)
        .ThenBy(current => current.MaterialCode, MaterialSortOrder.Comparer)
        .ToList();
      SelectedLine = Lines.FirstOrDefault(current => current.MaterialId == item.Id);
      PendingAllocationLocationId = 0;
      PendingAllocationQuantity = GetDefaultPendingAllocationBaseQuantity(SelectedLine);
      await RefreshThumbnailsAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError(Errors.ToUserMessage(ex, "agregar el material a la orden", new { MaterialId = item.Id, item.MaterialCode }));
    }
  }

  protected void SeleccionarLinea(EditablePurchaseLine line)
  {
    SelectedLine = line;
    PendingAllocationLocationId = 0;
    PendingAllocationQuantity = GetDefaultPendingAllocationBaseQuantity(line);
  }

  protected void AbrirAutoPoModal()
  {
    ResetAutoPoRequest(GetPreferredVendorId());
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

    var normalizedRoomIds = GetNormalizedAutoPoRoomIds();
    AutoPoRequest.RoomIds = normalizedRoomIds.ToList();
    if (normalizedRoomIds.Count == 0)
    {
      AutoPoSelectedRoomIds.Clear();
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
      UiMessages.ShowError(Errors.ToUserMessage(ex, "generar la orden de compra automática", new { AutoPoRequest.BusinessPartnerId, RoomCount = AutoPoRequest.RoomIds?.Count ?? 0 }));
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
    UnlinkedMaterialNames.Remove(string.IsNullOrWhiteSpace(line.MaterialDescription) ? line.MaterialCode : line.MaterialDescription);
    if (ReferenceEquals(SelectedLine, line))
    {
      SelectedLine = Lines.FirstOrDefault();
      PendingAllocationQuantity = GetDefaultPendingAllocationBaseQuantity(SelectedLine);
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
    PendingAllocationQuantity = GetDefaultPendingAllocationBaseQuantity(SelectedLine);
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
      UiMessages.ShowError(Errors.ToUserMessage(ex, "guardar la orden de compra", new { SelectedPurchaseOrder?.Id, Editor.BusinessPartnerId, LineCount = Lines.Count }));
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
      UiMessages.ShowError(Errors.ToUserMessage(ex, "emitir la orden de compra", new { SelectedPurchaseOrder?.Id }));
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
        Quantity = item.ReceiveNowQuantity,
        TotalAmount = item.TotalAmount.GetValueOrDefault(),
        IncludesIva = item.IncludesIva
      })
      .ToList();

    if (lines.Count == 0)
    {
      UiMessages.ShowWarning("Captura al menos una cantidad para registrar la recepción.");
      return;
    }

    if (lines.Any(line => line.TotalAmount <= 0m))
    {
      UiMessages.ShowWarning("Captura el total del ticket para cada artículo que vas a recibir.");
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
      UiMessages.ShowError(Errors.ToUserMessage(ex, "registrar la recepción", new { SelectedPurchaseOrder?.Id, LineCount = lines.Count }));
    }
    finally
    {
      IsReceiving = false;
    }
  }

  protected void RecibirTodo()
  {
    foreach (var item in ReceiveItems)
    {
      item.ReceiveNowQuantity = item.RemainingQuantity;
    }
  }

  protected PurchaseReceiptAmounts GetReceiptItemAmounts(ReceiveAllocationInput item)
    => item.TotalAmount.GetValueOrDefault() > 0m
      ? PurchaseReceiptAmountCalculator.Calculate(item.TotalAmount!.Value, item.IncludesIva)
      : default;

  protected decimal? GetReceiptItemBaseUnitCost(ReceiveAllocationInput item)
    => item.TotalAmount.GetValueOrDefault() > 0m && item.ReceiveNowQuantity > 0m
      ? PurchaseReceiptAmountCalculator.CalculateBaseUnitCost(
        item.TotalAmount!.Value,
        item.IncludesIva,
        item.ReceiveNowQuantity)
      : null;

  protected decimal? GetSuggestedReceiptTotal(ReceiveAllocationInput item)
    => item.BaseUnitPrice.HasValue && item.ReceiveNowQuantity > 0m
      ? decimal.Round(item.BaseUnitPrice.Value * item.ReceiveNowQuantity, 2, MidpointRounding.AwayFromZero)
      : null;

  protected string GetProcessStepClass(int step)
  {
    if (NormalizeStatus(SelectedPurchaseOrder?.Status) == PurchaseOrderStatuses.Cancelled)
    {
      return "is-disabled";
    }

    if (step < CurrentProcessStep || (CurrentProcessStep == 4 && step == 4))
    {
      return "is-complete";
    }

    return step == CurrentProcessStep ? "is-current" : string.Empty;
  }

  protected string FormatMoney(decimal? amount)
    => amount.HasValue ? amount.Value.ToString("C2", CultureInfo.CurrentCulture) : "—";

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
      UiMessages.ShowError(Errors.ToUserMessage(ex, "cerrar la cantidad pendiente de la orden", new { SelectedPurchaseOrder?.Id }));
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
      UiMessages.ShowError(Errors.ToUserMessage(ex, "cancelar la orden de compra", new { SelectedPurchaseOrder?.Id }));
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
      UiMessages.ShowError(Errors.ToUserMessage(ex, "generar el PDF de la orden de compra", new { SelectedPurchaseOrder?.Id }));
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

  protected string GetPurchaseUnitName(EditablePurchaseLine line)
    => PurchaseQuantityDisplay.GetPrimaryUnitName(line.BaseUnitName, line.PurchaseUnitName);

  protected string GetPurchaseUnitName(ReceiveAllocationInput item)
    => PurchaseQuantityDisplay.GetPrimaryUnitName(item.BaseUnitName, item.PurchaseUnitName);

  protected string GetPurchaseUnitName(PurchaseReceiptLineHistoryDto item)
    => PurchaseQuantityDisplay.GetPrimaryUnitName(item.BaseUnitName, item.PurchaseUnitName);

  protected string? GetPurchasePresentationSummary(EditablePurchaseLine line)
    => PurchaseQuantityDisplay.BuildPresentationSummary(
      line.BaseUnitName,
      line.PurchaseQuantity,
      line.PurchaseUnitName,
      CultureInfo.CurrentCulture);

  protected string FormatBaseUnitPrice(EditablePurchaseLine line)
    => PurchaseQuantityDisplay.FormatBaseUnitPrice(
      line.BaseUnitPrice,
      line.BaseUnitName,
      CultureInfo.CurrentCulture);

  protected string? GetPurchasePresentationPriceEquivalent(EditablePurchaseLine line)
    => PurchaseQuantityDisplay.BuildPurchasePresentationPriceEquivalent(
      line.BaseUnitPrice,
      line.PurchaseQuantity,
      line.PurchaseUnitName,
      CultureInfo.CurrentCulture);

  protected string FormatPurchaseQuantity(EditablePurchaseLine line, decimal quantity)
    => PurchaseQuantityDisplay.FormatQuantity(
      quantity,
      line.PurchaseQuantity,
      line.BaseUnitName,
      line.PurchaseUnitName,
      CultureInfo.CurrentCulture);

  protected string FormatPurchaseQuantity(ReceiveAllocationInput item, decimal quantity)
    => PurchaseQuantityDisplay.FormatQuantity(
      quantity,
      item.PurchaseQuantity,
      item.BaseUnitName,
      item.PurchaseUnitName,
      CultureInfo.CurrentCulture);

  protected string FormatPurchaseQuantity(PurchaseReceiptLineHistoryDto item, decimal quantity)
    => PurchaseQuantityDisplay.FormatQuantity(
      quantity,
      item.PurchaseQuantity,
      item.BaseUnitName,
      item.PurchaseUnitName,
      CultureInfo.CurrentCulture);

  protected decimal GetPendingAllocationDisplayQuantity()
    => SelectedLine is null
      ? PendingAllocationQuantity
      : PurchaseQuantityDisplay.ToDisplayQuantity(
        PendingAllocationQuantity,
        SelectedLine.PurchaseQuantity,
        SelectedLine.PurchaseUnitName);

  protected void SetPendingAllocationDisplayQuantity(decimal quantity)
  {
    PendingAllocationQuantity = SelectedLine is null
      ? quantity
      : PurchaseQuantityDisplay.ToBaseQuantity(
        quantity,
        SelectedLine.PurchaseQuantity,
        SelectedLine.PurchaseUnitName);
  }

  protected decimal GetAllocationDisplayQuantity(EditablePurchaseLine line, EditablePurchaseAllocation allocation)
    => PurchaseQuantityDisplay.ToDisplayQuantity(
      allocation.PlannedQuantity,
      line.PurchaseQuantity,
      line.PurchaseUnitName);

  protected void SetAllocationDisplayQuantity(EditablePurchaseLine line, EditablePurchaseAllocation allocation, decimal quantity)
    => allocation.PlannedQuantity = PurchaseQuantityDisplay.ToBaseQuantity(
      quantity,
      line.PurchaseQuantity,
      line.PurchaseUnitName);

  protected decimal GetReceiveNowDisplayQuantity(ReceiveAllocationInput item)
    => PurchaseQuantityDisplay.ToDisplayQuantity(
      item.ReceiveNowQuantity,
      item.PurchaseQuantity,
      item.PurchaseUnitName);

  protected void SetReceiveNowDisplayQuantity(ReceiveAllocationInput item, decimal quantity)
    => item.ReceiveNowQuantity = PurchaseQuantityDisplay.ToBaseQuantity(
      quantity,
      item.PurchaseQuantity,
      item.PurchaseUnitName);

  protected void UpdateReceiveTotalAmount(ReceiveAllocationInput item, decimal? amount)
    => item.TotalAmount = amount.HasValue
      ? decimal.Round(amount.Value, 2, MidpointRounding.AwayFromZero)
      : null;

  protected bool HasInvalidPurchaseMultiple(EditablePurchaseLine line)
    => !MaterialPurchaseIncrement.IsValidQuantity(
      line.OrderedQuantity, line.PurchaseQuantity, line.PurchaseUnitName, line.PurchaseIncrement);

  protected bool HasInvalidPurchaseAllocationMultiple(EditablePurchaseLine line)
    => line.Allocations.Any(allocation => HasInvalidPurchaseAllocationMultiple(line, allocation));

  protected bool HasInvalidPurchaseAllocationMultiple(EditablePurchaseLine line, EditablePurchaseAllocation allocation)
    => !MaterialPurchaseIncrement.IsValidQuantity(
      allocation.PlannedQuantity, line.PurchaseQuantity, line.PurchaseUnitName, line.PurchaseIncrement);

  protected bool HasInvalidPurchasePackConfiguration(EditablePurchaseLine line)
    => HasInvalidPurchaseMultiple(line) || HasInvalidPurchaseAllocationMultiple(line);

  protected bool IsAutoPoRoomSelected(int roomId)
    => AutoPoSelectedRoomIds.Contains(roomId);

  protected void ToggleAutoPoRoomSelection(int roomId, bool isSelected)
  {
    if (isSelected)
    {
      AutoPoSelectedRoomIds.Add(roomId);
    }
    else
    {
      AutoPoSelectedRoomIds.Remove(roomId);
    }
  }

  protected void SeleccionarTodasLasSuitesAutoPo()
  {
    AutoPoSelectedRoomIds = AutoPoRoomOptions
      .Select(room => room.Id)
      .ToHashSet();
  }

  protected void LimpiarSeleccionSuitesAutoPo()
  {
    AutoPoSelectedRoomIds.Clear();
  }

  /// <summary>El escalón del renglón en palabras: "24.00 Rollo por Paquete", "1 Kilo".</summary>
  protected string GetPurchaseIncrementRequirement(EditablePurchaseLine line)
    => MaterialPurchaseIncrement.DescribeRequirement(
      line.BaseUnitName,
      line.PurchaseUnitName,
      line.PurchaseQuantity,
      line.PurchaseIncrement,
      CultureInfo.CurrentCulture);

  protected string? GetPurchaseAllocationValidationMessage(EditablePurchaseLine line, EditablePurchaseAllocation allocation)
  {
    if (!HasInvalidPurchaseAllocationMultiple(line, allocation))
    {
      return null;
    }

    var locationLabel = string.IsNullOrWhiteSpace(allocation.LocationCode)
      ? allocation.LocationName
      : allocation.LocationCode;

    return $"{locationLabel}: ajusta la cantidad a múltiplos de {GetPurchaseIncrementRequirement(line)}.";
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
      UiMessages.ShowError(Errors.ToUserMessage(ex, "cargar las órdenes de compra", new { Filter.VendorId, Filter.OpenOnly, Filter.SearchText }));
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
      LinkMaterialsToVendor = LinkMaterialsToVendor,
      Lines = Lines
        .Select(line => new PurchaseOrderLineUpsertRequest
        {
          Id = line.Id,
          MaterialId = line.MaterialId,
          BaseUnitPrice = line.BaseUnitPrice,
          PurchaseQuantitySnapshot = NormalizePurchaseQuantity(line.PurchaseQuantity),
          PurchaseUnitNameSnapshot = line.PurchaseUnitName,
          PurchaseIncrementSnapshot = MaterialPurchaseIncrement.Normalize(line.PurchaseIncrement),
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

  private void ResetAutoPoRequest(int? businessPartnerId = null)
  {
    AutoPoRequest = CreateAutoPoRequest(businessPartnerId);
    AutoPoSelectedRoomIds = [];
  }

  private static decimal GetDefaultPendingAllocationBaseQuantity(EditablePurchaseLine? line)
    => line is null
      ? 1m
      : PurchaseQuantityDisplay.ToBaseQuantity(
        1m,
        line.PurchaseQuantity,
        line.PurchaseUnitName);

  private IReadOnlyList<int> GetNormalizedAutoPoRoomIds()
  {
    var normalized = AutoPoSelectedRoomIds
      .Where(roomId => AutoPoRoomOptions.Any(room => room.Id == roomId))
      .OrderBy(roomId => roomId)
      .ToList();

    if (normalized.Count == 0)
    {
      return normalized;
    }

    return AutoPoRoomOptions.Count > 0 && normalized.Count == AutoPoRoomOptions.Count
      ? []
      : normalized;
  }

  private static decimal NormalizePurchaseQuantity(decimal value)
    => value > 0m ? value : 1m;

  private static string FormatQuantity(decimal value)
    => value.ToString("N2", CultureInfo.CurrentCulture);

  private string CurrentRfc => RfcState.RequireRfc();

  private static PurchaseOrderUpsertRequest CreateEditor()
    => new()
    {
      OrderDate = DateTime.Today
    };

  private static AutoPurchaseOrderCreateRequest CreateAutoPoRequest(int? businessPartnerId = null, IEnumerable<int>? roomIds = null)
    => new()
    {
      BusinessPartnerId = businessPartnerId.GetValueOrDefault(),
      OrderDate = DateTime.Today,
      RoomIds = NormalizeRoomIds(roomIds)
    };

  private static List<int> NormalizeRoomIds(IEnumerable<int>? roomIds)
    => roomIds?
      .Where(roomId => roomId > 0)
      .Distinct()
      .OrderBy(roomId => roomId)
      .ToList()
      ?? [];

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

    /// <summary>Escalón mínimo de compra vigente para este renglón. Ver <see cref="MaterialPurchaseIncrement"/>.</summary>
    public decimal PurchaseIncrement { get; set; } = MaterialPurchaseIncrement.WholePresentation;

    public decimal? BaseUnitPrice { get; set; }
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
    public decimal PurchaseQuantity { get; set; } = 1m;
    public string? PurchaseUnitName { get; set; }
    public decimal? BaseUnitPrice { get; set; }
    public int LocationId { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public string? LocationCode { get; set; }
    public decimal PlannedQuantity { get; set; }
    public decimal ReceivedQuantity { get; set; }
    public decimal RemainingQuantity => Math.Max(PlannedQuantity - ReceivedQuantity, 0m);
    public decimal ReceiveNowQuantity { get; set; }
    public decimal? TotalAmount { get; set; }
    public bool IncludesIva { get; set; }
  }
}
