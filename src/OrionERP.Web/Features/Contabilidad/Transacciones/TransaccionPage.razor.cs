using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.Web.Services;
using OrionERP.Web.Shared;
using OrionERP.Web.State;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using Microsoft.Extensions.Configuration;

namespace OrionERP.Web.Features.Contabilidad.Transacciones;

public partial class TransaccionPage : ComponentBase, IDisposable
{
  protected enum SectionPanel
  {
    Movimientos,
    Comprobantes,
    Attachments,
    Resumen
  }

  private CancellationTokenSource? _loadCts;
  private TransaccionHeaderModel? _headerOriginal;
  private MovimientoModel? _movimientoTarget;
  private int? _attachmentDownloadingId;
  private int? _attachmentDeletingId;
  private int? _movimientoDeletingId;
  private int? _unlinkingComprobanteId;
  private readonly List<LookupInt32Dto> _allProyectoOptions = [];
  private readonly List<LookupInt32Dto> _allCompraOptions = [];
  private CuentaContablePicker? CuentaPicker;
  private int _attachmentInputKey;
  private SectionPanel? _expandedSection = SectionPanel.Movimientos;

  private bool _isDisposed;

  private static readonly CultureInfo CurrencyCulture = new("es-MX");

  private const long AttachmentMaxFileSize = TransaccionAttachmentCreateRequest.MaxFileSizeBytes;

  [Parameter] public int Id { get; set; }

  [Inject] public ITransaccionService TransaccionService { get; set; } = default!;
  [Inject] public IUiMessageService UiMessages { get; set; } = default!;
  [Inject] public IUserRfcState RfcState { get; set; } = default!;
  [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;
  [Inject] public IJSRuntime JsRuntime { get; set; } = default!;
  [Inject] public NavigationManager NavManager { get; set; } = default!;
  [Inject] public IConfiguration Configuration { get; set; } = default!;

  protected TransaccionHeaderModel? Header { get; private set; }
  protected EditContext? HeaderEditContext { get; private set; }
  protected bool IsLoading { get; private set; } = true;
  protected bool IsSavingHeader { get; private set; }
  protected bool IsApplyingPlantilla { get; private set; }
  protected string? ErrorMessage { get; private set; }

  protected MovimientoTotalsDto Totals { get; private set; } = new();
  protected List<MovimientoModel> Movimientos { get; } = [];
  protected List<AttachmentModel> Attachments { get; } = [];
  protected List<TransaccionCfdiCandidateDto> Comprobantes { get; } = [];
  protected List<LookupInt32Dto> CategoriaOptions { get; } = [];
  protected List<LookupInt32Dto> ProyectoOptions { get; } = [];
  protected List<LookupInt32Dto> CompraOptions { get; } = [];
  protected List<LookupInt32Dto> ServicioOptions { get; } = [];
  protected List<LookupInt32Dto> NominaOptions { get; } = [];
  protected List<FormaPagoLookupDto> FormaPagoOptions { get; } = [];
  protected IReadOnlyList<string> TipoPolizaOptions { get; } = new[] { "INGRESO", "EGRESO", "DIARIO" };

  protected string ProyectoSearchTerm { get; set; } = string.Empty;
  protected string CompraSearchTerm { get; set; } = string.Empty;

  protected bool ShowMovimientoModal { get; private set; }
  protected MovimientoModel? MovimientoDraft { get; private set; }
  protected EditContext? MovimientoEditContext { get; private set; }
  protected string MovimientoModalTitle => _movimientoTarget is null ? "Agregar movimiento" : "Editar movimiento";
  protected bool IsCuentaPickerVisible { get; private set; }
  protected CuentaContableSelection? MovimientoCuentaSelection { get; private set; }
  protected string? CuentaPickerRfc { get; private set; }
  protected string? CuentaPickerError { get; private set; }

  protected string HeaderStatus => Totals.Balance == 0m ? "Balanceada" : "Desbalanceada";
  protected string HeaderStatusCss => Totals.Balance == 0m ? "text-bg-success" : "text-bg-warning";
  protected bool IsUploadingAttachment { get; private set; }

  protected bool IsSectionExpanded(SectionPanel section) => _expandedSection == section;

  protected string GetSectionToggleIcon(SectionPanel section) => IsSectionExpanded(section) ? "oi-chevron-bottom" : "oi-chevron-right";

  protected static string FormatCurrency(decimal value)
    => value.ToString("C2", CurrencyCulture);

  protected void ToggleSection(SectionPanel section)
  {
    _expandedSection = _expandedSection == section ? (SectionPanel?)null : section;
  }

  protected override void OnInitialized()
  {


    RfcState.Changed += OnRfcStateChanged;
  }

  protected void SearchProyectoOptions()
  {
    ApplyLookupFilter(ProyectoSearchTerm, _allProyectoOptions, ProyectoOptions, Header?.ProyectoId);
  }

  protected void SearchCompraOptions()
  {
    ApplyLookupFilter(CompraSearchTerm, _allCompraOptions, CompraOptions, Header?.CompraId);
  }

  protected void HandleProyectoSearchKeyDown(KeyboardEventArgs args)
  {
    if (args.Key == "Enter")
    {
      SearchProyectoOptions();
    }
  }

  protected void HandleCompraSearchKeyDown(KeyboardEventArgs args)
  {
    if (args.Key == "Enter")
    {
      SearchCompraOptions();
    }
  }

  protected override async Task OnParametersSetAsync()
  {
   
    await PerformLoadAsync();
  }

  private async Task LoadLookupDataAsync(CancellationToken ct)
  {
    CategoriaOptions.Clear();
    _allProyectoOptions.Clear();
    ProyectoOptions.Clear();
    _allCompraOptions.Clear();
    CompraOptions.Clear();
    ServicioOptions.Clear();
    NominaOptions.Clear();
    FormaPagoOptions.Clear();
    ProyectoSearchTerm = string.Empty;
    CompraSearchTerm = string.Empty;

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
      _allProyectoOptions.AddRange(actividades);
      EnsureSelectedProyectoOption();

      var compras = await TransaccionService.GetComprasAsync(currentRfc, ct);
      _allCompraOptions.AddRange(compras);
      EnsureSelectedCompraOption();

      var servicios = await TransaccionService.GetServiciosAsync(currentRfc, ct);
      ServicioOptions.AddRange(servicios);

      var nominas = await TransaccionService.GetNominasAsync(currentRfc, ct);
      NominaOptions.AddRange(nominas);
    }

    var formasPago = await TransaccionService.GetFormasPagoAsync(ct);
    FormaPagoOptions.AddRange(formasPago);
  }

  private void ApplyLookupFilter(string? searchTerm, List<LookupInt32Dto> source, List<LookupInt32Dto> target, int? selectedId)
  {
    target.Clear();

    var trimmed = searchTerm?.Trim();
    if (string.IsNullOrWhiteSpace(trimmed))
    {
      EnsureSelectedOption(selectedId, source, target);
      return;
    }

    var candidates = new List<LookupInt32Dto>();
    if (int.TryParse(trimmed, out var id))
    {
      candidates.AddRange(source.Where(option => option.Id == id));
    }

    candidates.AddRange(source.Where(option => !string.IsNullOrWhiteSpace(option.Display) &&
                                             option.Display.Contains(trimmed, StringComparison.OrdinalIgnoreCase)));

    var seen = new HashSet<int>();
    foreach (var option in candidates)
    {
      if (seen.Add(option.Id))
      {
        target.Add(option);
      }
    }

    if (target.Count == 0)
    {
      EnsureSelectedOption(selectedId, source, target);
    }
  }

  private void EnsureSelectedProyectoOption()
  {
    EnsureSelectedOption(Header?.ProyectoId, _allProyectoOptions, ProyectoOptions);
  }

  private void EnsureSelectedCompraOption()
  {
    EnsureSelectedOption(Header?.CompraId, _allCompraOptions, CompraOptions);
  }

  private static void EnsureSelectedOption(int? selectedId, List<LookupInt32Dto> source, List<LookupInt32Dto> target)
  {
    if (selectedId is null)
    {
      return;
    }

    if (target.Any(option => option.Id == selectedId.Value))
    {
      return;
    }

    var selected = source.FirstOrDefault(option => option.Id == selectedId.Value);
    if (selected is not null)
    {
      target.Add(selected);
    }
  }

  private async Task PerformLoadAsync()
  {
    _loadCts?.Cancel();
    _loadCts?.Dispose();
    _loadCts = new CancellationTokenSource();

    await LoadAsync(_loadCts.Token);
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
        NominaId = headerDto.NominaId,
        TipoPoliza = headerDto.TipoPoliza,
        FormaPago = headerDto.FormaPago,
        ComprobanteId = headerDto.ComprobanteId,
        ComprobanteMonto = headerDto.ComprobanteMonto
      };
      await LoadLookupDataAsync(ct);
      _headerOriginal = Header.Clone();
      HeaderEditContext = new EditContext(Header);

      await ReloadMovimientosAsync(ct);
      EnsureSelectedProyectoOption();
      EnsureSelectedCompraOption();

      await ReloadAttachmentsAsync(ct);
      await ReloadComprobantesAsync(ct);
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

    if (Totals.Balance != 0m)
    {
      UiMessages.ShowWarning("Los Cargos y los Abonos no coinciden, verifique los movimientos Contables");
      return;
    }

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

      await GuardarMovimientosAsync();

      _headerOriginal = Header.Clone();
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

  protected async Task ApplyCategoriaPlantillaAsync()
  {
    if (Header is null)
      return;

    if (Header.CategoriaId is null)
    {
      UiMessages.ShowWarning("Selecciona una categoría antes de aplicar la plantilla.");
      return;
    }

    bool confirm;
    try
    {
      confirm = await JsRuntime.InvokeAsync<bool>("confirm", "¿Estas seguro que deseas aplicar esta plantilla a la poliza?");
    }
    catch
    {
      confirm = true;
    }

    if (!confirm)
      return;

    IsApplyingPlantilla = true;
    await InvokeAsync(StateHasChanged);

    try
    {
      var result = await TransaccionService.ApplyCategoriaPlantillaAsync(Header.Id, Header.CategoriaId.Value);
      if (result.Success)
      {
        UiMessages.ShowSuccess(result.Message);
        await ReloadMovimientosAsync();
      }
      else
      {
        UiMessages.ShowError(result.Message);
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"Error al aplicar la plantilla: {ex.Message}");
    }
    finally
    {
      IsApplyingPlantilla = false;
      await InvokeAsync(StateHasChanged);
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
        NombreCuenta = Header?.Cuenta,
        Descripcion = Header?.Cuenta
      };
    }
    else
    {
      _movimientoTarget = existing;
      MovimientoDraft = existing.Clone();
    }

    MovimientoEditContext = new EditContext(MovimientoDraft!);
    CuentaPickerError = null;
    CuentaPickerRfc = Header?.Rfc;
    MovimientoCuentaSelection = MovimientoDraft is null ? null : CreateCuentaSelectionFromMovimiento(MovimientoDraft);
    IsCuentaPickerVisible = false;
    ShowMovimientoModal = true;
  }

  protected void CloseMovimientoModal()
  {
    ShowMovimientoModal = false;
    MovimientoDraft = null;
    MovimientoEditContext = null;
    _movimientoTarget = null;
    MovimientoCuentaSelection = null;
    IsCuentaPickerVisible = false;
    CuentaPickerError = null;
  }

  protected void ShowCuentaPicker()
  {
    if (Header is null || MovimientoDraft is null)
      return;

    CuentaPickerError = null;
    CuentaPickerRfc = Header.Rfc;
    MovimientoCuentaSelection = CreateCuentaSelectionFromMovimiento(MovimientoDraft);
    IsCuentaPickerVisible = true;
  }

  protected void HideCuentaPicker()
  {
    IsCuentaPickerVisible = false;
    CuentaPickerError = null;
  }

  protected Task OnCuentaPickerRfcChangedAsync(string? rfc)
  {
    CuentaPickerRfc = rfc;
    return Task.CompletedTask;
  }

  protected async Task OnCuentaPickerSelectionChangedAsync(CuentaContableSelection? selection)
  {
    MovimientoCuentaSelection = selection;

    if (MovimientoDraft is null)
      return;

    MovimientoDraft.CuentaId = selection?.Id;
    MovimientoDraft.Nivel1 = selection?.Nivel1;
    MovimientoDraft.Nivel2 = selection?.Nivel2;
    MovimientoDraft.Nivel3 = selection?.Nivel3;
    MovimientoDraft.Descripcion = selection?.Descripcion;
    if (!string.IsNullOrWhiteSpace(selection?.Descripcion))
    {
      MovimientoDraft.NombreCuenta = selection.Descripcion;
    }

    NotifyMovimientoFieldChanged(nameof(MovimientoDraft.CuentaId));
    NotifyMovimientoFieldChanged(nameof(MovimientoDraft.Nivel1));
    NotifyMovimientoFieldChanged(nameof(MovimientoDraft.Nivel2));
    NotifyMovimientoFieldChanged(nameof(MovimientoDraft.Nivel3));
    NotifyMovimientoFieldChanged(nameof(MovimientoDraft.Descripcion));
    NotifyMovimientoFieldChanged(nameof(MovimientoDraft.NombreCuenta));

    CuentaPickerError = null;

    if (selection?.HasNivel3 == true)
    {
      IsCuentaPickerVisible = false;
    }

    await InvokeAsync(StateHasChanged);
  }

  protected async Task OnCuentaPickerSearchRequestedAsync()
  {
    if (CuentaPicker is not null)
    {
      await CuentaPicker.TryResolveDirectAccountAsync();
    }
  }

  protected async Task OnCuentaPickerErrorChangedAsync(string? error)
  {
    CuentaPickerError = error;
    await InvokeAsync(StateHasChanged);
  }

  protected Task PreventMovimientoSubmit(EditContext editContext)
    => Task.CompletedTask;

  protected async Task SaveMovimientoAsync()
  {
      if (MovimientoDraft is null || MovimientoEditContext is null || Header is null)
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

      UpdateTotalsFromMovimientos();

      UiMessages.ShowSuccess("Movimiento guardado.");
      CloseMovimientoModal();
      await InvokeAsync(StateHasChanged);
  }

  private async Task GuardarMovimientosAsync()
  {
      if (Header is null) return;

      var request = new TransaccionMovimientosUpdateRequest
      {
          TransaccionId = Header.Id,
          Movimientos = Movimientos.Select(m => new TransaccionMovimientoUpdateItem
          {
              Id = m.Id,
              CuentaId = m.CuentaId,
              Nivel1 = m.Nivel1,
              Nivel2 = m.Nivel2,
              Nivel3 = m.Nivel3,
              NombreCuenta = m.NombreCuenta,
              Concepto = m.Concepto,
              Debe = m.Debe,
              Haber = m.Haber
          }).ToList()
      };

      var result = await TransaccionService.GuardarMovimientosAsync(request);
      if (!result.Success)
      {
          UiMessages.ShowError(result.Message ?? "No se pudieron guardar los movimientos.");
      }
  }

  protected void CopyMontoToDebe()
  {
    if (MovimientoDraft is null || Header is null)
      return;

    MovimientoDraft.Debe = Header.Monto;
    NotifyMovimientoFieldChanged(nameof(MovimientoDraft.Debe));
  }

  protected void ApplySubtotalToDebe()
  {
    if (MovimientoDraft is null || Header is null)
      return;

    MovimientoDraft.Debe = CalculateSubtotal(Header.Monto);
    NotifyMovimientoFieldChanged(nameof(MovimientoDraft.Debe));
  }

  protected void ApplyIvaToDebe()
  {
    if (MovimientoDraft is null || Header is null)
      return;

    MovimientoDraft.Debe = CalculateIva(Header.Monto);
    NotifyMovimientoFieldChanged(nameof(MovimientoDraft.Debe));
  }

  protected void ClearDebe()
  {
    if (MovimientoDraft is null)
      return;

    MovimientoDraft.Debe = 0m;
    NotifyMovimientoFieldChanged(nameof(MovimientoDraft.Debe));
  }

  protected void CopyMontoToHaber()
  {
    if (MovimientoDraft is null || Header is null)
      return;

    MovimientoDraft.Haber = Header.Monto;
    NotifyMovimientoFieldChanged(nameof(MovimientoDraft.Haber));
  }

  protected void ApplySubtotalToHaber()
  {
    if (MovimientoDraft is null || Header is null)
      return;

    MovimientoDraft.Haber = CalculateSubtotal(Header.Monto);
    NotifyMovimientoFieldChanged(nameof(MovimientoDraft.Haber));
  }

  protected void ApplyIvaToHaber()
  {
    if (MovimientoDraft is null || Header is null)
      return;

    MovimientoDraft.Haber = CalculateIva(Header.Monto);
    NotifyMovimientoFieldChanged(nameof(MovimientoDraft.Haber));
  }

  protected void ClearHaber()
  {
    if (MovimientoDraft is null)
      return;

    MovimientoDraft.Haber = 0m;
    NotifyMovimientoFieldChanged(nameof(MovimientoDraft.Haber));
  }

  protected void CopyConceptoFromHeader()
  {
    if (MovimientoDraft is null || Header is null)
      return;

    MovimientoDraft.Concepto = Header.Concepto;
    MovimientoEditContext?.NotifyFieldChanged(new FieldIdentifier(MovimientoDraft, nameof(MovimientoDraft.Concepto)));
  }

  protected static string FormatNivelValue(string? value)
    => string.IsNullOrWhiteSpace(value) ? "-" : value;

  private void NotifyMovimientoFieldChanged(string propertyName)
  {
    if (MovimientoDraft is null || MovimientoEditContext is null)
      return;

    MovimientoEditContext.NotifyFieldChanged(new FieldIdentifier(MovimientoDraft, propertyName));
  }

  private static decimal CalculateSubtotal(decimal amount)
    => amount == 0m
       ? 0m
       : decimal.Round(amount / 1.16m, 2, MidpointRounding.AwayFromZero);

  private static decimal CalculateIva(decimal amount)
    => amount == 0m
       ? 0m
       : decimal.Round(amount * 0.16m, 2, MidpointRounding.AwayFromZero);

  private CuentaContableSelection? CreateCuentaSelectionFromMovimiento(MovimientoModel movimiento)
  {
    if (string.IsNullOrWhiteSpace(movimiento.Nivel1)
        && string.IsNullOrWhiteSpace(movimiento.Nivel2)
        && string.IsNullOrWhiteSpace(movimiento.Nivel3)
        && string.IsNullOrWhiteSpace(movimiento.Descripcion)
        && movimiento.CuentaId is null)
    {
      return null;
    }

    return new CuentaContableSelection
    {
      Id = movimiento.CuentaId,
      Rfc = Header?.Rfc ?? CuentaPickerRfc,
      Nivel1 = movimiento.Nivel1,
      Nivel2 = movimiento.Nivel2,
      Nivel3 = movimiento.Nivel3,
      Descripcion = movimiento.Descripcion ?? movimiento.NombreCuenta
    };
  }

  private void UpdateTotalsFromMovimientos()
  {
    var totalDebe = Movimientos.Sum(m => m.Debe);
    var totalHaber = Movimientos.Sum(m => m.Haber);

    Totals = new MovimientoTotalsDto
    {
      Debe = totalDebe,
      Haber = totalHaber
    };

    if (Header is not null)
    {
      Header.Status = HeaderStatus;
    }
  }

  private async Task ReloadMovimientosAsync(CancellationToken ct = default)
  {
    Movimientos.Clear();
    var movimientosDto = await TransaccionService.GetMovimientosAsync(Id, ct);
    Movimientos.AddRange(movimientosDto.Select(m => new MovimientoModel
    {
      Id = m.Id,
      Nivel1 = m.Nivel1,
      Nivel2 = m.Nivel2,
      Nivel3 = m.Nivel3,
      NombreCuenta = m.NombreCuenta,
      Descripcion = m.NombreCuenta,
      Concepto = m.Concepto,
      Debe = m.Debe,
      Haber = m.Haber
    }));

    Totals = await TransaccionService.GetMovimientoTotalsAsync(Id, ct);
    if (Header is not null)
    {
      Header.Status = HeaderStatus;
    }
  }

  private async Task ReloadAttachmentsAsync(CancellationToken ct = default)
  {
    Attachments.Clear();
    var attachmentsDto = await TransaccionService.GetAttachmentsAsync(Id, ct);
    Attachments.AddRange(attachmentsDto.Select(a => new AttachmentModel
    {
      Id = a.Id,
      Nombre = string.IsNullOrWhiteSpace(a.AttachmentName) ? $"Adjunto {a.Id}" : a.AttachmentName!,
      Extension = string.IsNullOrWhiteSpace(a.AttachmentExtension) ? "-" : a.AttachmentExtension!,
      TamanoBytes = a.Length ?? 0
    }));
  }

  private async Task ReloadComprobantesAsync(CancellationToken ct = default)
  {
    Comprobantes.Clear();

    if (Header is null)
    {
      return;
    }

    try
    {
      var ids = await TransaccionService.GetLinkedCfdiIdsAsync(Header.Id, ct);
      if (ids.Count == 0)
      {
        return;
      }

      var request = new TransaccionCfdiSearchRequest
      {
        Rfc = Header.Rfc ?? string.Empty,
        ComprobantesCsv = string.Join(',', ids),
        Renglones = Math.Max(ids.Count, 25)
      };

      var rows = await TransaccionService.GetCfdiCandidatesAsync(request, ct);
      Comprobantes.AddRange(rows);
    }
    catch (OperationCanceledException)
    {
      // ignored
    }
    catch (Exception)
    {
      UiMessages.ShowError("No se pudieron cargar los comprobantes ligados.");
    }
  }

  protected async Task OpenComprobanteCfdiAsync(TransaccionCfdiCandidateDto? comprobante)
  {
    if (comprobante?.XmlAttachmentId is null)
    {
      return;
    }

    var url = $"/cfdi/html-cfdi/{comprobante.XmlAttachmentId}";

    try
    {
      await JsRuntime.InvokeVoidAsync("open", url, "_blank", "noopener,noreferrer");
    }
    catch
    {
      NavManager.NavigateTo(url);
    }
  }

  protected bool IsAttachmentDownloading(AttachmentModel attachment)
    => attachment.Id == _attachmentDownloadingId;

  protected bool IsAttachmentDeleting(AttachmentModel attachment)
    => attachment.Id == _attachmentDeletingId;

  protected bool IsMovimientoDeleting(MovimientoModel movimiento)
    => movimiento is not null && movimiento.Id == _movimientoDeletingId;

  protected bool IsComprobanteUnlinking(TransaccionCfdiCandidateDto comprobante)
    => comprobante is not null && comprobante.ComprobanteId == _unlinkingComprobanteId;

  protected async Task UnlinkComprobanteAsync(TransaccionCfdiCandidateDto comprobante)
  {
    if (Header is null)
    {
      return;
    }

    var confirmed = await ConfirmAsync("¿Estás seguro que deseas desligar este comprobante de esta póliza?");
    if (!confirmed)
    {
      return;
    }

    var placeholderTransaccionId = GetPlaceholderTransaccionId();
    if (!placeholderTransaccionId.HasValue)
    {
      UiMessages.ShowError("No se pudo determinar la póliza temporal configurada.");
      return;
    }

    _unlinkingComprobanteId = comprobante.ComprobanteId;
    await InvokeAsync(StateHasChanged);

    try
    {
      var request = new TransaccionComprobanteUnlinkRequest
      {
        CurrentTransaccionId = Header.Id,
        TempTransaccionId = placeholderTransaccionId.Value,
        ComprobanteId = comprobante.ComprobanteId
      };

      var result = await TransaccionService.UnlinkComprobanteAsync(request);
      if (!result.Success)
      {
        UiMessages.ShowWarning(result.Message ?? "No se encontró el vínculo de este comprobante con la póliza actual.");
      }
      else
      {
        UiMessages.ShowSuccess(result.Message ?? "Comprobante desligado correctamente.");
      }

      await ReloadComprobantesAsync();
      await ReloadAttachmentsAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo desligar el comprobante: {ex.Message}");
    }
    finally
    {
      _unlinkingComprobanteId = null;
      await InvokeAsync(StateHasChanged);
    }
  }

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

  protected async Task DeleteAttachmentAsync(AttachmentModel attachment)
  {
    if (attachment is null || Header is null)
      return;

    var extension = (attachment.Extension ?? string.Empty).Trim().ToLowerInvariant();
    var isXml = extension == "xml";

    if (!isXml)
    {
      var confirmed = await ConfirmAsync("¿Estás seguro que deseas borrar este archivo adjunto?");
      if (!confirmed)
        return;

      await ExecuteAttachmentMutationAsync(
          attachment.Id,
          () => TransaccionService.DeleteAttachmentAsync(attachment.Id),
          "Archivo adjunto eliminado.");
      return;
    }

    int comprobanteId;
    try
    {
      comprobanteId = await TransaccionService.GetComprobanteIdByXmlAttachmentAsync(attachment.Id);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo validar el XML: {ex.Message}");
      return;
    }

    if (comprobanteId <= 0)
    {
      var confirmed = await ConfirmAsync("Este XML no está ligado a ningún comprobante. ¿Deseas borrarlo?");
      if (!confirmed)
        return;

      await ExecuteAttachmentMutationAsync(
          attachment.Id,
          () => TransaccionService.DeleteAttachmentAsync(attachment.Id),
          "Archivo adjunto eliminado.");
      return;
    }

    bool isLinkedToCurrent;
    try
    {
      isLinkedToCurrent = await TransaccionService.IsComprobanteLinkedToTransaccionAsync(Header.Id, comprobanteId);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo validar el vínculo del comprobante: {ex.Message}");
      return;
    }

    if (isLinkedToCurrent)
    {
      UiMessages.ShowWarning("Este XML está ligado a esta Póliza y no puede ser borrado.");
      return;
    }

    var placeholderTransaccionId = GetPlaceholderTransaccionId();
    if (!placeholderTransaccionId.HasValue)
    {
      UiMessages.ShowError("No se pudo determinar la póliza temporal configurada.");
      return;
    }

    var moveConfirmMessage =
      $"Este XML está ligado a un comprobante, pero no a esta Póliza.\n" +
      $"No se borrará; se moverá a la póliza temporal (TranID = {placeholderTransaccionId}).\n" +
      "¿Deseas continuar?";

    var moveConfirmed = await ConfirmAsync(moveConfirmMessage);
    if (!moveConfirmed)
      return;

    await ExecuteAttachmentMutationAsync(
        attachment.Id,
        () => TransaccionService.MoveAttachmentToTransaccionAsync(attachment.Id, placeholderTransaccionId.Value),
        "XML movido a la póliza temporal.");
  }

  private async Task<bool> ConfirmAsync(string message)
  {
    try
    {
      return await JsRuntime.InvokeAsync<bool>("confirm", message);
    }
    catch
    {
      return true;
    }
  }

  private async Task ExecuteAttachmentMutationAsync(int attachmentId, Func<Task> mutation, string successMessage)
  {
    _attachmentDeletingId = attachmentId;
    await InvokeAsync(StateHasChanged);

    try
    {
      await mutation();
      await ReloadAttachmentsAsync();
      UiMessages.ShowSuccess(successMessage);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo actualizar el archivo adjunto: {ex.Message}");
    }
    finally
    {
      _attachmentDeletingId = null;
      await InvokeAsync(StateHasChanged);
    }
  }

  private int? GetPlaceholderTransaccionId()
  {
    var placeholder = Configuration["SatXml:PlaceholderTransaccionId"];
    return int.TryParse(placeholder, out var parsed) ? parsed : null;
  }

  protected async Task DeleteMovimientoAsync(MovimientoModel movimiento)
  {
    if (movimiento is null || Header is null)
      return;

    bool confirm;
    try
    {
      confirm = await JsRuntime.InvokeAsync<bool>("confirm", $"¿Deseas eliminar el movimiento '{movimiento.NombreCuenta}'?");
    }
    catch
    {
      confirm = true;
    }

    if (!confirm)
      return;

    _movimientoDeletingId = movimiento.Id;
    await InvokeAsync(StateHasChanged);

    try
    {
      await TransaccionService.DeleteMovimientoAsync(Header.Id, movimiento.Id);
      await ReloadMovimientosAsync();
      UiMessages.ShowSuccess("Movimiento eliminado.");
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo eliminar el movimiento: {ex.Message}");
    }
    finally
    {
      _movimientoDeletingId = null;
      await InvokeAsync(StateHasChanged);
    }
  }

  protected async Task OnAttachmentSelectedAsync(InputFileChangeEventArgs args)
  {
    if (Header is null || args.FileCount == 0)
    {
      _attachmentInputKey++;
      await InvokeAsync(StateHasChanged);
      return;
    }

    var file = args.File;
    if (file is null)
    {
      _attachmentInputKey++;
      await InvokeAsync(StateHasChanged);
      return;
    }

    if (file.Size == 0)
    {
      UiMessages.ShowError("El archivo seleccionado está vacío.");
      _attachmentInputKey++;
      await InvokeAsync(StateHasChanged);
      return;
    }

    if (file.Size > AttachmentMaxFileSize)
    {
      UiMessages.ShowError("El archivo excede el tamaño máximo permitido (5 MB).");
      _attachmentInputKey++;
      await InvokeAsync(StateHasChanged);
      return;
    }

    IsUploadingAttachment = true;
    await InvokeAsync(StateHasChanged);

    try
    {
      await using var stream = file.OpenReadStream(AttachmentMaxFileSize);
      using var ms = new MemoryStream();
      await stream.CopyToAsync(ms);
      var bytes = ms.ToArray();

      var extension = Path.GetExtension(file.Name);
      if (!string.IsNullOrWhiteSpace(extension))
      {
        extension = extension.Trim().TrimStart('.');
      }
      else
      {
        extension = null;
      }

      var request = new TransaccionAttachmentCreateRequest
      {
        TransaccionId = Header.Id,
        FileName = file.Name,
        Extension = extension,
        Description = "Archivo adjunto (carga manual)",
        Content = bytes
      };

      await TransaccionService.AddAttachmentAsync(request);
      await ReloadAttachmentsAsync();
      UiMessages.ShowSuccess("Archivo cargado correctamente.");
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"Error al cargar el archivo: {ex.Message}");
    }
    finally
    {
      IsUploadingAttachment = false;
      _attachmentInputKey++;
      await InvokeAsync(StateHasChanged);
    }
  }

  protected async Task DeleteTransaccionAsync()
  {
      if (Header is null) return;

      try
      {
          var confirmation = await JsRuntime.InvokeAsync<string>("prompt", "Para confirmar la eliminación, escribe 'Delete' y presiona Aceptar:");
          if (confirmation != "Delete")
          {
              UiMessages.ShowInfo("La eliminación ha sido cancelada.");
              return;
          }

          IsSavingHeader = true;
          await InvokeAsync(StateHasChanged);

          var result = await TransaccionService.DeleteTransaccionAsync(Header.Id);

          if (result.Success)
          {
              UiMessages.ShowSuccess(result.Message ?? "Transacción eliminada correctamente.");
              NavManager.NavigateTo("/contabilidad/transacciones/list"); // Redirect to list page
          }
          else
          {
              UiMessages.ShowError(result.Message ?? "No se pudo eliminar la transacción.");
          }
      }
      catch (Exception ex)
      {
          UiMessages.ShowError($"Ocurrió un error al intentar eliminar la transacción: {ex.Message}");
      }
      finally
      {
          IsSavingHeader = false;
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
    GC.SuppressFinalize(this);
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

  protected sealed class TransaccionHeaderModel
  {
    public int Id { get; set; }
    public string? Folio { get; set; }
    public string? Rfc { get; set; }

    [Required(ErrorMessage = "La fecha es obligatoria.")]
    public DateTime Fecha { get; set; }

    public string? Cuenta { get; set; }

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
      ProyectoId = other.ProyectoId;
      CompraId = other.CompraId;
      ServicioId = other.ServicioId;
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

    public int? CuentaId { get; set; }

    [Required(ErrorMessage = "La cuenta es obligatoria.")]

    public string? NombreCuenta { get; set; }

    public string? Nivel1 { get; set; }

    public string? Nivel2 { get; set; }

    public string? Nivel3 { get; set; }

    public string? Descripcion { get; set; }

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
      CuentaId = other.CuentaId;
      NombreCuenta = other.NombreCuenta;
      Nivel1 = other.Nivel1;
      Nivel2 = other.Nivel2;
      Nivel3 = other.Nivel3;
      Descripcion = other.Descripcion;
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
