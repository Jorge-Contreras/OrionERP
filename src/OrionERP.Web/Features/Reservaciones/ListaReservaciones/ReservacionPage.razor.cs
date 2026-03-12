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
using Microsoft.JSInterop;
using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;
using OrionERP.Web.Services;
using OrionERP.Web.State;

namespace OrionERP.Web.Features.Reservaciones.ListaReservaciones;

[Authorize(Roles = "Administrador,SatOperator")]
public partial class ReservacionPage : ComponentBase
{
  private sealed record ReservationFormState(
    int? ClienteId,
    string ClienteSearchText,
    string SelectedClienteNombre,
    string? Status,
    DateTime? CheckIn,
    DateTime? CheckOut,
    bool Taxable,
    string? RecommenedBy,
    string? Notes);

  [Parameter] public int ReservationId { get; set; }

  [Inject] public IListaReservacionesService ReservacionesService { get; set; } = default!;
  [Inject] public ITransaccionService TransaccionService { get; set; } = default!;
  [Inject] public IUserRfcState RfcState { get; set; } = default!;
  [Inject] public IUiMessageService UiMessages { get; set; } = default!;
  [Inject] public IJSRuntime Js { get; set; } = default!;
  [Inject] public NavigationManager Nav { get; set; } = default!;
  [Inject] public IReservacionPdfService ReservacionPdfService { get; set; } = default!;

  protected ReservacionDetailDto? Detail { get; set; }
  protected List<ClienteOptionDto> Clientes { get; set; } = new();
  protected List<RoomOptionDto> Rooms { get; set; } = new();
  protected List<ReservacionSuiteDto> Suites { get; set; } = new();
  protected List<SuiteDisponibleDto> SuitesDisponibles { get; set; } = new();
  protected List<ReservacionExtraDto> Extras { get; set; } = new();
  protected List<ReservacionPagoDto> Pagos { get; set; } = new();
  protected List<ReservacionAttachmentDto> Attachments { get; set; } = new();

  protected HashSet<int> SelectedSuiteIds { get; set; } = new();
  protected HashSet<int> SelectedSuiteDisponibleIds { get; set; } = new();

  protected int? ClienteId { get; set; }
  protected string ClienteSearchText { get; set; } = string.Empty;
  protected string SelectedClienteNombre { get; set; } = string.Empty;
  protected bool ShowClienteResults { get; set; }
  protected string? Status { get; set; }
  protected DateTime? CheckIn { get; set; }
  protected DateTime? CheckOut { get; set; }
  protected bool Taxable { get; set; }
  protected string? RecommenedBy { get; set; }
  protected string? Notes { get; set; }

  protected decimal TotalSuites { get; set; }
  protected decimal TotalExtras { get; set; }
  protected decimal SubTotal { get; set; }
  protected decimal Tax { get; set; }
  protected decimal Ish { get; set; }
  protected decimal TotalReservacion { get; set; }
  protected decimal TotalPagado { get; set; }
  protected decimal PorPagar { get; set; }
  protected int NumNoches { get; set; }

  protected decimal PrecioSuiteInput { get; set; }
  protected decimal PrecioSuiteConIvaInput { get; set; }
  protected decimal DescuentoSuiteInput { get; set; }
  protected decimal TotalSuiteInput { get; set; }
  protected decimal SuiteActionValueInput { get; set; }
  protected string SelectedSuiteAction { get; set; } = SuiteActionPrice;

  protected int? EditingExtraId { get; set; }
  protected int? ExtraRoomId { get; set; }
  protected decimal ExtraPrice { get; set; }
  protected decimal ExtraDiscount { get; set; }
  protected string? ExtraNotes { get; set; }

  protected string AttachmentDescription { get; set; } = string.Empty;
  protected bool ShowSuitePicker { get; set; }
  protected bool ShowExtraForm { get; set; }
  protected bool IsLoading { get; set; }
  protected bool IsSaving { get; set; }
  protected bool IsWorking { get; set; }
  protected bool IsCreatingPoliza { get; set; }
  protected bool IsGeneratingPdf { get; set; }
  protected bool IsUploadingAttachment { get; set; }
  protected string? ErrorMessage { get; set; }

  private IBrowserFile? _pendingAttachment;
  private int _attachmentInputKey;
  private int? _attachmentDownloadingId;
  private int? _attachmentDeletingId;
  private const string SuiteActionPrice = "price";
  private const string SuiteActionPriceWithIva = "price-with-iva";
  private const string SuiteActionDiscount = "discount";
  private const string SuiteActionTotal = "total";
  private const string SuiteActionCleaning = "cleaning";

  protected IReadOnlyList<string> StatusOptions { get; } = new[] { "NUEVA", "PAGADA", "Cancelada" };
  protected IReadOnlyList<(string Value, string Label)> SuiteActionOptions { get; } = new[]
  {
    (SuiteActionPrice, "Precio"),
    (SuiteActionPriceWithIva, "Precio c/IVA"),
    (SuiteActionDiscount, "Descuento %"),
    (SuiteActionTotal, "Total"),
    (SuiteActionCleaning, "Limpieza")
  };

  protected decimal ExtraDiscountAmount
    => ExtraDiscount > 0 ? decimal.Round(ExtraPrice * (ExtraDiscount / 100m), 2, MidpointRounding.ToEven) : 0m;

  protected decimal ExtraTotal
    => decimal.Round(ExtraPrice - ExtraDiscountAmount, 2, MidpointRounding.ToEven);

  protected bool IsEditingExtra => EditingExtraId.HasValue;

  protected string CheckInText
    => CheckIn?.ToString("yyyy-MM-dd") ?? string.Empty;

  protected string CheckOutText
    => CheckOut?.ToString("yyyy-MM-dd") ?? string.Empty;

  protected string SuiteActionPlaceholder
    => SelectedSuiteAction switch
    {
      SuiteActionPrice => "Precio",
      SuiteActionPriceWithIva => "Precio c/IVA",
      SuiteActionDiscount => "% Desc.",
      SuiteActionTotal => "Total",
      SuiteActionCleaning => "Sin monto",
      _ => "Valor"
    };

  protected override async Task OnParametersSetAsync()
  {
    await LoadAllAsync();
  }

  protected async Task LoadAllAsync(bool preserveFormState = false)
  {
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

      Clientes = await LoadClientesAsync(ClienteSearchText);
      Rooms = (await ReservacionesService.GetRoomsForExtrasAsync()).ToList();

      Suites = Detail.Suites.ToList();
      Extras = Detail.Extras.ToList();
      Pagos = Detail.Pagos.ToList();
      Attachments = Detail.Attachments.ToList();

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

  protected async Task GuardarAsync()
  {
    await SaveReservationStateAsync(showSuccessMessage: true);
  }

  protected async Task CerrarAsync()
  {
    if (IsSaving || IsWorking)
    {
      return;
    }

    Nav.NavigateTo("/reservaciones/lista");
    await Task.CompletedTask;
  }

  protected async Task AbrirPdfAsync()
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

  protected async Task CrearPolizaAsync()
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

      var url = $"/contabilidad/transacciones/{createResult.NewTransaccionId}";
      try
      {
        await Js.InvokeVoidAsync("open", url, "_blank", "noopener,noreferrer");
      }
      catch
      {
        Nav.NavigateTo(url);
      }

      UiMessages.ShowSuccess($"Póliza {createResult.NewTransaccionId} creada.");
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
      var clienteReady = await EnsureClienteReadyForSaveAsync();
      if (!clienteReady)
      {
        return false;
      }

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
        TotalPrice = TotalReservacion
      });

      if (!saveResult.Success)
      {
        UiMessages.ShowError(saveResult.Message);
        return false;
      }

      await ReservacionesService.SyncSuiteStatusAsync(Detail.Id, Status);
      await ReservacionesService.SyncSuiteLockedByAsync(Detail.Id, ClienteId);
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

  protected async Task OnStatusChangedAsync(ChangeEventArgs args)
  {
    Status = args.Value?.ToString();
    if (Detail is null)
      return;

    await ReservacionesService.SyncSuiteStatusAsync(Detail.Id, Status);
    await RefreshSuitesAsync();
  }

  protected void OnClienteInputChanged(ChangeEventArgs args)
  {
    ClienteSearchText = args.Value?.ToString() ?? string.Empty;
    if (!string.Equals(NormalizeClienteNombre(ClienteSearchText), NormalizeClienteNombre(SelectedClienteNombre), StringComparison.OrdinalIgnoreCase))
    {
      ClienteId = null;
      SelectedClienteNombre = string.Empty;
    }
    ShowClienteResults = false;
  }

  protected async Task OnClienteInputKeyDownAsync(KeyboardEventArgs args)
  {
    if (!IsClienteSearchTriggerKey(args))
    {
      return;
    }

    Clientes = await LoadClientesAsync(ClienteSearchText);
    ShowClienteResults = true;
  }

  protected async Task SelectClienteAsync(ClienteOptionDto cliente)
  {
    await ApplyClienteSelectionAsync(cliente);

    if (Detail is null)
      return;

    await ReservacionesService.SyncSuiteLockedByAsync(Detail.Id, ClienteId);
    await RefreshSuitesAsync();
  }

  protected Task OnCheckInChangedAsync(ChangeEventArgs args)
  {
    CheckIn = ParseDate(args.Value?.ToString());
    EnsureValidDateRange();
    RecalculateTotals();
    return Task.CompletedTask;
  }

  protected Task OnCheckOutChangedAsync(ChangeEventArgs args)
  {
    CheckOut = ParseDate(args.Value?.ToString());
    EnsureValidDateRange();
    RecalculateTotals();
    return Task.CompletedTask;
  }

  protected Task ToggleTaxableAsync(ChangeEventArgs args)
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

  protected static string FormatDate(DateTime? value)
    => value.HasValue ? value.Value.ToString("d", CultureInfo.CurrentCulture) : string.Empty;

  protected static string FormatCurrency(decimal value)
    => value.ToString("C", CultureInfo.CurrentCulture);

  protected bool IsAttachmentDownloading(ReservacionAttachmentDto attachment)
    => _attachmentDownloadingId == attachment.Id;

  protected bool IsAttachmentDeleting(ReservacionAttachmentDto attachment)
    => _attachmentDeletingId == attachment.Id;

  protected async Task RefreshSuitesAsync()
  {
    if (Detail is null)
      return;

    Suites = (await ReservacionesService.GetSuitesByReservationAsync(Detail.Id)).ToList();
    RecalculateTotals();
  }

  protected void ToggleSuiteSelection(int id, bool isSelected)
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

  protected void ToggleSuiteDisponibleSelection(int id, bool isSelected)
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

  protected async Task OpenSuitePickerAsync()
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

  protected void CancelSuitePicker()
  {
    ShowSuitePicker = false;
    SelectedSuiteDisponibleIds.Clear();
  }

  protected async Task AddSuitesSeleccionadasAsync()
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
      await LoadAllAsync(preserveFormState: true);
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

  protected async Task EliminarSuitesSeleccionadasAsync()
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
      await LoadAllAsync(preserveFormState: true);
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

  protected async Task AplicarPrecioSuiteAsync()
  {
    if (SelectedSuiteIds.Count == 0)
    {
      UiMessages.ShowWarning("Selecciona al menos una suite.");
      return;
    }

    var result = await ReservacionesService.SetSuitesPriceAsync(SelectedSuiteIds.ToArray(), PrecioSuiteInput);
    if (result.Success)
    {
      UiMessages.ShowSuccess(result.Message);
      await LoadAllAsync(preserveFormState: true);
    }
    else
    {
      UiMessages.ShowError(result.Message);
    }
  }

  protected async Task AplicarPrecioConIvaSuiteAsync()
  {
    if (SelectedSuiteIds.Count == 0)
    {
      UiMessages.ShowWarning("Selecciona al menos una suite.");
      return;
    }

    var result = await ReservacionesService.SetSuitesPriceWithIvaAsync(SelectedSuiteIds.ToArray(), PrecioSuiteConIvaInput);
    if (result.Success)
    {
      UiMessages.ShowSuccess(result.Message);
      await LoadAllAsync(preserveFormState: true);
    }
    else
    {
      UiMessages.ShowError(result.Message);
    }
  }

  protected async Task AplicarDescuentoSuiteAsync()
  {
    if (SelectedSuiteIds.Count == 0)
    {
      UiMessages.ShowWarning("Selecciona al menos una suite.");
      return;
    }

    var result = await ReservacionesService.ApplySuitesDiscountAsync(SelectedSuiteIds.ToArray(), DescuentoSuiteInput);
    if (result.Success)
    {
      UiMessages.ShowSuccess(result.Message);
      await LoadAllAsync(preserveFormState: true);
    }
    else
    {
      UiMessages.ShowError(result.Message);
    }
  }

  protected async Task AlternarLimpiezaSuiteAsync()
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
      UiMessages.ShowSuccess(result.Message);
      await LoadAllAsync(preserveFormState: true);
    }
    else
    {
      UiMessages.ShowError(result.Message);
    }
  }

  protected async Task DistribuirTotalSuitesAsync()
  {
    var result = await ReservacionesService.DistributeSuitesTotalAsync(ReservationId, TotalSuiteInput);
    if (result.Success)
    {
      UiMessages.ShowSuccess(result.Message);
      await LoadAllAsync(preserveFormState: true);
    }
    else
    {
      UiMessages.ShowError(result.Message);
    }
  }

  protected async Task ApplySuiteActionAsync()
  {
    PrecioSuiteInput = SuiteActionValueInput;
    PrecioSuiteConIvaInput = SuiteActionValueInput;
    DescuentoSuiteInput = SuiteActionValueInput;
    TotalSuiteInput = SuiteActionValueInput;

    switch (SelectedSuiteAction)
    {
      case SuiteActionPrice:
        await AplicarPrecioSuiteAsync();
        break;
      case SuiteActionPriceWithIva:
        await AplicarPrecioConIvaSuiteAsync();
        break;
      case SuiteActionDiscount:
        await AplicarDescuentoSuiteAsync();
        break;
      case SuiteActionTotal:
        await DistribuirTotalSuitesAsync();
        break;
      case SuiteActionCleaning:
        await AlternarLimpiezaSuiteAsync();
        break;
      default:
        UiMessages.ShowWarning("Selecciona una acción válida.");
        break;
    }
  }

  protected void ToggleExtraForm()
  {
    if (ShowExtraForm && !IsEditingExtra)
    {
      ResetExtraEditor();
      return;
    }

    StartNewExtra();
  }

  protected void EditExtra(ReservacionExtraDto extra)
  {
    EditingExtraId = extra.Id;
    ExtraRoomId = extra.RoomId;
    ExtraPrice = extra.Price;
    ExtraDiscount = extra.Discount;
    ExtraNotes = extra.Notes;
    ShowExtraForm = true;
  }

  protected void OnExtraRoomChanged(ChangeEventArgs args)
  {
    if (!int.TryParse(args.Value?.ToString(), out var id))
    {
      ExtraRoomId = null;
      return;
    }

    ExtraRoomId = id;
    var room = Rooms.FirstOrDefault(r => r.Id == id);
    if (room is not null)
    {
      ExtraPrice = room.BasePrice;
    }
  }

  protected async Task GuardarExtraAsync()
  {
    if (!ExtraRoomId.HasValue)
    {
      UiMessages.ShowWarning("Selecciona una suite para el extra.");
      return;
    }

    ReservacionCommandResult result;
    if (IsEditingExtra)
    {
      result = await ReservacionesService.UpdateExtraAsync(new ReservacionExtraUpdateRequest
      {
        Id = EditingExtraId!.Value,
        ReservationId = ReservationId,
        RoomId = ExtraRoomId.Value,
        Price = ExtraPrice,
        Discount = ExtraDiscount,
        DiscountedPrice = ExtraTotal,
        Notes = ExtraNotes
      });
    }
    else
    {
      result = await ReservacionesService.AddExtraAsync(new ReservacionExtraCreateRequest
      {
        ReservationId = ReservationId,
        RoomId = ExtraRoomId.Value,
        Price = ExtraPrice,
        Discount = ExtraDiscount,
        DiscountedPrice = ExtraTotal,
        Notes = ExtraNotes
      });
    }

    if (result.Success)
    {
      UiMessages.ShowSuccess(result.Message);
      ResetExtraEditor();
      await LoadAllAsync(preserveFormState: true);
    }
    else
    {
      UiMessages.ShowError(result.Message);
    }
  }

  protected async Task EliminarExtraAsync(int extraId)
  {
    var confirm = await Js.InvokeAsync<bool>("confirm", "¿Eliminar el extra seleccionado?");
    if (!confirm)
    {
      return;
    }

    var result = await ReservacionesService.DeleteExtraAsync(extraId);
    if (result.Success)
    {
      if (EditingExtraId == extraId)
      {
        ResetExtraEditor();
      }

      UiMessages.ShowSuccess(result.Message);
      await LoadAllAsync(preserveFormState: true);
    }
    else
    {
      UiMessages.ShowError(result.Message);
    }
  }

  protected void CancelExtraEdit()
  {
    ResetExtraEditor();
  }

  protected async Task AbrirPagoAsync(int transaccionId)
  {
    var url = $"/contabilidad/transacciones/{transaccionId}";
    await Js.InvokeVoidAsync("open", url, "_blank", "noopener,noreferrer");
  }

  protected async Task OnAttachmentSelectedAsync(InputFileChangeEventArgs args)
  {
    _pendingAttachment = args.FileCount > 0 ? args.File : null;
    await InvokeAsync(StateHasChanged);
  }

  protected async Task CargarAttachmentAsync()
  {
    if (_pendingAttachment is null)
    {
      UiMessages.ShowWarning("Selecciona un archivo.");
      return;
    }

    if (string.IsNullOrWhiteSpace(AttachmentDescription))
    {
      UiMessages.ShowWarning("Ingresa una descripción para el archivo.");
      return;
    }

    if (_pendingAttachment.Size > ReservacionAttachmentCreateRequest.MaxFileSizeBytes)
    {
      UiMessages.ShowError("El archivo excede el tamaño máximo permitido (5 MB).");
      return;
    }

    IsUploadingAttachment = true;
    try
    {
      await using var stream = _pendingAttachment.OpenReadStream(ReservacionAttachmentCreateRequest.MaxFileSizeBytes);
      using var ms = new MemoryStream();
      await stream.CopyToAsync(ms);

      var extension = Path.GetExtension(_pendingAttachment.Name)?.TrimStart('.');
      await ReservacionesService.AddAttachmentAsync(new ReservacionAttachmentCreateRequest
      {
        ReservationId = ReservationId,
        FileName = _pendingAttachment.Name,
        Extension = extension,
        Description = AttachmentDescription.Trim(),
        Content = ms.ToArray()
      });

      AttachmentDescription = string.Empty;
      _pendingAttachment = null;
      _attachmentInputKey++;
      await RefreshAttachmentsAsync();
      UiMessages.ShowSuccess("Archivo agregado.");
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cargar el archivo. {ex.Message}");
    }
    finally
    {
      IsUploadingAttachment = false;
    }
  }

  protected async Task DescargarAttachmentAsync(ReservacionAttachmentDto attachment)
  {
    _attachmentDownloadingId = attachment.Id;
    try
    {
      var content = await ReservacionesService.GetAttachmentContentAsync(attachment.Id);
      if (content is null || content.Bytes.Length == 0)
      {
        UiMessages.ShowError("No se encontró el contenido del archivo.");
        return;
      }

      var dataUrl = $"data:{content.ContentType};base64,{Convert.ToBase64String(content.Bytes)}";
      await Js.InvokeVoidAsync("triggerFileDownload", content.FileName, dataUrl);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo descargar el archivo. {ex.Message}");
    }
    finally
    {
      _attachmentDownloadingId = null;
    }
  }

  protected async Task EliminarAttachmentAsync(ReservacionAttachmentDto attachment)
  {
    var confirm = await Js.InvokeAsync<bool>("confirm", "¿Eliminar el archivo seleccionado?");
    if (!confirm)
    {
      return;
    }

    _attachmentDeletingId = attachment.Id;
    try
    {
      await ReservacionesService.DeleteAttachmentAsync(attachment.Id);
      await RefreshAttachmentsAsync();
      UiMessages.ShowSuccess("Archivo eliminado.");
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo eliminar el archivo. {ex.Message}");
    }
    finally
    {
      _attachmentDeletingId = null;
    }
  }

  protected async Task RefreshAttachmentsAsync()
  {
    Attachments = (await ReservacionesService.GetAttachmentsAsync(ReservationId)).ToList();
  }

  protected void RecalculateTotals()
  {
    TotalSuites = decimal.Round(Suites.Sum(s => s.Precio), 2, MidpointRounding.ToEven);
    TotalExtras = decimal.Round(Extras.Sum(e => e.DiscountedPrice), 2, MidpointRounding.ToEven);
    SubTotal = decimal.Round(TotalSuites + TotalExtras, 2, MidpointRounding.ToEven);

    Tax = 0m;
    Ish = 0m;
    if (Taxable)
    {
      Tax = decimal.Round(SubTotal * 0.16m, 2, MidpointRounding.ToEven);
      if (CheckIn.HasValue && CheckIn.Value.Year < 2025)
      {
        Ish = decimal.Round(SubTotal * 0.02m, 2, MidpointRounding.ToEven);
      }
    }

    TotalReservacion = decimal.Round(SubTotal + Tax + Ish, 2, MidpointRounding.ToEven);
    TotalPagado = decimal.Round(Pagos.Sum(p => p.Monto), 2, MidpointRounding.ToEven);
    PorPagar = decimal.Round(TotalReservacion - TotalPagado, 2, MidpointRounding.ToEven);

    NumNoches = 0;
    if (CheckIn.HasValue && CheckOut.HasValue && CheckOut.Value.Date >= CheckIn.Value.Date)
    {
      NumNoches = (int)(CheckOut.Value.Date - CheckIn.Value.Date).TotalDays;
    }

    TotalSuiteInput = TotalSuites;
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

  private async Task ApplyClienteSelectionAsync(ClienteOptionDto cliente, bool reloadMatches = true)
  {
    var clienteNombre = NormalizeClienteNombre(cliente.Nombre);

    ClienteId = cliente.Id;
    SelectedClienteNombre = clienteNombre;
    ClienteSearchText = clienteNombre;
    ShowClienteResults = false;

    if (reloadMatches)
    {
      Clientes = await LoadClientesAsync(clienteNombre);
    }
  }

  private async Task<List<ClienteOptionDto>> LoadClientesAsync(string? searchText)
  {
    var clientes = (await ReservacionesService.GetClientesAsync(searchText)).ToList();

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

  private void StartNewExtra()
  {
    EditingExtraId = null;
    ExtraRoomId = null;
    ExtraPrice = 0m;
    ExtraDiscount = 0m;
    ExtraNotes = null;
    ShowExtraForm = true;
  }

  private void ResetExtraEditor()
  {
    EditingExtraId = null;
    ExtraRoomId = null;
    ExtraPrice = 0m;
    ExtraDiscount = 0m;
    ExtraNotes = null;
    ShowExtraForm = false;
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
    RecommenedBy = formState.RecommenedBy;
    Notes = formState.Notes;
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
    RecommenedBy = Detail.RecommenedBy;
    Notes = Detail.Notes;
  }

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

    return new ReservacionPdfDocumentModel(
      ReservationId,
      string.IsNullOrWhiteSpace(cliente) ? Detail?.Cliente ?? string.Empty : cliente,
      Status ?? Detail?.Status ?? string.Empty,
      FormatDate(CheckIn ?? Detail?.CheckIn),
      FormatDate(CheckOut ?? Detail?.CheckOut),
      NumNoches.ToString(CultureInfo.CurrentCulture),
      RecommenedBy ?? Detail?.RecommenedBy ?? string.Empty,
      Taxable ? "Si" : "No",
      Notes ?? Detail?.Notes ?? string.Empty,
      DateTime.Now.ToString("f", CultureInfo.CurrentCulture),
      FormatCurrency(TotalSuites),
      FormatCurrency(TotalExtras),
      FormatCurrency(SubTotal),
      FormatCurrency(Tax),
      FormatCurrency(Ish),
      FormatCurrency(TotalReservacion),
      FormatCurrency(TotalPagado),
      FormatCurrency(PorPagar),
      Suites.Select(suite => new ReservacionPdfSuiteRow(
          suite.Fecha.ToShortDateString(),
          suite.Suite,
          FormatCurrency(suite.Precio),
          suite.LimpiezaProfunda ? "Si" : "No"))
        .ToList(),
      Extras.Select(extra => new ReservacionPdfExtraRow(
          extra.RoomName,
          extra.RoomDescription,
          FormatCurrency(extra.Price),
          extra.Discount.ToString(CultureInfo.CurrentCulture),
          FormatCurrency(extra.DiscountedPrice),
          extra.Notes ?? string.Empty))
        .ToList(),
      Pagos.Select(pago => new ReservacionPdfPagoRow(
          pago.TransaccionId.ToString(CultureInfo.CurrentCulture),
          pago.Fecha.ToShortDateString(),
          FormatCurrency(pago.Monto),
          pago.Concepto))
        .ToList(),
      Attachments.Select(attachment => new ReservacionPdfAttachmentRow(
          attachment.AttachmentName,
          attachment.AttachmentExtension,
          attachment.AttachmentDescription ?? string.Empty,
          attachment.Length.ToString("N0", CultureInfo.CurrentCulture)))
        .ToList());
  }
}
