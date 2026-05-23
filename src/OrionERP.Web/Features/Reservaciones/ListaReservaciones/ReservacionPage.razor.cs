using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.JSInterop;
using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.Application.Features.Reservaciones.CalendarSync;
using OrionERP.Application.Features.Reservaciones.Cfdi;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;
using OrionERP.Web.Services;
using OrionERP.Web.State;

namespace OrionERP.Web.Features.Reservaciones.ListaReservaciones;

[Authorize(Roles = "Administrador,SatOperator")]
public partial class ReservacionPage : ComponentBase
{
  private const int ClienteSuggestionLimit = 5;

  private sealed record ReservationFormState(
    int? ClienteId,
    string ClienteSearchText,
    string SelectedClienteNombre,
    string? Status,
    DateTime? CheckIn,
    DateTime? CheckOut,
    bool Taxable,
    decimal SuiteDiscountPercent,
    string? RecommenedBy,
    string? Notes);

  [Parameter] public int ReservationId { get; set; }

  [Inject] public IListaReservacionesService ReservacionesService { get; set; } = default!;
  [Inject] public IBonhomiaRoomCalendarSyncService BonhomiaRoomCalendarSyncService { get; set; } = default!;
  [Inject] public ITransaccionService TransaccionService { get; set; } = default!;
  [Inject] public IUserRfcState RfcState { get; set; } = default!;
  [Inject] public IUiMessageService UiMessages { get; set; } = default!;
  [Inject] public IJSRuntime Js { get; set; } = default!;
  [Inject] public NavigationManager Nav { get; set; } = default!;
  [Inject] public IConfiguration Configuration { get; set; } = default!;
  [Inject] public IReservacionPdfService ReservacionPdfService { get; set; } = default!;
  [Inject] public IReservacionPdfDocumentFactory ReservacionPdfDocumentFactory { get; set; } = default!;

  internal ReservacionDetailDto? Detail { get; set; }
  internal List<ClienteOptionDto> Clientes { get; set; } = new();
  internal List<RoomOptionDto> Rooms { get; set; } = new();
  internal IReadOnlyList<ReservacionSuiteDto> Suites { get; set; } = Array.Empty<ReservacionSuiteDto>();
  internal List<SuiteDisponibleDto> SuitesDisponibles { get; set; } = new();
  internal IReadOnlyList<ReservacionExtraDto> Extras { get; set; } = Array.Empty<ReservacionExtraDto>();
  internal IReadOnlyList<ReservacionPagoDto> Pagos { get; set; } = Array.Empty<ReservacionPagoDto>();
  internal IReadOnlyList<ReservacionAttachmentDto> Attachments { get; set; } = Array.Empty<ReservacionAttachmentDto>();

  internal HashSet<int> SelectedSuiteIds { get; set; } = new();
  internal HashSet<int> SelectedSuiteDisponibleIds { get; set; } = new();

  internal int? ClienteId { get; set; }
  internal string ClienteSearchText { get; set; } = string.Empty;
  internal string SelectedClienteNombre { get; set; } = string.Empty;
  internal bool ShowClienteResults { get; set; }
  internal string? Status { get; set; }
  internal DateTime? CheckIn { get; set; }
  internal DateTime? CheckOut { get; set; }
  internal bool Taxable { get; set; }
  internal string? RecommenedBy { get; set; }
  internal string? Notes { get; set; }

  internal decimal TotalSuites { get; set; }
  internal decimal SuiteDiscountPercent { get; set; }
  internal decimal SuiteDiscountAmount { get; set; }
  internal decimal TotalExtras { get; set; }
  internal decimal SubTotal { get; set; }
  internal decimal Tax { get; set; }
  internal decimal Ish { get; set; }
  internal decimal TotalReservacion { get; set; }
  internal decimal TotalPagado { get; set; }
  internal decimal PorPagar { get; set; }
  internal int NumNoches { get; set; }

  internal decimal PrecioSuiteInput { get; set; }
  internal decimal PrecioSuiteConIvaInput { get; set; }
  internal decimal TotalSuiteInput { get; set; }
  internal decimal SuiteActionValueInput { get; set; }
  internal string SelectedSuiteAction { get; set; } = SuiteActionPrice;

  internal int? EditingExtraId { get; set; }
  internal int? ExtraRoomId { get; set; }
  internal decimal ExtraPrice { get; set; }
  internal decimal ExtraDiscount { get; set; }
  internal string? ExtraNotes { get; set; }

  internal string AttachmentDescription { get; set; } = string.Empty;
  internal bool ShowSuitePicker { get; set; }
  internal bool ShowExtraForm { get; set; }
  internal ReservationEditorTab ActiveTab { get; set; } = ReservationEditorTab.Suites;
  internal bool IsLoading { get; set; }
  internal bool IsSaving { get; set; }
  internal bool IsWorking { get; set; }
  internal bool IsCreatingPoliza { get; set; }
  internal bool IsGeneratingPdf { get; set; }
  internal bool IsDeletingReservation { get; set; }
  internal bool IsUploadingAttachment { get; set; }
  internal bool IsApplyingAirbnb { get; set; }
  internal string? ErrorMessage { get; set; }
  internal string? AirbnbErrorMessage { get; set; }

  private IBrowserFile? _pendingAttachment;
  private int _attachmentInputKey;
  private int? _attachmentDownloadingId;
  private int? _attachmentDeletingId;
  private bool _airbnbDefaultsLoaded;
  private bool _roomsLoaded;
  private bool _hasPendingCalendarSync;
  internal int AttachmentInputKey => _attachmentInputKey;

  internal const string SuiteActionPrice = "price";
  internal const string SuiteActionPriceWithIva = "price-with-iva";
  internal const string SuiteActionTotal = "total";
  internal const string SuiteActionCleaning = "cleaning";
  internal const string SuiteActionAirbnb = "airbnb";

  internal bool ShowAirbnbPanel { get; set; }
  internal decimal AirbnbPayoutInput { get; set; }
  internal decimal AirbnbCleaningFeeInput { get; set; } = AirbnbReservationDefaults.CleaningFee;
  internal decimal AirbnbIvaRatePercentInput { get; set; } = RateToPercent(AirbnbReservationDefaults.IvaRate);
  internal decimal AirbnbIvaRetentionRatePercentInput { get; set; } = RateToPercent(AirbnbReservationDefaults.IvaRetentionRate);
  internal decimal AirbnbIsrRetentionRatePercentInput { get; set; } = RateToPercent(AirbnbReservationDefaults.IsrRetentionRate);
  internal decimal AirbnbHostServiceFeeRatePercentInput { get; set; } = RateToPercent(AirbnbReservationDefaults.HostServiceFeeRate);
  internal decimal AirbnbHostServiceFeeIvaRatePercentInput { get; set; } = RateToPercent(AirbnbReservationDefaults.HostServiceFeeIvaRate);

  internal IReadOnlyList<string> StatusOptions { get; } = new[] { "NUEVA", "PAGADA", "Cancelada" };
  internal IReadOnlyList<(string Value, string Label)> SuiteActionOptions { get; } = new[]
  {
    (SuiteActionPrice, "Precio"),
    (SuiteActionPriceWithIva, "Precio c/IVA"),
    (SuiteActionTotal, "Total"),
    (SuiteActionCleaning, "Limpieza"),
    (SuiteActionAirbnb, "Airbnb")
  };

  internal decimal ExtraDiscountAmount
    => ExtraDiscount > 0 ? decimal.Round(ExtraPrice * (ExtraDiscount / 100m), 2, MidpointRounding.ToEven) : 0m;

  internal decimal ExtraTotal
    => decimal.Round(ExtraPrice - ExtraDiscountAmount, 2, MidpointRounding.ToEven);

  internal bool HasActiveSuiteDiscount
    => SuiteDiscountPercent > 1m && SuiteDiscountAmount > 0m;

  internal bool IsEditingExtra => EditingExtraId.HasValue;

  internal string CheckInText
    => CheckIn?.ToString("yyyy-MM-dd") ?? string.Empty;

  internal string CheckOutText
    => CheckOut?.ToString("yyyy-MM-dd") ?? string.Empty;

  internal string SuiteActionPlaceholder
    => SelectedSuiteAction switch
    {
      SuiteActionPrice => "Precio",
      SuiteActionPriceWithIva => "Precio c/IVA",
      SuiteActionTotal => "Total",
      SuiteActionCleaning => "Sin monto",
      SuiteActionAirbnb => "Tú ganas",
      _ => "Valor"
    };

  internal bool HasAirbnbBreakdown => Detail?.AirbnbBreakdown is not null;

  internal int AirbnbTargetSuiteCount
    => GetAirbnbTargetSuiteIds().Length;

  protected override async Task OnParametersSetAsync()
  {
    await LoadAllAsync();
  }

  internal async Task LoadAllAsync(bool preserveFormState = false)
  {
    EnsureAirbnbDefaultsLoaded();
    IsLoading = true;
    ErrorMessage = null;
    var formState = preserveFormState ? CaptureFormState() : null;

    try
    {
      Detail = await ReservacionesService.GetReservacionDetailAsync(ReservationId);
      if (Detail is null)
      {
        return;
      }

      if (formState is null)
      {
        ApplyDetailToForm();
      }
      else
      {
        RestoreFormState(formState);
      }

      Clientes.Clear();
      await EnsureRoomsLoadedAsync();

      Suites = Detail.Suites;
      Extras = Detail.Extras;
      Pagos = Detail.Pagos;
      Attachments = Detail.Attachments;
      await LoadReservationFacturacionStatusAsync();

      SelectedSuiteIds.Clear();
      SelectedSuiteDisponibleIds.Clear();
      EnsureValidDateRange();
      RecalculateTotals();
    }
    catch (Exception ex)
    {
      ErrorMessage = ex.Message;
      UiMessages.ShowError($"No se pudo cargar la reservación. {ex.Message}");
    }
    finally
    {
      IsLoading = false;
    }
  }

  internal async Task GuardarAsync()
  {
    await SaveReservationStateAsync(showSuccessMessage: true);
  }

  internal void SetActiveTab(ReservationEditorTab tab)
  {
    ActiveTab = tab;
  }

  internal async Task CerrarAsync()
  {
    if (IsSaving || IsWorking)
    {
      return;
    }

    Nav.NavigateTo("/reservaciones/lista");
    await Task.CompletedTask;
  }

  internal async Task AbrirPdfAsync()
  {
    if (Detail is null || IsGeneratingPdf)
    {
      return;
    }

    EnsureValidDateRange();
    RecalculateTotals();

    IsGeneratingPdf = true;

    try
    {
      var document = BuildPdfDocument();
      var pdfBytes = ReservacionPdfService.Generate(document);
      var fileName = $"reservacion-{ReservationId.ToString("D6", CultureInfo.InvariantCulture)}.pdf";
      var dataUrl = $"data:application/pdf;base64,{Convert.ToBase64String(pdfBytes)}";

      await Js.InvokeVoidAsync("triggerFileDownload", fileName, dataUrl);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo generar el PDF. {ex.Message}");
    }
    finally
    {
      IsGeneratingPdf = false;
    }
  }

  internal async Task BorrarReservacionAsync()
  {
    if (Detail is null || IsDeletingReservation)
      return;

    if (Pagos.Count > 0 || Attachments.Count > 0)
    {
      UiMessages.ShowWarning("No se puede borrar la reservación porque tiene pagos o archivos adjuntos.");
      return;
    }

    var confirm = await Js.InvokeAsync<bool>(
      "confirm",
      "¿Borrar esta reservación? Se quitarán sus suites y extras. Esta acción no se puede deshacer.");

    if (!confirm)
    {
      return;
    }

    IsDeletingReservation = true;
    try
    {
      var result = await ReservacionesService.DeleteReservationAsync(Detail.Id);
      if (!result.Success)
      {
        UiMessages.ShowWarning(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      Nav.NavigateTo("/reservaciones/lista");
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo borrar la reservación. {ex.Message}");
    }
    finally
    {
      IsDeletingReservation = false;
    }
  }

  internal async Task CrearPolizaAsync()
  {
    if (Detail is null)
      return;

    if (string.IsNullOrWhiteSpace(RfcState.CurrentRfc))
    {
      UiMessages.ShowError("Selecciona un RFC antes de crear la póliza.");
      return;
    }

    IsCreatingPoliza = true;
    try
    {
      var saveOk = await SaveReservationStateAsync(showSuccessMessage: false);
      if (!saveOk || Detail is null)
      {
        return;
      }

      var cliente = string.IsNullOrWhiteSpace(Detail.Cliente)
        ? "(Sin cliente)"
        : Detail.Cliente;

      var createResult = await TransaccionService.CreateTransaccionAsync(new TransaccionCreateRequest
      {
        Rfc = RfcState.CurrentRfc!,
        Fecha = DateTime.Now,
        Concepto = $"PAGO POR RESERVACION#{Detail.Id} - {cliente}",
        CategoriaId = 19,
        Monto = TotalReservacion,
        Cuenta = "ORION HABITAT DE MEXICO",
        TipoPoliza = "INGRESO",
        FormaPago = "03"
      });

      if (!createResult.Success || createResult.NewTransaccionId <= 0)
      {
        UiMessages.ShowError(createResult.Message ?? "No se pudo crear la póliza.");
        return;
      }

      var linkResult = await TransaccionService.UpsertReservacionLinkAsync(new TransaccionReservacionLinkUpsertRequest
      {
        ReservationId = Detail.Id,
        TransaccionId = createResult.NewTransaccionId,
        Amount = TotalReservacion
      });

      if (!linkResult.Success)
      {
        UiMessages.ShowError(
          $"La póliza {createResult.NewTransaccionId} se creó, pero no se pudo ligar a la reservación. {linkResult.Message}");
        return;
      }

      var appliedAirbnbAccounting = false;
      if (Detail.AirbnbBreakdown is not null)
      {
        var accountingResult = await ReservationCfdiService.ApplyAirbnbAccountingAsync(
          new ReservationAirbnbAccountingRequest
          {
            ReservationId = Detail.Id,
            TransaccionId = createResult.NewTransaccionId,
            IssuerRfc = RfcState.CurrentRfc!
          });

        if (!accountingResult.Success)
        {
          UiMessages.ShowError(
            $"La póliza {createResult.NewTransaccionId} se creó y se ligó a la reservación, pero no se pudo generar el registro contable Airbnb. {accountingResult.Message}");
          return;
        }

        appliedAirbnbAccounting = true;
      }

      var url = $"/contabilidad/transacciones/{createResult.NewTransaccionId}";
      try
      {
        await Js.InvokeVoidAsync("open", url, "_blank", "noopener,noreferrer");
      }
      catch
      {
        Nav.NavigateTo(url);
      }

      UiMessages.ShowSuccess(appliedAirbnbAccounting
        ? $"Póliza {createResult.NewTransaccionId} creada, ligada y con registro contable Airbnb."
        : $"Póliza {createResult.NewTransaccionId} creada.");
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo crear la póliza. {ex.Message}");
    }
    finally
    {
      IsCreatingPoliza = false;
    }
  }

  private async Task<bool> SaveReservationStateAsync(bool showSuccessMessage)
  {
    if (Detail is null)
      return false;

    EnsureValidDateRange();
    RecalculateTotals();

    IsSaving = true;
    try
    {
      var calendarBefore = ReservationCalendarSyncDecision.FromDetail(Detail);
      var clienteReady = await EnsureClienteReadyForSaveAsync();
      if (!clienteReady)
      {
        return false;
      }

      var calendarAfter = ReservationCalendarSyncDecision.FromForm(ClienteId, Status, CheckIn, CheckOut);
      var shouldSyncCalendar = ReservationCalendarSyncDecision.ShouldSync(
        calendarBefore,
        calendarAfter,
        _hasPendingCalendarSync);

      var saveResult = await ReservacionesService.SaveReservationAsync(new ReservacionUpdateRequest
      {
        Id = Detail.Id,
        ClienteId = ClienteId,
        CheckIn = CheckIn,
        CheckOut = CheckOut,
        Status = Status,
        RecommenedBy = RecommenedBy,
        Notes = Notes,
        Taxable = Taxable,
        TotalPrice = TotalReservacion,
        SuiteDiscountPercent = SuiteDiscountPercent
      });

      if (!saveResult.Success)
      {
        UiMessages.ShowError(saveResult.Message);
        return false;
      }

      if (shouldSyncCalendar)
      {
        await ReservacionesService.SyncSuiteStatusAsync(Detail.Id, Status);
        await ReservacionesService.SyncSuiteLockedByAsync(Detail.Id, ClienteId);
        await SyncConAirbnbAsync();
        _hasPendingCalendarSync = false;
      }

      await LoadAllAsync();

      if (showSuccessMessage)
      {
        UiMessages.ShowSuccess(saveResult.Message);
      }

      return true;
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo guardar la reservación. {ex.Message}");
      return false;
    }
    finally
    {
      IsSaving = false;
    }
  }

  private async Task EnsureRoomsLoadedAsync()
  {
    if (_roomsLoaded && Rooms.Count > 0)
    {
      return;
    }

    Rooms = (await ReservacionesService.GetRoomsForExtrasAsync()).ToList();
    _roomsLoaded = true;
  }

  private async Task SyncConAirbnbAsync()
  {
    try
    {
      var today = DateTime.Today;
      var endDateExclusive = new DateTime(today.Year + 1, 1, 1);
      var result = await BonhomiaRoomCalendarSyncService.SyncAsync(today, endDateExclusive);

      if (result.ErrorCount <= 0)
      {
        return;
      }

      var summary = BuildSyncSummary(result);
      if (result.ErrorCount >= result.Rooms.Count && result.Rooms.Count > 0)
      {
        UiMessages.ShowError(summary);
      }
      else
      {
        UiMessages.ShowWarning(summary);
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowWarning($"La reservación se guardó, pero no se pudo sincronizar Outlook/Airbnb. {ex.Message}");
    }
  }

  private static string BuildSyncSummary(BonhomiaRoomCalendarSyncResult result)
  {
    var summary = $"Sync Outlook/Airbnb: {result.CreatedCount} creados, {result.UpdatedCount} actualizados, {result.DeletedCount} borrados, {result.SkippedCount} sin cambios.";
    if (result.RecoveredMappingCount > 0)
    {
      summary += $" {result.RecoveredMappingCount} mapeos recuperados.";
    }

    var errorRooms = result.Rooms
      .Where(item => !string.IsNullOrWhiteSpace(item.ErrorMessage))
      .Select(item => item.RoomName)
      .ToArray();

    return errorRooms.Length > 0
      ? $"{summary} Con errores en: {string.Join(", ", errorRooms)}."
      : $"{summary} Se detectaron {result.ErrorCount} errores.";
  }

  internal async Task OnStatusChangedAsync(ChangeEventArgs args)
  {
    Status = args.Value?.ToString();
    if (Detail is null)
      return;

    await ReservacionesService.SyncSuiteStatusAsync(Detail.Id, Status);
    await RefreshSuitesAsync();
  }

  internal async Task OnClienteInputChangedAsync(ChangeEventArgs args)
  {
    ClienteSearchText = args.Value?.ToString() ?? string.Empty;
    if (!string.Equals(NormalizeClienteNombre(ClienteSearchText), NormalizeClienteNombre(SelectedClienteNombre), StringComparison.OrdinalIgnoreCase))
    {
      ClienteId = null;
      SelectedClienteNombre = string.Empty;
    }

    await RefreshClienteMatchesAsync(allowEmptySearch: false);
  }

  internal async Task OnClienteInputKeyDownAsync(KeyboardEventArgs args)
  {
    if (!IsClienteSearchTriggerKey(args))
    {
      return;
    }

    await RefreshClienteMatchesAsync(allowEmptySearch: true);
  }

  internal async Task SelectClienteAsync(ClienteOptionDto cliente)
  {
    await ApplyClienteSelectionAsync(cliente);

    if (Detail is null)
      return;

    await ReservacionesService.SyncSuiteLockedByAsync(Detail.Id, ClienteId);
    await RefreshSuitesAsync();
  }

  internal Task OnCheckInChangedAsync(ChangeEventArgs args)
  {
    CheckIn = ParseDate(args.Value?.ToString());
    EnsureValidDateRange();
    RecalculateTotals();
    return Task.CompletedTask;
  }

  internal Task OnCheckOutChangedAsync(ChangeEventArgs args)
  {
    CheckOut = ParseDate(args.Value?.ToString());
    EnsureValidDateRange();
    RecalculateTotals();
    return Task.CompletedTask;
  }

  internal Task ToggleTaxableAsync(ChangeEventArgs args)
  {
    Taxable = args.Value is bool b && b;
    RecalculateTotals();
    return Task.CompletedTask;
  }

  private static DateTime? ParseDate(string? value)
  {
    if (string.IsNullOrWhiteSpace(value))
      return null;

    return DateTime.TryParse(value, out var parsed) ? parsed.Date : null;
  }

  internal static string FormatDate(DateTime? value)
    => value.HasValue ? value.Value.ToString("d", CultureInfo.CurrentCulture) : string.Empty;

  internal static string FormatCurrency(decimal value)
    => value.ToString("C", CultureInfo.CurrentCulture);

  internal static string FormatPercent(decimal value)
    => value.ToString("0.##", CultureInfo.CurrentCulture);

  private static bool TryParseDiscountPercent(string value, out decimal percent)
  {
    value = value.Trim().TrimEnd('%').Trim();

    return decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out percent)
      || decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out percent);
  }

  internal bool IsAttachmentDownloading(ReservacionAttachmentDto attachment)
    => _attachmentDownloadingId == attachment.Id;

  internal bool IsAttachmentDeleting(ReservacionAttachmentDto attachment)
    => _attachmentDeletingId == attachment.Id;

  internal async Task RefreshSuitesAsync()
  {
    if (Detail is null)
      return;

    Suites = await ReservacionesService.GetSuitesByReservationAsync(Detail.Id);
    RecalculateTotals();
  }

  internal void ToggleSuiteSelection(int id, bool isSelected)
  {
    if (isSelected)
    {
      SelectedSuiteIds.Add(id);
    }
    else
    {
      SelectedSuiteIds.Remove(id);
    }
  }

  internal void SeleccionarTodasSuites()
  {
    SelectedSuiteIds = Suites.Select(s => s.Id).ToHashSet();
  }

  internal void ToggleSuiteDisponibleSelection(int id, bool isSelected)
  {
    if (isSelected)
    {
      SelectedSuiteDisponibleIds.Add(id);
    }
    else
    {
      SelectedSuiteDisponibleIds.Remove(id);
    }
  }

  internal async Task OpenSuitePickerAsync()
  {
    if (!CheckIn.HasValue || !CheckOut.HasValue)
    {
      UiMessages.ShowWarning("Captura CHECKIN y CHECKOUT para buscar suites.");
      return;
    }

    EnsureValidDateRange();
    SuitesDisponibles = (await ReservacionesService.GetSuitesDisponiblesAsync(CheckIn.Value, CheckOut!.Value)).ToList();
    SelectedSuiteDisponibleIds.Clear();
    ShowSuitePicker = true;
  }

  internal void CancelSuitePicker()
  {
    ShowSuitePicker = false;
    SelectedSuiteDisponibleIds.Clear();
  }

  internal async Task AddSuitesSeleccionadasAsync()
  {
    if (Detail is null || SelectedSuiteDisponibleIds.Count == 0)
    {
      return;
    }

    var selectedBlocked = SuitesDisponibles.Any(s => SelectedSuiteDisponibleIds.Contains(s.Id) && s.IsLocked);
    if (selectedBlocked)
    {
      UiMessages.ShowWarning("Hay suites seleccionadas que ya están bloqueadas.");
      return;
    }

    IsWorking = true;
    try
    {
      var clienteNombre = Clientes.FirstOrDefault(c => c.Id == ClienteId)?.Nombre ?? Detail.Cliente;
      var result = await ReservacionesService.AddSuitesToReservationAsync(
        ReservationId,
        Status,
        clienteNombre,
        SelectedSuiteDisponibleIds.ToArray());

      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      ShowSuitePicker = false;
      _hasPendingCalendarSync = true;
      await RefreshSuitesAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudieron agregar suites. {ex.Message}");
    }
    finally
    {
      IsWorking = false;
    }
  }

  internal async Task EliminarSuitesSeleccionadasAsync()
  {
    if (SelectedSuiteIds.Count == 0)
    {
      UiMessages.ShowWarning("Selecciona al menos una suite.");
      return;
    }

    var confirm = await Js.InvokeAsync<bool>("confirm", "¿Eliminar las suites seleccionadas de la reservación?");
    if (!confirm)
    {
      return;
    }

    IsWorking = true;
    try
    {
      var result = await ReservacionesService.DeleteSuitesAsync(SelectedSuiteIds.ToArray());
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      SelectedSuiteIds.Clear();
      _hasPendingCalendarSync = true;
      await RefreshSuitesAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudieron eliminar suites. {ex.Message}");
    }
    finally
    {
      IsWorking = false;
    }
  }

  internal async Task AplicarPrecioSuiteAsync()
  {
    if (SelectedSuiteIds.Count == 0)
    {
      UiMessages.ShowWarning("Selecciona al menos una suite.");
      return;
    }

    var result = await ReservacionesService.SetSuitesPriceAsync(SelectedSuiteIds.ToArray(), PrecioSuiteInput);
    if (result.Success)
    {
      await HandleNonAirbnbSuiteActionSuccessAsync(result);
    }
    else
    {
      UiMessages.ShowError(result.Message);
    }
  }

  internal async Task AplicarPrecioConIvaSuiteAsync()
  {
    if (SelectedSuiteIds.Count == 0)
    {
      UiMessages.ShowWarning("Selecciona al menos una suite.");
      return;
    }

    var result = await ReservacionesService.SetSuitesPriceWithIvaAsync(SelectedSuiteIds.ToArray(), PrecioSuiteConIvaInput);
    if (result.Success)
    {
      await HandleNonAirbnbSuiteActionSuccessAsync(result);
    }
    else
    {
      UiMessages.ShowError(result.Message);
    }
  }

  internal async Task AlternarLimpiezaSuiteAsync()
  {
    if (SelectedSuiteIds.Count == 0)
    {
      UiMessages.ShowWarning("Selecciona al menos una suite.");
      return;
    }

    var suiteBase = Suites.FirstOrDefault(s => SelectedSuiteIds.Contains(s.Id));
    var nextState = suiteBase is null || !suiteBase.LimpiezaProfunda;

    var result = await ReservacionesService.ToggleSuitesLimpiezaAsync(SelectedSuiteIds.ToArray(), nextState);
    if (result.Success)
    {
      await HandleNonAirbnbSuiteActionSuccessAsync(result);
    }
    else
    {
      UiMessages.ShowError(result.Message);
    }
  }

  internal async Task DistribuirTotalSuitesAsync()
  {
    if (SelectedSuiteIds.Count == 0)
    {
      UiMessages.ShowWarning("Selecciona al menos una suite.");
      return;
    }

    var result = await ReservacionesService.DistributeSuitesTotalWithIvaAsync(SelectedSuiteIds.ToArray(), TotalSuiteInput);
    if (result.Success)
    {
      await HandleNonAirbnbSuiteActionSuccessAsync(result);
    }
    else
    {
      UiMessages.ShowError(result.Message);
    }
  }

  internal async Task ApplySuiteActionAsync()
  {
    PrecioSuiteInput = SuiteActionValueInput;
    PrecioSuiteConIvaInput = SuiteActionValueInput;
    TotalSuiteInput = SuiteActionValueInput;

    switch (SelectedSuiteAction)
    {
      case SuiteActionPrice:
        await AplicarPrecioSuiteAsync();
        break;
      case SuiteActionPriceWithIva:
        await AplicarPrecioConIvaSuiteAsync();
        break;
      case SuiteActionTotal:
        await DistribuirTotalSuitesAsync();
        break;
      case SuiteActionCleaning:
        await AlternarLimpiezaSuiteAsync();
        break;
      case SuiteActionAirbnb:
        OpenAirbnbPanel();
        break;
      default:
        UiMessages.ShowWarning("Selecciona una acción válida.");
        break;
    }
  }

  private async Task HandleNonAirbnbSuiteActionSuccessAsync(ReservacionCommandResult actionResult)
  {
    UiMessages.ShowSuccess(actionResult.Message);
    await ClearAirbnbBreakdownAfterManualSuiteActionAsync();
    await LoadAllAsync(preserveFormState: true);
  }

  private async Task ClearAirbnbBreakdownAfterManualSuiteActionAsync()
  {
    if (Detail?.AirbnbBreakdown is null)
    {
      return;
    }

    var result = await ReservacionesService.ClearAirbnbBreakdownIfNoPolizaAsync(Detail.Id);
    if (!result.Success)
    {
      UiMessages.ShowWarning(result.Message);
      return;
    }

    if (result.Message.Contains("eliminado", StringComparison.OrdinalIgnoreCase))
    {
      UiMessages.ShowSuccess(result.Message);
    }
    else if (result.Message.Contains("conservado", StringComparison.OrdinalIgnoreCase))
    {
      UiMessages.ShowWarning(result.Message);
    }
  }

  internal void OpenAirbnbPanel()
  {
    if (Suites.Count == 0)
    {
      UiMessages.ShowWarning("Agrega suites a la reservación antes de aplicar Airbnb.");
      return;
    }

    if (AirbnbPayoutInput <= 0m && SuiteActionValueInput > 0m)
    {
      AirbnbPayoutInput = SuiteActionValueInput;
    }

    ShowAirbnbPanel = true;
    AirbnbErrorMessage = null;
  }

  internal void CloseAirbnbPanel()
  {
    ShowAirbnbPanel = false;
    AirbnbErrorMessage = null;
  }

  internal async Task AplicarAirbnbAsync()
  {
    if (Detail is null || IsApplyingAirbnb)
    {
      return;
    }

    if (!TryCalculateAirbnbPreview(out _, out var errorMessage))
    {
      AirbnbErrorMessage = errorMessage;
      UiMessages.ShowWarning(errorMessage ?? "Revisa el desglose Airbnb.");
      return;
    }

    var targetSuiteIds = GetAirbnbTargetSuiteIds();
    if (targetSuiteIds.Length == 0)
    {
      AirbnbErrorMessage = "Selecciona al menos una suite para aplicar el desglose Airbnb.";
      UiMessages.ShowWarning(AirbnbErrorMessage);
      return;
    }

    IsApplyingAirbnb = true;
    AirbnbErrorMessage = null;

    try
    {
      var result = await ReservacionesService.ApplyAirbnbBreakdownAsync(new AirbnbReservationBreakdownApplyRequest
      {
        ReservationId = Detail.Id,
        RoomCalendarIds = targetSuiteIds,
        PayoutAmount = AirbnbPayoutInput,
        CleaningFee = AirbnbCleaningFeeInput,
        IvaRate = PercentToRate(AirbnbIvaRatePercentInput),
        IvaRetentionRate = PercentToRate(AirbnbIvaRetentionRatePercentInput),
        IsrRetentionRate = PercentToRate(AirbnbIsrRetentionRatePercentInput),
        HostServiceFeeRate = PercentToRate(AirbnbHostServiceFeeRatePercentInput),
        HostServiceFeeIvaRate = PercentToRate(AirbnbHostServiceFeeIvaRatePercentInput)
      });

      if (!result.Success)
      {
        AirbnbErrorMessage = result.Message;
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      ShowAirbnbPanel = false;
      SelectedSuiteIds.Clear();
      await LoadAllAsync();
    }
    catch (Exception ex)
    {
      AirbnbErrorMessage = ex.Message;
      UiMessages.ShowError($"No se pudo aplicar Airbnb. {ex.Message}");
    }
    finally
    {
      IsApplyingAirbnb = false;
    }
  }

  internal AirbnbReservationBreakdownDto? GetAirbnbPreview()
    => TryCalculateAirbnbPreview(out var preview, out _) ? preview : null;

  internal string? GetAirbnbPreviewError()
    => TryCalculateAirbnbPreview(out _, out var errorMessage) ? null : errorMessage;

  internal async Task AbrirPagoAsync(int transaccionId)
  {
    var url = $"/contabilidad/transacciones/{transaccionId}";
    await Js.InvokeVoidAsync("open", url, "_blank", "noopener,noreferrer");
  }

  internal void RecalculateTotals()
  {
    var totals = ReservacionTotalsCalculator.Calculate(
      CheckIn,
      CheckOut,
      Taxable,
      Suites.Select(s => s.Precio),
      Extras.Select(e => e.DiscountedPrice),
      Pagos.Sum(p => p.Monto),
      SuiteDiscountPercent);

    TotalSuites = totals.TotalSuites;
    SuiteDiscountPercent = totals.SuiteDiscountPercent;
    SuiteDiscountAmount = totals.SuiteDiscountAmount;
    TotalExtras = totals.TotalExtras;
    SubTotal = totals.SubTotal;
    Tax = totals.Tax;
    Ish = totals.Ish;
    TotalReservacion = totals.TotalReservacion;
    TotalPagado = totals.TotalPagado;
    PorPagar = totals.PorPagar;
    NumNoches = totals.NumNoches;

    TotalSuiteInput = TotalSuites;
  }

  internal async Task AplicarDescuentoSuitesAsync()
  {
    var currentValue = SuiteDiscountPercent > 0m
      ? SuiteDiscountPercent.ToString("0.##", CultureInfo.CurrentCulture)
      : "0";

    var input = await Js.InvokeAsync<string?>(
      "prompt",
      "Porcentaje de descuento para suites (0 para quitar):",
      currentValue);

    if (input is null)
    {
      return;
    }

    if (!TryParseDiscountPercent(input, out var discountPercent))
    {
      UiMessages.ShowWarning("Captura un porcentaje válido para el descuento.");
      return;
    }

    if (discountPercent < 0m || discountPercent > 100m || (discountPercent > 0m && discountPercent <= 1m))
    {
      UiMessages.ShowWarning("El descuento debe ser 0 para quitarlo, o mayor a 1% y menor o igual a 100%.");
      return;
    }

    SuiteDiscountPercent = ReservacionTotalsCalculator.NormalizeSuiteDiscountPercent(discountPercent);
    RecalculateTotals();
  }

  private async Task<bool> EnsureClienteReadyForSaveAsync()
  {
    var clienteNombre = NormalizeClienteNombre(ClienteSearchText);
    var resolvedCliente = await ReservacionesService.ResolveClienteAsync(ClienteId, clienteNombre);

    if (resolvedCliente is not null)
    {
      await ApplyClienteSelectionAsync(resolvedCliente, reloadMatches: false);
      return true;
    }

    if (string.IsNullOrWhiteSpace(clienteNombre))
    {
      UiMessages.ShowWarning("Captura o selecciona un cliente antes de guardar.");
      return false;
    }

    var confirm = await Js.InvokeAsync<bool>(
      "confirm",
      $"No existe el cliente '{clienteNombre}'. ¿Deseas crearlo para guardar la reservación?");

    if (!confirm)
    {
      UiMessages.ShowWarning("Selecciona un cliente válido antes de guardar.");
      return false;
    }

    var createdCliente = await ReservacionesService.CreateClienteAsync(clienteNombre);
    await ApplyClienteSelectionAsync(createdCliente, reloadMatches: false);
    UiMessages.ShowSuccess($"Cliente {createdCliente.Nombre} creado.");
    return true;
  }

  private Task ApplyClienteSelectionAsync(ClienteOptionDto cliente, bool reloadMatches = true)
  {
    var clienteNombre = NormalizeClienteNombre(cliente.Nombre);

    ClienteId = cliente.Id;
    SelectedClienteNombre = clienteNombre;
    ClienteSearchText = clienteNombre;
    ShowClienteResults = false;
    Clientes.Clear();

    return Task.CompletedTask;
  }

  private async Task RefreshClienteMatchesAsync(bool allowEmptySearch)
  {
    var searchText = NormalizeClienteNombre(ClienteSearchText);
    if (!allowEmptySearch && string.IsNullOrWhiteSpace(searchText))
    {
      ShowClienteResults = false;
      Clientes.Clear();
      return;
    }

    Clientes = await LoadClientesAsync(searchText);
    ShowClienteResults = allowEmptySearch || !string.IsNullOrWhiteSpace(searchText);
  }

  private async Task<List<ClienteOptionDto>> LoadClientesAsync(string? searchText)
  {
    var clientes = (await ReservacionesService.GetClientesAsync(searchText, ClienteSuggestionLimit)).ToList();

    if (ClienteId.HasValue && clientes.All(c => c.Id != ClienteId.Value))
    {
      var selectedName = !string.IsNullOrWhiteSpace(SelectedClienteNombre)
        ? SelectedClienteNombre
        : ClienteSearchText;

      if (!string.IsNullOrWhiteSpace(selectedName))
      {
        clientes.Insert(0, new ClienteOptionDto
        {
          Id = ClienteId.Value,
          Nombre = selectedName.Trim()
        });
      }
    }

    return clientes
      .GroupBy(c => c.Id)
      .Select(g => g.First())
      .OrderBy(c => c.Nombre)
      .ToList();
  }

  private static bool IsClienteSearchTriggerKey(KeyboardEventArgs args)
    => string.Equals(args.Key, "Enter", StringComparison.Ordinal)
      || string.Equals(args.Key, "NumpadEnter", StringComparison.Ordinal)
      || string.Equals(args.Key, " ", StringComparison.Ordinal)
      || string.Equals(args.Key, "Space", StringComparison.Ordinal)
      || string.Equals(args.Key, "Spacebar", StringComparison.Ordinal)
      || string.Equals(args.Code, "Space", StringComparison.Ordinal);

  private static string NormalizeClienteNombre(string? clienteNombre)
  {
    if (string.IsNullOrWhiteSpace(clienteNombre))
    {
      return string.Empty;
    }

    var normalized = clienteNombre.Trim();
    return string.Equals(normalized, "(Sin cliente)", StringComparison.OrdinalIgnoreCase)
      ? string.Empty
      : normalized;
  }

  private ReservationFormState CaptureFormState()
    => new(
      ClienteId,
      ClienteSearchText,
      SelectedClienteNombre,
      Status,
      CheckIn,
      CheckOut,
      Taxable,
      SuiteDiscountPercent,
      RecommenedBy,
      Notes);

  private void RestoreFormState(ReservationFormState formState)
  {
    ClienteId = formState.ClienteId;
    ClienteSearchText = formState.ClienteSearchText;
    SelectedClienteNombre = formState.SelectedClienteNombre;
    ShowClienteResults = false;
    Status = formState.Status;
    CheckIn = formState.CheckIn?.Date;
    CheckOut = formState.CheckOut?.Date;
    Taxable = formState.Taxable;
    SuiteDiscountPercent = formState.SuiteDiscountPercent;
    RecommenedBy = formState.RecommenedBy;
    Notes = formState.Notes;
    ApplyAirbnbBreakdownToInputs();
  }

  private void ApplyDetailToForm()
  {
    if (Detail is null)
    {
      return;
    }

    var clienteNombreActual = NormalizeClienteNombre(Detail.ClienteId.HasValue ? Detail.Cliente : null);
    ClienteId = Detail.ClienteId;
    ClienteSearchText = clienteNombreActual;
    SelectedClienteNombre = clienteNombreActual;
    ShowClienteResults = false;
    Status = Detail.Status;
    CheckIn = Detail.CheckIn?.Date;
    CheckOut = Detail.CheckOut?.Date;
    Taxable = Detail.Taxable;
    SuiteDiscountPercent = Detail.SuiteDiscountPercent;
    RecommenedBy = Detail.RecommenedBy;
    Notes = Detail.Notes;
    ApplyAirbnbBreakdownToInputs();
  }

  private void ApplyAirbnbBreakdownToInputs()
  {
    if (Detail?.AirbnbBreakdown is null)
    {
      return;
    }

    var breakdown = Detail.AirbnbBreakdown;
    AirbnbPayoutInput = breakdown.PayoutAmount;
    AirbnbCleaningFeeInput = breakdown.CleaningFee;
    AirbnbIvaRatePercentInput = RateToPercent(breakdown.IvaRate);
    AirbnbIvaRetentionRatePercentInput = RateToPercent(breakdown.IvaRetentionRate);
    AirbnbIsrRetentionRatePercentInput = RateToPercent(breakdown.IsrRetentionRate);
    AirbnbHostServiceFeeRatePercentInput = RateToPercent(breakdown.HostServiceFeeRate);
    AirbnbHostServiceFeeIvaRatePercentInput = RateToPercent(breakdown.HostServiceFeeIvaRate);
  }

  private void EnsureAirbnbDefaultsLoaded()
  {
    if (_airbnbDefaultsLoaded)
    {
      return;
    }

    AirbnbCleaningFeeInput = ReadAirbnbDecimal("CleaningFee", AirbnbReservationDefaults.CleaningFee);
    AirbnbIvaRatePercentInput = RateToPercent(ReadAirbnbDecimal("IvaRate", AirbnbReservationDefaults.IvaRate));
    AirbnbIvaRetentionRatePercentInput = RateToPercent(ReadAirbnbDecimal("IvaRetentionRate", AirbnbReservationDefaults.IvaRetentionRate));
    AirbnbIsrRetentionRatePercentInput = RateToPercent(ReadAirbnbDecimal("IsrRetentionRate", AirbnbReservationDefaults.IsrRetentionRate));
    AirbnbHostServiceFeeRatePercentInput = RateToPercent(ReadAirbnbDecimal("HostServiceFeeRate", AirbnbReservationDefaults.HostServiceFeeRate));
    AirbnbHostServiceFeeIvaRatePercentInput = RateToPercent(ReadAirbnbDecimal("HostServiceFeeIvaRate", AirbnbReservationDefaults.HostServiceFeeIvaRate));
    _airbnbDefaultsLoaded = true;
  }

  private decimal ReadAirbnbDecimal(string key, decimal defaultValue)
  {
    var value = Configuration[$"Reservations:Airbnb:{key}"];
    return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
      ? parsed
      : defaultValue;
  }

  private bool TryCalculateAirbnbPreview(out AirbnbReservationBreakdownDto? preview, out string? errorMessage)
  {
    preview = null;
    errorMessage = null;

    try
    {
      preview = AirbnbReservationBreakdownCalculator.Calculate(new AirbnbReservationBreakdownInput
      {
        PayoutAmount = AirbnbPayoutInput,
        CleaningFee = AirbnbCleaningFeeInput,
        IvaRate = PercentToRate(AirbnbIvaRatePercentInput),
        IvaRetentionRate = PercentToRate(AirbnbIvaRetentionRatePercentInput),
        IsrRetentionRate = PercentToRate(AirbnbIsrRetentionRatePercentInput),
        HostServiceFeeRate = PercentToRate(AirbnbHostServiceFeeRatePercentInput),
        HostServiceFeeIvaRate = PercentToRate(AirbnbHostServiceFeeIvaRatePercentInput)
      });

      return true;
    }
    catch (Exception ex)
    {
      errorMessage = ex.Message;
      return false;
    }
  }

  private int[] GetAirbnbTargetSuiteIds()
  {
    if (SelectedSuiteIds.Count > 0)
    {
      return SelectedSuiteIds
        .Where(id => Suites.Any(suite => suite.Id == id))
        .OrderBy(id => id)
        .ToArray();
    }

    return Suites
      .Select(suite => suite.Id)
      .OrderBy(id => id)
      .ToArray();
  }

  private static decimal PercentToRate(decimal percent)
    => percent / 100m;

  private static decimal RateToPercent(decimal rate)
    => decimal.Round(rate * 100m, 4, MidpointRounding.ToEven);

  private void EnsureValidDateRange()
  {
    if (CheckIn.HasValue && CheckOut.HasValue && CheckIn.Value.Date >= CheckOut.Value.Date)
    {
      CheckOut = CheckIn.Value.Date.AddDays(1);
    }
  }

  private ReservacionPdfDocumentModel BuildPdfDocument()
  {
    var cliente = NormalizeClienteNombre(ClienteSearchText);

    return ReservacionPdfDocumentFactory.CreateFromSnapshot(new ReservacionPdfSnapshot(
      ReservationId,
      string.IsNullOrWhiteSpace(cliente) ? Detail?.Cliente ?? string.Empty : cliente,
      Status ?? Detail?.Status ?? string.Empty,
      CheckIn ?? Detail?.CheckIn,
      CheckOut ?? Detail?.CheckOut,
      RecommenedBy ?? Detail?.RecommenedBy,
      Taxable,
      Notes ?? Detail?.Notes,
      TotalSuites,
      SuiteDiscountPercent,
      SuiteDiscountAmount,
      TotalExtras,
      SubTotal,
      Tax,
      Ish,
      TotalReservacion,
      TotalPagado,
      PorPagar,
      Suites,
      Extras,
      Pagos,
      Attachments));
  }
}
