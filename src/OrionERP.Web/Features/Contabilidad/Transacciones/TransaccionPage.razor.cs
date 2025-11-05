using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.Application.Features.Cfdi.ContabilidadRegistros;
using OrionERP.Web.Components.CuentasContables;
using OrionERP.Web.Services;
using OrionERP.Web.State;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace OrionERP.Web.Features.Contabilidad.Transacciones;

public partial class TransaccionPage : ComponentBase, IDisposable
{
  private CancellationTokenSource? _loadCts;
  private TransaccionHeaderModel? _headerOriginal;
  private MovimientoModel? _movimientoTarget;
  private int? _attachmentDownloadingId;
  private CuentasContablesPicker? cuentaPicker;
  private CuentasContablesSelection? _cuentaSelectionOriginal;
  private int? cuentaDirectInput;

  private bool _isDisposed;

  [Parameter] public int Id { get; set; }

  [Inject] public ITransaccionService TransaccionService { get; set; } = default!;
  [Inject] public IUiMessageService UiMessages { get; set; } = default!;
  [Inject] public IUserRfcState RfcState { get; set; } = default!;
  [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;
  [Inject] public IJSRuntime JsRuntime { get; set; } = default!;
  [Inject] public ICuentasContablesRepository CuentasRepository { get; set; } = default!;

  protected TransaccionHeaderModel? Header { get; private set; }
  protected EditContext? HeaderEditContext { get; private set; }
  protected bool IsLoading { get; private set; } = true;
  protected bool IsSavingHeader { get; private set; }
  protected string? ErrorMessage { get; private set; }

  protected MovimientoTotalsDto Totals { get; private set; } = new();
  protected List<MovimientoModel> Movimientos { get; } = new();
  protected List<AttachmentModel> Attachments { get; } = new();
  protected List<ComprobanteModel> Comprobantes { get; } = new();
  protected List<LookupInt32Dto> CategoriaOptions { get; } = new();
  protected List<LookupInt32Dto> ProyectoOptions { get; } = new();
  protected List<LookupInt32Dto> CompraOptions { get; } = new();
  protected List<LookupInt32Dto> ServicioOptions { get; } = new();
  protected List<LookupStringDto> ReservacionOptions { get; } = new();
  protected List<LookupInt32Dto> NominaOptions { get; } = new();
  protected List<FormaPagoLookupDto> FormaPagoOptions { get; } = new();
  protected IReadOnlyList<string> TipoPolizaOptions { get; } = new[] { "INGRESO", "EGRESO", "DIARIO" };

  protected bool ShowMovimientoModal { get; private set; }
  protected MovimientoModel? MovimientoDraft { get; private set; }
  protected EditContext? MovimientoEditContext { get; private set; }
  protected string MovimientoModalTitle => _movimientoTarget is null ? "Agregar movimiento" : "Editar movimiento";
  protected CuentasContablesSelection? CuentaSelection { get; private set; }

  protected string HeaderStatus => Totals.Balance == 0m ? "Balanceada" : "Desbalanceada";
  protected string HeaderStatusCss => Totals.Balance == 0m ? "text-bg-success" : "text-bg-warning";

  protected override void OnInitialized()
  {
   
    
    RfcState.Changed += OnRfcStateChanged;
  }

  protected override async Task OnParametersSetAsync()
  {
   
    await PerformLoadAsync();
  }

  private async Task LoadLookupDataAsync(CancellationToken ct)
  {
    CategoriaOptions.Clear();
    ProyectoOptions.Clear();
    CompraOptions.Clear();
    ServicioOptions.Clear();
    ReservacionOptions.Clear();
    NominaOptions.Clear();
    FormaPagoOptions.Clear();

    var currentRfc = RfcState.CurrentRfc;
    if (string.IsNullOrWhiteSpace(currentRfc))
    {
      currentRfc = Header?.Rfc;
    }
    if (!string.IsNullOrWhiteSpace(currentRfc))
    {
      var categorias = await TransaccionService.GetCategoriasAsync(currentRfc, ct);
      CategoriaOptions.AddRange(categorias);

      var actividades = await TransaccionService.GetActividadesAsync(currentRfc, ct);
      ProyectoOptions.AddRange(actividades);

      var compras = await TransaccionService.GetComprasAsync(currentRfc, ct);
      CompraOptions.AddRange(compras);

      var servicios = await TransaccionService.GetServiciosAsync(currentRfc, ct);
      ServicioOptions.AddRange(servicios);

      var reservaciones = await TransaccionService.GetReservacionesAsync(currentRfc, ct);
      ReservacionOptions.AddRange(reservaciones);

      var nominas = await TransaccionService.GetNominasAsync(currentRfc, ct);
      NominaOptions.AddRange(nominas);
    }

    var formasPago = await TransaccionService.GetFormasPagoAsync(ct);
    FormaPagoOptions.AddRange(formasPago);
  }

  private async Task PerformLoadAsync()
  {
    _loadCts?.Cancel();
    _loadCts?.Dispose();
    _loadCts = new CancellationTokenSource();

    await LoadAsync(_loadCts.Token);
  }

  private async Task LoadProyectoOptionsAsync(string rfc, int? selectedId, CancellationToken ct)
  {
    ProyectoOptions.Clear();

    if (selectedId is int proyectoId)
    {
      var selectedResults = await TransaccionService.SearchActividadesAsync(
        rfc,
        proyectoId.ToString(CultureInfo.InvariantCulture),
        1,
        ct);
      AddDistinct(ProyectoOptions, selectedResults);
    }

    var defaultResults = await TransaccionService.SearchActividadesAsync(rfc, term: null, DefaultLookupLimit, ct);
    AddDistinct(ProyectoOptions, defaultResults);
  }

  private async Task LoadCompraOptionsAsync(string rfc, int? selectedId, CancellationToken ct)
  {
    CompraOptions.Clear();

    if (selectedId is int compraId)
    {
      var selectedResults = await TransaccionService.SearchComprasAsync(
        rfc,
        compraId.ToString(CultureInfo.InvariantCulture),
        1,
        ct);
      AddDistinct(CompraOptions, selectedResults);
    }

    var defaultResults = await TransaccionService.SearchComprasAsync(rfc, term: null, DefaultLookupLimit, ct);
    AddDistinct(CompraOptions, defaultResults);
  }

  private static void AddDistinct(List<LookupInt32Dto> target, IReadOnlyList<LookupInt32Dto> source)
  {
    foreach (var item in source)
    {
      if (target.Any(existing => existing.Id == item.Id))
        continue;

      target.Add(item);
    }
  }

  private bool TryGetCurrentRfc(out string? rfc)
  {
    rfc = RfcState.CurrentRfc;
    if (string.IsNullOrWhiteSpace(rfc))
    {
      rfc = Header?.Rfc;
    }

    if (string.IsNullOrWhiteSpace(rfc))
    {
      rfc = null;
      return false;
    }

    return true;
  }

  private async Task SearchProyectoOptionsAsync(string? overrideTerm = null)
  {
    if (!TryGetCurrentRfc(out var currentRfc) || currentRfc is null)
      return;

    var term = overrideTerm ?? proyectoSearchTerm;
    term = string.IsNullOrWhiteSpace(term) ? null : term.Trim();
    var maxResults = term is null ? DefaultLookupLimit : SearchLookupLimit;

    _proyectoSearchCts?.Cancel();
    _proyectoSearchCts?.Dispose();
    _proyectoSearchCts = CancellationTokenSource.CreateLinkedTokenSource(_loadCts?.Token ?? CancellationToken.None);
    var ct = _proyectoSearchCts.Token;

    isSearchingProyecto = true;
    if (!_isDisposed)
    {
      await InvokeAsync(StateHasChanged);
    }

    try
    {
      var results = await TransaccionService.SearchActividadesAsync(currentRfc, term, maxResults, ct);
      var list = results.ToList();

      if (Header?.ProyectoId is int proyectoId)
      {
        var proyectoIdText = proyectoId.ToString(CultureInfo.InvariantCulture);
        if (term is null || !string.Equals(term, proyectoIdText, StringComparison.OrdinalIgnoreCase))
        {
          var selectedResults = await TransaccionService.SearchActividadesAsync(currentRfc, proyectoIdText, 1, ct);
          foreach (var item in selectedResults)
          {
            if (!list.Any(existing => existing.Id == item.Id))
            {
              list.Insert(0, item);
            }
          }
        }
      }

      ProyectoOptions.Clear();
      ProyectoOptions.AddRange(list);
    }
    catch (OperationCanceledException)
    {
      // Ignored - a newer search is running.
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"Error al buscar proyectos: {ex.Message}");
    }
    finally
    {
      isSearchingProyecto = false;
      if (!_isDisposed)
      {
        await InvokeAsync(StateHasChanged);
      }
    }
  }

  private async Task SearchCompraOptionsAsync(string? overrideTerm = null)
  {
    if (!TryGetCurrentRfc(out var currentRfc) || currentRfc is null)
      return;

    var term = overrideTerm ?? compraSearchTerm;
    term = string.IsNullOrWhiteSpace(term) ? null : term.Trim();
    var maxResults = term is null ? DefaultLookupLimit : SearchLookupLimit;

    _compraSearchCts?.Cancel();
    _compraSearchCts?.Dispose();
    _compraSearchCts = CancellationTokenSource.CreateLinkedTokenSource(_loadCts?.Token ?? CancellationToken.None);
    var ct = _compraSearchCts.Token;

    isSearchingCompra = true;
    if (!_isDisposed)
    {
      await InvokeAsync(StateHasChanged);
    }

    try
    {
      var results = await TransaccionService.SearchComprasAsync(currentRfc, term, maxResults, ct);
      var list = results.ToList();

      if (Header?.CompraId is int compraId)
      {
        var compraIdText = compraId.ToString(CultureInfo.InvariantCulture);
        if (term is null || !string.Equals(term, compraIdText, StringComparison.OrdinalIgnoreCase))
        {
          var selectedResults = await TransaccionService.SearchComprasAsync(currentRfc, compraIdText, 1, ct);
          foreach (var item in selectedResults)
          {
            if (!list.Any(existing => existing.Id == item.Id))
            {
              list.Insert(0, item);
            }
          }
        }
      }

      CompraOptions.Clear();
      CompraOptions.AddRange(list);
    }
    catch (OperationCanceledException)
    {
      // Ignored - a newer search is running.
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"Error al buscar compras: {ex.Message}");
    }
    finally
    {
      isSearchingCompra = false;
      if (!_isDisposed)
      {
        await InvokeAsync(StateHasChanged);
      }
    }
  }

  private Task OnProyectoSearchKeyDown(KeyboardEventArgs args)
  {
    if (args.Key is "Enter" or "NumpadEnter")
    {
      return SearchProyectoOptionsAsync();
    }

    return Task.CompletedTask;
  }

  private void OnProyectoSearchInput(ChangeEventArgs args)
  {
    proyectoSearchTerm = args?.Value?.ToString() ?? string.Empty;
  }

  private Task OnCompraSearchKeyDown(KeyboardEventArgs args)
  {
    if (args.Key is "Enter" or "NumpadEnter")
    {
      return SearchCompraOptionsAsync();
    }

    return Task.CompletedTask;
  }

  private void OnCompraSearchInput(ChangeEventArgs args)
  {
    compraSearchTerm = args?.Value?.ToString() ?? string.Empty;
  }

  private async Task OnCuentaDirectKeyDown(KeyboardEventArgs args)
  {
    if (args.Key is not ("Enter" or "NumpadEnter"))
      return;

    if (cuentaDirectInput.HasValue && cuentaPicker is not null)
    {
      await cuentaPicker.ResolveAccountByIdAsync(cuentaDirectInput.Value);
    }
  }

  private async Task OnCuentaSelectionChangedAsync(CuentasContablesSelection? selection)
  {
    CuentaSelection = selection is null ? null : selection with { };
    cuentaDirectInput = selection?.Id;

    if (Header is not null)
    {
      Header.Cuenta = selection?.Id?.ToString(CultureInfo.InvariantCulture);
      Header.CuentaDescripcion = selection?.Descripcion;
    }

    if (!string.IsNullOrWhiteSpace(selection?.Rfc)
        && Header?.Rfc is string headerRfc
        && !string.Equals(headerRfc, selection.Rfc, StringComparison.OrdinalIgnoreCase))
    {
      UiMessages.ShowWarning($"La cuenta seleccionada pertenece al RFC {selection.Rfc}.");
    }

    await InvokeAsync(StateHasChanged);
  }

  private void OnCuentaPickerError(string message)
  {
    UiMessages.ShowError(message);
  }

  private void CancelLookupSearches()
  {
    _proyectoSearchCts?.Cancel();
    _proyectoSearchCts?.Dispose();
    _proyectoSearchCts = null;
    isSearchingProyecto = false;

    _compraSearchCts?.Cancel();
    _compraSearchCts?.Dispose();
    _compraSearchCts = null;
    isSearchingCompra = false;
  }

  private async Task LoadAsync(CancellationToken ct = default)
  {
    IsLoading = true;
    ErrorMessage = null;
    UiMessages.Clear();

    try
    {
      var headerDto = await TransaccionService.GetHeaderAsync(Id, ct);
      if (headerDto is null)
      {
        Header = null;
        _headerOriginal = null;
        Movimientos.Clear();
        Attachments.Clear();
        Comprobantes.Clear();
        Totals = new MovimientoTotalsDto();
        ErrorMessage = "No se encontró la transacción solicitada.";
        return;
      }

      Header = new TransaccionHeaderModel
      {
        Id = headerDto.Id,
        Folio = headerDto.Id.ToString("0000", CultureInfo.InvariantCulture),
        Rfc = headerDto.Rfc,
        Fecha = headerDto.Fecha,
        Cuenta = headerDto.Cuenta,
        Concepto = headerDto.Concepto,
        Monto = headerDto.Monto,
        CategoriaId = headerDto.Categoria,
        Facturado = headerDto.Facturado ?? false,
        Referencia = headerDto.Referencia,
        Memo = headerDto.Memo,
        ProyectoId = headerDto.ProyectoId,
        CompraId = headerDto.CompraId,
        ServicioId = headerDto.ServicioId,
        ReservacionId = headerDto.ReservacionId,
        NominaId = headerDto.NominaId,
        TipoPoliza = headerDto.TipoPoliza,
        FormaPago = headerDto.FormaPago,
        ComprobanteId = headerDto.ComprobanteId,
        ComprobanteMonto = headerDto.ComprobanteMonto
      };

      CuentaSelection = null;
      cuentaDirectInput = null;
      _cuentaSelectionOriginal = null;

      if (int.TryParse(headerDto.Cuenta, out var cuentaId))
      {
        var cuentaDto = await CuentasRepository.GetByIdAsync(cuentaId);
        if (cuentaDto is not null)
        {
          var selection = CreateSelectionFromDto(cuentaDto);
          CuentaSelection = selection with { };
          _cuentaSelectionOriginal = selection with { };
          cuentaDirectInput = selection.Id;
          Header.CuentaDescripcion = selection.Descripcion;
        }
        else
        {
          Header.CuentaDescripcion = null;
        }
      }
      else
      {
        Header.CuentaDescripcion = null;
      }

      await LoadLookupDataAsync(ct);
      _headerOriginal = Header.Clone();
      _cuentaSelectionOriginal = CuentaSelection is null ? null : CuentaSelection with { };
      HeaderEditContext = new EditContext(Header);

      Movimientos.Clear();
      var movimientosDto = await TransaccionService.GetMovimientosAsync(Id, ct);
      Movimientos.AddRange(movimientosDto.Select(m => new MovimientoModel
      {
        Id = m.Id,
        NombreCuenta = m.NombreCuenta,
        Concepto = m.Concepto,
        Debe = m.Debe,
        Haber = m.Haber
      }));

      Totals = await TransaccionService.GetMovimientoTotalsAsync(Id, ct);
      Header.Status = HeaderStatus;

      Attachments.Clear();
      var attachmentsDto = await TransaccionService.GetAttachmentsAsync(Id, ct);
      Attachments.AddRange(attachmentsDto.Select(a => new AttachmentModel
      {
        Id = a.Id,
        Nombre = string.IsNullOrWhiteSpace(a.AttachmentName) ? $"Adjunto {a.Id}" : a.AttachmentName!,
        Extension = string.IsNullOrWhiteSpace(a.AttachmentExtension) ? "-" : a.AttachmentExtension!,
        TamanoBytes = a.Length ?? 0
      }));

      Comprobantes.Clear();
      var comprobantesDto = await TransaccionService.GetComprobantesAsync(Id, ct);
      Comprobantes.AddRange(comprobantesDto.Select(c => new ComprobanteModel
      {
        ComprobanteId = c.ComprobanteId,
        Serie = c.Serie ?? string.Empty,
        Folio = c.Folio ?? string.Empty,
        Fecha = c.Fecha,
        Total = c.Total,
        Vinculado = c.Vinculado
      }));
    }
    catch (OperationCanceledException)
    {
      // ignored
    }
    catch (Exception ex)
    {
      ErrorMessage = ex.Message;
    }
    finally
    {
      IsLoading = false;
    }
  }

  protected string GetMovimientoRowClass(MovimientoModel movimiento)
    => _movimientoTarget == movimiento ? "table-active" : string.Empty;

  protected void ResetHeader()
  {
    if (_headerOriginal is null || Header is null)
      return;

    Header.CopyFrom(_headerOriginal);
    HeaderEditContext = new EditContext(Header);
    CuentaSelection = _cuentaSelectionOriginal is null ? null : _cuentaSelectionOriginal with { };
    cuentaDirectInput = CuentaSelection?.Id;
    Header.CuentaDescripcion = CuentaSelection?.Descripcion;
    StateHasChanged();
  }

  protected async Task SaveHeaderAsync()
  {
    if (HeaderEditContext is null || Header is null)
      return;

    if (!HeaderEditContext.Validate())
      return;

    if (Header.CategoriaId is null)
    {
      UiMessages.ShowError("Selecciona una categoría.");
      return;
    }

    if (string.IsNullOrWhiteSpace(Header.TipoPoliza) || string.IsNullOrWhiteSpace(Header.FormaPago))
    {
      UiMessages.ShowError("Selecciona un tipo de póliza y una forma de pago.");
      return;
    }

    if (CuentaSelection?.Id is null)
    {
      UiMessages.ShowError("Selecciona una cuenta contable.");
      return;
    }

    Header.Cuenta = CuentaSelection.Id.Value.ToString(CultureInfo.InvariantCulture);
    Header.CuentaDescripcion = CuentaSelection.Descripcion;

    IsSavingHeader = true;
    try
    {
      var request = new TransaccionGuardarCerrarRequest
      {
        TransaccionId = Header.Id,
        Concepto = Header.Concepto,
        Fecha = Header.Fecha,
        Cuenta = Header.Cuenta,
        Monto = Header.Monto,
        Categoria = Header.CategoriaId.Value,
        Facturado = Header.Facturado,
        Memo = string.IsNullOrWhiteSpace(Header.Memo) ? null : Header.Memo.Trim(),
        ProyectoId = Header.ProyectoId,
        CompraId = Header.CompraId,
        ServicioId = Header.ServicioId,
        ReservacionId = string.IsNullOrWhiteSpace(Header.ReservacionId) ? null : Header.ReservacionId,
        NominaId = Header.NominaId,
        TipoPoliza = Header.TipoPoliza!.Trim(),
        FormaPago = Header.FormaPago!.Trim()
      };

      var result = await TransaccionService.GuardarYCerrarAsync(request);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message ?? "No se pudo guardar la transacción.");
        return;
      }

      if (result.Totals is not null)
      {
        Totals = result.Totals;
        Header.Status = HeaderStatus;
      }

      _headerOriginal = Header.Clone();
      _cuentaSelectionOriginal = CuentaSelection is null ? null : CuentaSelection with { };
      UiMessages.ShowSuccess(result.Message ?? "Datos de la transacción guardados.");
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"Error al guardar: {ex.Message}");
    }
    finally
    {
      IsSavingHeader = false;
    }
  }

  protected void OpenMovimientoModal(MovimientoModel? existing)
  {
    if (existing is null)
    {
      _movimientoTarget = null;
      MovimientoDraft = new MovimientoModel
      {
        Debe = Header?.Monto ?? 0m,
        Haber = 0m,
        Concepto = Header?.Concepto,
       };
    }
    else
    {
      _movimientoTarget = existing;
      MovimientoDraft = existing.Clone();
    }

    MovimientoEditContext = new EditContext(MovimientoDraft!);
    ShowMovimientoModal = true;
  }

  protected void CloseMovimientoModal()
  {
    ShowMovimientoModal = false;
    MovimientoDraft = null;
    MovimientoEditContext = null;
    _movimientoTarget = null;
  }

  protected async Task SaveMovimientoAsync()
  {
    if (MovimientoEditContext is null || MovimientoDraft is null)
      return;

    if (!MovimientoEditContext.Validate())
      return;

    if (_movimientoTarget is null)
    {
      MovimientoDraft.Id = Movimientos.Count == 0 ? 1 : Movimientos.Max(m => m.Id) + 1;
      Movimientos.Add(MovimientoDraft.Clone());
    }
    else
    {
      _movimientoTarget.CopyFrom(MovimientoDraft);
    }

    UiMessages.ShowSuccess("Movimiento guardado.");
    CloseMovimientoModal();
    await InvokeAsync(StateHasChanged);
  }

  protected void CopyMontoToMovimiento()
  {
    if (MovimientoDraft is null || Header is null)
      return;

    MovimientoDraft.Debe = Header.Monto;
    MovimientoEditContext?.NotifyFieldChanged(new FieldIdentifier(MovimientoDraft, nameof(MovimientoDraft.Debe)));
  }

  protected void CopyConceptoFromHeader()
  {
    if (MovimientoDraft is null || Header is null)
      return;

    MovimientoDraft.Concepto = Header.Concepto;
    MovimientoEditContext?.NotifyFieldChanged(new FieldIdentifier(MovimientoDraft, nameof(MovimientoDraft.Concepto)));
  }

  protected bool IsAttachmentDownloading(AttachmentModel attachment)
    => attachment.Id == _attachmentDownloadingId;

  protected async Task DownloadAttachmentAsync(AttachmentModel attachment)
  {
    if (attachment is null)
      return;

    _attachmentDownloadingId = attachment.Id;
    await InvokeAsync(StateHasChanged);

    try
    {
      var content = await TransaccionService.GetAttachmentContentAsync(attachment.Id);
      if (content is null || content.Bytes is null || content.Bytes.Length == 0)
      {
        UiMessages.ShowError("No se encontró el contenido del adjunto.");
        return;
      }

      var fileName = string.IsNullOrWhiteSpace(content.FileName) ? attachment.Nombre : content.FileName;
      var contentType = string.IsNullOrWhiteSpace(content.ContentType) ? "application/octet-stream" : content.ContentType;
      var base64 = Convert.ToBase64String(content.Bytes);
      var dataUrl = $"data:{contentType};base64,{base64}";

      await JsRuntime.InvokeVoidAsync("triggerFileDownload", fileName, dataUrl);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"Error al descargar adjunto: {ex.Message}");
    }
    finally
    {
      _attachmentDownloadingId = null;
      await InvokeAsync(StateHasChanged);
    }
  }

  public void Dispose()
  {
    if (_isDisposed)
      return;

    _isDisposed = true;
    RfcState.Changed -= OnRfcStateChanged;
    _loadCts?.Cancel();
    _loadCts?.Dispose();
  }

  private async void OnRfcStateChanged()
  {
    if (_isDisposed)
      return;

    await InvokeAsync(async () =>
    {
      await PerformLoadAsync();
      StateHasChanged();
    });
  }

  private static CuentasContablesSelection CreateSelectionFromDto(CuentasContablesDto dto)
    => new()
    {
      Id = dto.Id,
      Rfc = dto.RazonSocial,
      Nivel1 = dto.Nivel1,
      Nivel2 = NormalizeTwoDigits(dto.Nivel2),
      Nivel3 = NormalizeTwoDigits(dto.Nivel3),
      Descripcion = dto.Descripcion
    };

  private static string NormalizeTwoDigits(string value)
  {
    var trimmed = value?.Trim() ?? string.Empty;
    if (trimmed.Length == 1 && char.IsDigit(trimmed[0]))
    {
      return trimmed.PadLeft(2, '0');
    }

    return trimmed;
  }

  protected sealed class TransaccionHeaderModel
  {
    public int Id { get; set; }
    public string? Folio { get; set; }
    public string? Rfc { get; set; }

    [Required(ErrorMessage = "La fecha es obligatoria.")]
    public DateTime Fecha { get; set; }

    public string? Cuenta { get; set; }

    public string? CuentaDescripcion { get; set; }

    [Required(ErrorMessage = "Captura un concepto.")]
    public string? Concepto { get; set; }

    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "Monto inválido.")]
    public decimal Monto { get; set; }

    [Required(ErrorMessage = "Selecciona una categoría.")]
    public int? CategoriaId { get; set; }

    public bool Facturado { get; set; }

    public string? Referencia { get; set; }

    [StringLength(500, ErrorMessage = "El memo no puede exceder 500 caracteres.")]
    public string? Memo { get; set; }

    public int? ProyectoId { get; set; }
    public int? CompraId { get; set; }
    public int? ServicioId { get; set; }
    public string? ReservacionId { get; set; }
    public int? NominaId { get; set; }

    [Required(ErrorMessage = "Selecciona un tipo de póliza.")]
    public string? TipoPoliza { get; set; }

    [Required(ErrorMessage = "Selecciona una forma de pago.")]
    [StringLength(10, ErrorMessage = "La forma de pago no puede exceder 10 caracteres.")]
    public string? FormaPago { get; set; }

    public int? ComprobanteId { get; set; }
    public decimal? ComprobanteMonto { get; set; }
    public string Status { get; set; } = "Desconocido";

    public string DisplayFolio => string.IsNullOrWhiteSpace(Folio)
      ? Id.ToString(CultureInfo.InvariantCulture)
      : Folio;

    public string ComprobanteResumen
      => ComprobanteId is null
        ? "Sin comprobante"
        : ComprobanteMonto.HasValue
          ? $"#{ComprobanteId} · {ComprobanteMonto.Value:N2}"
          : $"#{ComprobanteId}";

    public string ReferenciaPreview
      => string.IsNullOrWhiteSpace(Referencia)
        ? "-"
        : Referencia!.Length <= 10
          ? Referencia
          : Referencia[..10];

    public TransaccionHeaderModel Clone()
      => (TransaccionHeaderModel)MemberwiseClone();

    public void CopyFrom(TransaccionHeaderModel other)
    {
      Id = other.Id;
      Folio = other.Folio;
      Rfc = other.Rfc;
      Fecha = other.Fecha;
      Cuenta = other.Cuenta;
      Concepto = other.Concepto;
      Monto = other.Monto;
      CategoriaId = other.CategoriaId;
      Facturado = other.Facturado;
      Referencia = other.Referencia;
      Memo = other.Memo;
      CuentaDescripcion = other.CuentaDescripcion;
      ProyectoId = other.ProyectoId;
      CompraId = other.CompraId;
      ServicioId = other.ServicioId;
      ReservacionId = other.ReservacionId;
      NominaId = other.NominaId;
      TipoPoliza = other.TipoPoliza;
      FormaPago = other.FormaPago;
      ComprobanteId = other.ComprobanteId;
      ComprobanteMonto = other.ComprobanteMonto;
      Status = other.Status;
    }
  }

  protected sealed class MovimientoModel
  {
    public int Id { get; set; }

    [Required(ErrorMessage = "La cuenta es obligatoria.")]
    
    public string? NombreCuenta { get; set; }

    [Required(ErrorMessage = "El concepto es obligatorio.")]
    public string? Concepto { get; set; }

    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "Debe inválido.")]
    public decimal Debe { get; set; }

    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "Haber inválido.")]
    public decimal Haber { get; set; }

    public string? Memo { get; set; }

    public MovimientoModel Clone()
      => (MovimientoModel)MemberwiseClone();

    public void CopyFrom(MovimientoModel other)
    {
      Id = other.Id;
      NombreCuenta = other.NombreCuenta;
      Concepto = other.Concepto;
      Debe = other.Debe;
      Haber = other.Haber;
      Memo = other.Memo;
    }
  }

  protected sealed class AttachmentModel
  {
    public int Id { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long TamanoBytes { get; set; }
    public string TamanoHumano => FormatSize(TamanoBytes);
  }

  protected sealed class ComprobanteModel
  {
    public int ComprobanteId { get; set; }
    public string Serie { get; set; } = string.Empty;
    public string Folio { get; set; } = string.Empty;
    public DateTime Fecha { get; set; }
    public decimal Total { get; set; }
    public bool Vinculado { get; set; }
  }

  private static string FormatSize(long bytes)
  {
    if (bytes <= 0)
      return "0 B";

    var units = new[] { "B", "KB", "MB", "GB", "TB" };
    var magnitude = (int)Math.Min(units.Length - 1, Math.Log(bytes, 1024));
    var adjusted = bytes / Math.Pow(1024, magnitude);
    return $"{adjusted:0.##} {units[magnitude]}";
  }
}
