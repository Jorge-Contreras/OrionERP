using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Logistica.Stock;

namespace OrionERP.Web.Features.Restaurante;

public partial class RestaurantWastePage
{
  private const long MaxEvidenceBytes = 10 * 1024 * 1024;
  private const int BackdateDayLimit = 7;
  private static readonly CultureInfo MoneyCulture = CultureInfo.GetCultureInfo("es-MX");

  /// <summary>Renglón en captura. La cantidad se lleva en positivo: el servicio la niega.</summary>
  private sealed class WasteEntry
  {
    public int MaterialId { get; set; }
    public long? MaterialLotId { get; set; }
    public decimal Quantity { get; set; }
  }

  private WasteWorkspaceDto workspace = new();
  private WasteHistoryDto history = new();
  private List<WasteEntry> entries = [new()];

  private int locationId;
  private string reasonCode = WasteReasonCatalog.Expired;
  private string reasonNote = string.Empty;
  private DateOnly occurredOn = DateOnly.FromDateTime(DateTime.Now);
  private byte[] evidence = [];
  private string evidenceFileName = string.Empty;
  private bool showConfirm;

  private WasteDocumentDto? reversalTarget;
  private string reversalReason = string.Empty;

  private DateOnly fromDate = DateOnly.FromDateTime(DateTime.Now).AddDays(-29);
  private DateOnly toDate = DateOnly.FromDateTime(DateTime.Now);
  private int filterLocationId;
  private string filterReasonCode = string.Empty;
  private string statusFilter = string.Empty;
  private int pageSize = 20;

  private bool isBusy;
  private string? message;
  private bool isError;

  private string CurrentRfc => RfcState.RequireRfc();
  private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
  private static DateOnly MinimumDate => Today.AddDays(-BackdateDayLimit);
  private bool NoteRequired => WasteReasonCatalog.Find(reasonCode)?.RequiresNote ?? false;

  private IEnumerable<InventoryBalanceOptionDto> LocationBalances => workspace.Balances
    .Where(balance => balance.LocationId == locationId && balance.AvailableQuantity > 0);

  private IReadOnlyList<RestaurantMaterialOption> MaterialOptions => LocationBalances
    .GroupBy(balance => balance.MaterialId)
    .Select(group => group.First())
    .Select(balance => new RestaurantMaterialOption(
      balance.MaterialId,
      balance.MaterialCode,
      balance.MaterialName,
      $"Disponible {balance.AvailableQuantity:0.####} {balance.UnitCode}"))
    .OrderBy(option => option.Description, StringComparer.CurrentCultureIgnoreCase)
    .ToList();

  private IReadOnlyList<InventoryLotOptionDto> LotOptions(int materialId)
    => materialId <= 0 || !TracksLots(materialId)
      ? []
      : workspace.Lots.Where(lot => lot.LocationId == locationId && lot.MaterialId == materialId).ToList();

  private bool TracksLots(int materialId)
    => workspace.Balances.Any(balance => balance.MaterialId == materialId && balance.TrackLots);

  private InventoryBalanceOptionDto? BalanceFor(int materialId)
    => workspace.Balances.FirstOrDefault(balance => balance.LocationId == locationId && balance.MaterialId == materialId);

  private string UnitFor(int materialId) => BalanceFor(materialId)?.UnitCode ?? string.Empty;

  /// <summary>Disponible del lote cuando hay lote, y del saldo de la ubicación cuando no.</summary>
  private decimal AvailableFor(WasteEntry entry)
  {
    if (entry.MaterialId <= 0) return 0;
    if (entry.MaterialLotId.HasValue)
      return workspace.Lots.FirstOrDefault(lot => lot.Id == entry.MaterialLotId.Value)?.AvailableQuantity ?? 0;
    return BalanceFor(entry.MaterialId)?.AvailableQuantity ?? 0;
  }

  private decimal CostFor(WasteEntry entry)
    => entry.Quantity * (BalanceFor(entry.MaterialId)?.AverageUnitCost ?? 0);

  private decimal TotalCost => entries.Sum(CostFor);
  private static bool HasQuantity(WasteEntry entry) => entry.MaterialId > 0 && entry.Quantity > 0;
  private static string QuantityText(WasteEntry entry)
    => entry.Quantity == 0 ? string.Empty : entry.Quantity.ToString("0.####", CultureInfo.InvariantCulture);

  private string LocationName => workspace.Locations
    .FirstOrDefault(location => location.Id == locationId)?.Name ?? "la ubicación";

  /// <summary>La fecha local si se capturó; si no, lo único que del documento viejo se sabe.</summary>
  private static DateTime DocumentDate(WasteDocumentDto document)
    => document.OccurredOn ?? document.CreatedAt;

  private static string ExpirationLabel(InventoryLotOptionDto lot)
    => lot.ExpirationDate.HasValue ? $" · cad. {lot.ExpirationDate:dd/MM/yy}" : string.Empty;

  /// <summary>Explica en una línea qué falta, en vez de dejar el botón muerto sin decir por qué.</summary>
  private bool CanSubmit => MissingSummary.Length == 0;

  private string MissingSummary
  {
    get
    {
      if (locationId <= 0) return "Elige la ubicación de donde sale el producto.";
      if (!WasteReasonCatalog.IsSelectable(reasonCode)) return "Elige el motivo de la merma.";
      if (!entries.Any(HasQuantity)) return "Captura al menos un material con cantidad.";
      var overdrawn = entries.FirstOrDefault(entry => HasQuantity(entry) && entry.Quantity > AvailableFor(entry));
      if (overdrawn is not null)
        return $"No puedes dar de baja más de lo disponible ({AvailableFor(overdrawn):0.####} {UnitFor(overdrawn.MaterialId)}).";
      var missingLot = entries.FirstOrDefault(entry =>
        HasQuantity(entry) && LotOptions(entry.MaterialId).Count > 0 && !entry.MaterialLotId.HasValue);
      if (missingLot is not null) return "Elige el lote del material que lo requiere.";
      if (NoteRequired && string.IsNullOrWhiteSpace(reasonNote))
        return "Ese motivo necesita que escribas qué pasó.";
      if (evidence.Length == 0) return "Adjunta la foto o el PDF de la evidencia.";
      return string.Empty;
    }
  }

  protected override async Task OnInitializedAsync() => await LoadAsync();

  private async Task LoadAsync()
  {
    isBusy = true;
    try
    {
      workspace = await WasteService.GetWorkspaceAsync(CurrentRfc);
      if (!workspace.Locations.Any(location => location.Id == locationId))
        locationId = workspace.Locations.Count == 1 ? workspace.Locations[0].Id : 0;
      ResetCapture();
      await LoadHistoryAsync();
    }
    catch (Exception ex) { Show(Errors.ToUserMessage(ex, "cargar la pantalla de merma"), true); }
    finally { isBusy = false; }
  }

  private async Task LoadHistoryAsync()
    => history = await WasteService.GetHistoryAsync(new WasteHistoryQuery
    {
      Rfc = CurrentRfc,
      FromDate = fromDate,
      ToDate = toDate,
      LocationId = filterLocationId > 0 ? filterLocationId : null,
      ReasonCode = string.IsNullOrWhiteSpace(filterReasonCode) ? null : filterReasonCode,
      Status = string.IsNullOrWhiteSpace(statusFilter) ? null : statusFilter,
      Page = 1,
      PageSize = pageSize
    });

  private async Task ReloadHistoryAsync()
  {
    isBusy = true;
    try { await LoadHistoryAsync(); }
    catch (Exception ex) { Show(Errors.ToUserMessage(ex, "consultar las mermas registradas"), true); }
    finally { isBusy = false; }
  }

  private async Task SetFromDateAsync(ChangeEventArgs args)
  {
    if (DateOnly.TryParse(args.Value?.ToString(), CultureInfo.InvariantCulture, out var value)) fromDate = value;
    await ReloadHistoryAsync();
  }

  private async Task SetToDateAsync(ChangeEventArgs args)
  {
    if (DateOnly.TryParse(args.Value?.ToString(), CultureInfo.InvariantCulture, out var value)) toDate = value;
    await ReloadHistoryAsync();
  }

  private async Task ShowPendingOnlyAsync()
  {
    statusFilter = statusFilter == WasteStatuses.PendingReview ? string.Empty : WasteStatuses.PendingReview;
    // La bandeja no sirve si el rango tapa lo que está esperando revisión.
    if (statusFilter == WasteStatuses.PendingReview) fromDate = Today.AddDays(-89);
    await ReloadHistoryAsync();
  }

  private async Task ShowMoreAsync()
  {
    pageSize += 20;
    await ReloadHistoryAsync();
  }

  private void ResetCapture()
  {
    entries = [new()];
    wasteCode = $"MM-{DateTime.Now:yyMMdd-HHmmss}";
    reasonCode = WasteReasonCatalog.Expired;
    reasonNote = string.Empty;
    occurredOn = Today;
    evidence = [];
    evidenceFileName = string.Empty;
    showConfirm = false;
  }

  private void OnLocationChanged() => entries = [new()];
  private static void OnMaterialChanged(WasteEntry entry) { entry.MaterialLotId = null; entry.Quantity = 0; }
  private static void OnLotChanged(WasteEntry entry) => entry.Quantity = 0;
  private void AddEntry() => entries.Add(new());
  private void RemoveEntry(WasteEntry entry) { if (entries.Count > 1) entries.Remove(entry); }
  private void TakeAll(WasteEntry entry) => entry.Quantity = AvailableFor(entry);

  private static void SetQuantity(WasteEntry entry, ChangeEventArgs args)
    => entry.Quantity = decimal.TryParse(args.Value?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
      ? Math.Max(0, value)
      : 0;

  private void SetOccurredOn(ChangeEventArgs args)
  {
    if (!DateOnly.TryParse(args.Value?.ToString(), CultureInfo.InvariantCulture, out var value)) return;
    occurredOn = value < MinimumDate ? MinimumDate : value > Today ? Today : value;
  }

  private async Task CaptureEvidenceAsync(InputFileChangeEventArgs args)
  {
    try
    {
      await using var stream = args.File.OpenReadStream(MaxEvidenceBytes);
      using var memory = new MemoryStream();
      await stream.CopyToAsync(memory);
      evidence = memory.ToArray();
      evidenceFileName = args.File.Name;
    }
    catch (Exception ex)
    {
      evidence = [];
      evidenceFileName = string.Empty;
      Show(Errors.ToUserMessage(ex, "adjuntar la evidencia", new { args.File.Name, args.File.Size }), true);
    }
  }

  /// <summary>
  /// Folio de la captura en curso. Se fija al limpiar el formulario y no al enviar, para que un
  /// doble clic reenvíe el mismo folio y el servicio lo reconozca en vez de duplicar la baja.
  /// </summary>
  private string wasteCode = string.Empty;

  private async Task PostAsync()
  {
    showConfirm = false;
    var request = new WasteCreateRequest
    {
      Rfc = CurrentRfc,
      WasteCode = wasteCode,
      ReasonCode = reasonCode,
      Reason = reasonNote,
      OccurredOn = occurredOn,
      Evidence = evidence,
      EvidenceFileName = evidenceFileName,
      Lines = entries.Where(HasQuantity).Select(entry => new WasteLineRequest
      {
        LocationId = locationId,
        MaterialId = entry.MaterialId,
        MaterialLotId = entry.MaterialLotId,
        Quantity = entry.Quantity
      }).ToList()
    };
    var posted = await RunAsync(
      async () => await WasteService.PostAsync(request, await ResolveActorAsync()),
      "registrar la merma",
      new { locationId, reasonCode, lines = request.Lines.Count });
    if (posted) await LoadAsync();
  }

  private async Task ApproveAsync(WasteDocumentDto document)
  {
    var approved = await RunAsync(
      async () => await WasteService.ApproveAsync(CurrentRfc, document.Id, await CurrentUserNameAsync()),
      "revisar la merma",
      new { document.Id, document.WasteCode });
    if (approved) await LoadAsync();
  }

  private void BeginReversal(WasteDocumentDto document)
  {
    reversalTarget = document;
    reversalReason = string.Empty;
  }

  private void CancelReversal()
  {
    reversalTarget = null;
    reversalReason = string.Empty;
  }

  private async Task ReverseAsync()
  {
    if (reversalTarget is null) return;
    var request = new WasteReversalRequest
    {
      Rfc = CurrentRfc,
      AdjustmentId = reversalTarget.Id,
      Reason = reversalReason
    };
    var reversed = await RunAsync(
      async () => await WasteService.ReverseAsync(request, await CurrentUserNameAsync()),
      "reversar la merma",
      new { reversalTarget.Id, reversalTarget.WasteCode });
    if (reversed)
    {
      CancelReversal();
      await LoadAsync();
    }
  }

  /// <summary>
  /// El componente corre en el servidor, así que el rango de supervisor se resuelve aquí con la
  /// política y no con nada que el navegador pueda mandar.
  /// </summary>
  private async Task<WasteActor> ResolveActorAsync()
  {
    var auth = await AuthenticationStateProvider.GetAuthenticationStateAsync();
    var isSupervisor = (await AuthorizationService.AuthorizeAsync(auth.User, "RestaurantAdmin")).Succeeded;
    return new WasteActor(auth.User.Identity?.Name ?? "merma", isSupervisor);
  }

  private async Task<string> CurrentUserNameAsync()
  {
    var auth = await AuthenticationStateProvider.GetAuthenticationStateAsync();
    return auth.User.Identity?.Name ?? "merma";
  }

  private async Task<bool> RunAsync(Func<Task<LogisticsCommandResult>> action, string operation, object? context)
  {
    isBusy = true;
    try
    {
      var result = await action();
      Show(result.Message, !result.Success);
      return result.Success;
    }
    catch (Exception ex)
    {
      // Los mensajes de negocio ya vienen en LogisticsCommandResult.Fail; esto traduce lo que
      // el servicio volvió a lanzar, sin filtrarle texto de SQL al operador.
      Show(Errors.ToUserMessage(ex, operation, context), true);
      return false;
    }
    finally { isBusy = false; }
  }

  private void Show(string value, bool error)
  {
    message = value;
    isError = error;
  }
}
