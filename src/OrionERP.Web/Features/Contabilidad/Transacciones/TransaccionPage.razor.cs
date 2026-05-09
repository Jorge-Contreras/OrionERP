using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using OrionERP.Application.Features.Cfdi.DeclaracionPrevia;
using OrionERP.Application.Features.Contabilidad.Bancos;
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
using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Web.Features.Contabilidad.Transacciones;

public partial class TransaccionPage : ComponentBase, IDisposable
{
  protected enum SectionPanel
  {
    Movimientos,
    Comprobantes,
    Reservaciones,
    Banco,
    Attachments,
    Resumen
  }

  private CancellationTokenSource? _loadCts;
  private TransaccionHeaderModel? _headerOriginal;
  private MovimientoModel? _movimientoTarget;
  private int? _attachmentDownloadingId;
  private int? _attachmentDeletingId;
  private int? _movimientoDeletingId;
  private int? _selectedReservacionId;
  private int? _unlinkingReservacionId;
  private long? _unlinkingComprobanteId;
  private long? _unlinkingBancoMovimientoId;
  private long? _selectedComprobanteId;
  private long? _cancellingComprobanteId;
  private readonly List<LookupInt32Dto> _allCompraOptions = [];
  private CuentaContablePicker? CuentaPicker;
  private int _attachmentInputKey;
  private SectionPanel _activeSection = SectionPanel.Movimientos;
  private LookupInt32Dto? _selectedProyectoOption;
  private CancellationTokenSource? _proyectoSearchCts;

  private bool _isDisposed;
  private string _montoInput = string.Empty;

  private static readonly CultureInfo CurrencyCulture = new("es-MX");
  private static readonly CultureInfo CurrencyInputCulture = new("en-US");
  private static readonly NumberStyles CurrencyNumberStyles = NumberStyles.AllowThousands | NumberStyles.AllowDecimalPoint;

  private const long AttachmentMaxFileSize = TransaccionAttachmentCreateRequest.MaxFileSizeBytes;
  private const decimal IvaRate = 0.16m;
  private const decimal SubtotalDivisor = 1m + IvaRate;
  private const int ProyectoDescriptionMaxLength = 20;
  private const int HeaderConceptoMaxLength = 500;

  [Parameter] public int Id { get; set; }

  [Inject] public ITransaccionService TransaccionService { get; set; } = default!;
  [Inject] public IUiMessageService UiMessages { get; set; } = default!;
  [Inject] public IUserRfcState RfcState { get; set; } = default!;
  [Inject] public IJSRuntime JsRuntime { get; set; } = default!;
  [Inject] public NavigationManager NavManager { get; set; } = default!;
  [Inject] public IBancosService BancosService { get; set; } = default!;
  [Inject] public IDeclaracionPreviaService DeclaracionPreviaService { get; set; } = default!;

  protected TransaccionHeaderModel? Header { get; private set; }
  protected EditContext? HeaderEditContext { get; private set; }
  protected bool IsLoading { get; private set; } = true;
  protected bool IsSavingHeader { get; private set; }
  protected bool IsApplyingPlantilla { get; private set; }
  protected string? ErrorMessage { get; private set; }

  protected MovimientoTotalsDto Totals { get; private set; } = new();
  protected List<MovimientoModel> Movimientos { get; } = [];
  protected List<BankMovementDto> BancoMovimientos { get; } = [];
  protected List<AttachmentModel> Attachments { get; } = [];
  protected List<TransaccionCfdiCandidateDto> Comprobantes { get; } = [];
  protected List<TransaccionReservacionLinkDto> ReservacionLinks { get; } = [];
  protected List<TransaccionReservacionSearchItemDto> ReservacionCandidates { get; } = [];
  protected List<LookupInt32Dto> CategoriaOptions { get; } = [];
  protected List<LookupInt32Dto> ProyectoOptions { get; } = [];
  protected List<LookupInt32Dto> CompraOptions { get; } = [];
  protected List<LookupInt32Dto> ServicioOptions { get; } = [];
  protected List<LookupInt32Dto> NominaOptions { get; } = [];
  protected List<FormaPagoLookupDto> FormaPagoOptions { get; } = [];
  protected IReadOnlyList<string> TipoPolizaOptions { get; } = new[] { "INGRESO", "EGRESO", "DIARIO" };
  protected IReadOnlyList<PublicoMonthOption> PublicoMonthOptions { get; } = CreatePublicoMonthOptions();
  protected IReadOnlyList<int> PublicoYearOptions { get; } = CreatePublicoYearOptions();

  protected string ProyectoSearchTerm { get; set; } = string.Empty;
  protected string CompraSearchTerm { get; set; } = string.Empty;
  protected string ReservacionSearchTerm { get; set; } = string.Empty;
  protected decimal ReservacionAmountInput { get; set; }
  protected string SelectedPublicoMonthCode { get; set; } = DateTime.Today.Month.ToString("00", CultureInfo.InvariantCulture);
  protected int SelectedPublicoYear { get; set; } = DateTime.Today.Year;
  protected bool ShowProyectoResults { get; private set; }

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
  protected bool HasReservacionSelection => _selectedReservacionId.HasValue;
  protected int? SelectedReservacionId => _selectedReservacionId;
  protected string? SelectedReservacionCliente { get; private set; }
  protected string? SelectedReservacionStatus { get; private set; }
  protected DateTime? SelectedReservacionCheckIn { get; private set; }
  protected DateTime? SelectedReservacionCheckOut { get; private set; }
  protected decimal SelectedReservacionTotal { get; private set; }
  protected decimal SelectedReservacionPagado { get; private set; }
  protected decimal SelectedReservacionPorPagar { get; private set; }
  protected string? SelectedReservacionNotes { get; private set; }
  protected decimal ReservacionesAsignadasTotal => decimal.Round(ReservacionLinks.Sum(item => item.Amount), 2, MidpointRounding.AwayFromZero);
  protected decimal ReservacionesPorAsignar => Header is null
    ? 0m
    : decimal.Round(Header.Monto - ReservacionesAsignadasTotal, 2, MidpointRounding.AwayFromZero);
  protected bool HasReservacionesAsignadas => decimal.Abs(ReservacionesAsignadasTotal) > 0.01m;
  protected bool CanSaveHeaderWithReservaciones => !HasReservacionesAsignadas || decimal.Abs(ReservacionesPorAsignar) <= 0.01m;
  protected string ReservacionesBalanceCss => decimal.Abs(ReservacionesPorAsignar) <= 0.01m
    ? "text-bg-success"
    : ReservacionesPorAsignar > 0m
      ? "text-bg-warning"
      : "text-bg-danger";
  protected string ReservacionActionLabel => SelectedReservacionHasExistingLink
    ? "Actualizar asignacion"
    : "Asignar reservacion";
  protected bool SelectedReservacionHasExistingLink => _selectedReservacionId.HasValue
    && ReservacionLinks.Any(item => item.ReservationId == _selectedReservacionId.Value);
  protected bool IsUploadingAttachment { get; private set; }
  protected bool IsLoadingBancoMovimientos { get; private set; }
  protected bool IsLoadingReservacionLinks { get; private set; }
  protected bool IsSearchingReservaciones { get; private set; }
  protected bool IsSavingReservacionLink { get; private set; }
  protected bool IsRegeneratingMovimientos { get; private set; }
  protected bool IsTimbrandoPublico { get; private set; }

  protected bool IsActiveSection(SectionPanel section) => _activeSection == section;

  protected string GetTabButtonClass(SectionPanel section) => $"nav-link {(IsActiveSection(section) ? "active" : string.Empty)}";

  protected static string FormatCurrency(decimal value)
    => value.ToString("C2", CurrencyCulture);

  protected string MontoInput
  {
    get => _montoInput;
    set
    {
      _montoInput = value;

      if (Header is null)
        return;

      if (TryParseMonto(value, out var parsed))
      {
        Header.Monto = parsed;
        HeaderEditContext?.NotifyFieldChanged(new FieldIdentifier(Header, nameof(Header.Monto)));
      }
    }
  }

  protected void ActivateSection(SectionPanel section)
  {
    _activeSection = section;
  }

  protected override void OnInitialized()
  {
    RfcState.Changed += OnRfcStateChanged;
  }

  protected void SearchCompraOptions()
  {
    ApplyLookupFilter(CompraSearchTerm, _allCompraOptions, CompraOptions, Header?.CompraId);
  }

  private string? ResolveLookupRfc()
    => Normalize(Header?.Rfc) ?? Normalize(RfcState.CurrentRfc);

  protected async Task OnProyectoSearchInputAsync(ChangeEventArgs args)
  {
    ProyectoSearchTerm = args.Value?.ToString() ?? string.Empty;

    if (string.IsNullOrWhiteSpace(ProyectoSearchTerm))
    {
      SetProyectoSelection(null);
      ProyectoOptions.Clear();
      ShowProyectoResults = false;
      return;
    }

    await SearchProyectoOptionsAsync();
    ShowProyectoResults = true;
  }

  protected async Task HandleProyectoSearchKeyDownAsync(KeyboardEventArgs args)
  {
    if (args.Key == "Escape")
    {
      ShowProyectoResults = false;
      SyncProyectoSearchTermWithSelection();
      return;
    }

    if (args.Key != "Enter")
    {
      return;
    }

    await SearchProyectoOptionsAsync();

    if (int.TryParse(ProyectoSearchTerm?.Trim(), out var proyectoId))
    {
      var exactMatch = ProyectoOptions.FirstOrDefault(option => option.Id == proyectoId);
      if (exactMatch is not null)
      {
        SelectProyecto(exactMatch);
        return;
      }

      var exactProyecto = await LoadProyectoByIdAsync(proyectoId);
      if (exactProyecto is not null)
      {
        SelectProyecto(exactProyecto);
        return;
      }
    }

    ShowProyectoResults = true;

    if (ProyectoOptions.Count == 1)
    {
      SelectProyecto(ProyectoOptions[0]);
    }
  }

  protected async Task OnProyectoSearchBlurAsync()
  {
    await Task.Yield();

    ShowProyectoResults = false;
    SyncProyectoSearchTermWithSelection();

    await InvokeAsync(StateHasChanged);
  }

  protected void HandleCompraSearchKeyDown(KeyboardEventArgs args)
  {
    if (args.Key == "Enter")
    {
      SearchCompraOptions();
    }
  }

  protected async Task HandleReservacionSearchKeyDown(KeyboardEventArgs args)
  {
    if (args.Key == "Enter")
    {
      await SearchReservacionesAsync();
    }
  }

  protected override async Task OnParametersSetAsync()
  {
   
    await PerformLoadAsync();
  }

  private async Task LoadLookupDataAsync(CancellationToken ct)
  {
    ResetLookupData();

    var currentRfc = ResolveLookupRfc();
    var formasPagoTask = TransaccionService.GetFormasPagoAsync(ct);

    if (!string.IsNullOrWhiteSpace(currentRfc))
    {
      var categoriasTask = TransaccionService.GetCategoriasAsync(currentRfc, ct);
      var comprasTask = TransaccionService.GetComprasAsync(currentRfc, ct);
      var serviciosTask = TransaccionService.GetServiciosAsync(currentRfc, ct);
      var nominasTask = TransaccionService.GetNominasAsync(currentRfc, ct);

      await Task.WhenAll(formasPagoTask, categoriasTask, comprasTask, serviciosTask, nominasTask);

      CategoriaOptions.AddRange(await categoriasTask);
      _allCompraOptions.AddRange(await comprasTask);
      ServicioOptions.AddRange(await serviciosTask);
      NominaOptions.AddRange(await nominasTask);
    }

    FormaPagoOptions.AddRange(await formasPagoTask);
    await EnsureSelectedProyectoLoadedAsync(ct);
    SyncProyectoSearchTermWithSelection();
    EnsureSelectedCompraOption();
  }

  private void ResetLookupData()
  {
    CategoriaOptions.Clear();
    ProyectoOptions.Clear();
    _allCompraOptions.Clear();
    CompraOptions.Clear();
    ServicioOptions.Clear();
    NominaOptions.Clear();
    FormaPagoOptions.Clear();
    CancelProyectoSearch();
    _selectedProyectoOption = null;
    ProyectoSearchTerm = string.Empty;
    CompraSearchTerm = string.Empty;
    ShowProyectoResults = false;
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

  private void EnsureSelectedCompraOption()
  {
    EnsureSelectedOption(Header?.CompraId, _allCompraOptions, CompraOptions);
  }

  private async Task SearchProyectoOptionsAsync(CancellationToken ct = default)
  {
    ProyectoOptions.Clear();

    var trimmed = ProyectoSearchTerm.Trim();
    var currentRfc = ResolveLookupRfc();
    if (string.IsNullOrWhiteSpace(trimmed) || string.IsNullOrWhiteSpace(currentRfc))
    {
      return;
    }

    CancelProyectoSearch();
    _proyectoSearchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

    try
    {
      var matches = await TransaccionService.SearchActividadesAsync(currentRfc, trimmed, 25, _proyectoSearchCts.Token);
      ProyectoOptions.AddRange(matches);
      EnsureSelectedProyectoOption();
    }
    catch (OperationCanceledException)
    {
      // ignored
    }
  }

  private async Task EnsureSelectedProyectoLoadedAsync(CancellationToken ct = default)
  {
    _selectedProyectoOption = null;

    var proyectoId = Header?.ProyectoId;
    var currentRfc = ResolveLookupRfc();
    if (proyectoId is null || proyectoId <= 0 || string.IsNullOrWhiteSpace(currentRfc))
    {
      return;
    }

    _selectedProyectoOption = await TransaccionService.GetActividadByIdAsync(currentRfc, proyectoId.Value, ct);
  }

  private async Task<LookupInt32Dto?> LoadProyectoByIdAsync(int proyectoId, CancellationToken ct = default)
  {
    var currentRfc = ResolveLookupRfc();
    if (proyectoId <= 0 || string.IsNullOrWhiteSpace(currentRfc))
    {
      return null;
    }

    return await TransaccionService.GetActividadByIdAsync(currentRfc, proyectoId, ct);
  }

  private void EnsureSelectedProyectoOption()
  {
    var proyectoId = Header?.ProyectoId;
    if (proyectoId is null || _selectedProyectoOption is null || _selectedProyectoOption.Id != proyectoId.Value)
    {
      return;
    }

    if (ProyectoOptions.All(option => option.Id != proyectoId.Value))
    {
      ProyectoOptions.Insert(0, _selectedProyectoOption);
    }
  }

  private void CancelProyectoSearch()
  {
    _proyectoSearchCts?.Cancel();
    _proyectoSearchCts?.Dispose();
    _proyectoSearchCts = null;
  }

  protected string GetProyectoOptionDisplay(LookupInt32Dto proyecto)
    => FormatProyectoDisplay(proyecto, truncateDescription: false);

  protected void SelectProyecto(LookupInt32Dto proyecto)
  {
    SetProyectoSelection(proyecto);
    ProyectoOptions.Clear();
    ShowProyectoResults = false;
  }

  private void SetProyectoSelection(LookupInt32Dto? proyecto)
  {
    _selectedProyectoOption = proyecto;

    if (Header is null)
    {
      ProyectoSearchTerm = proyecto is null
        ? string.Empty
        : FormatProyectoDisplay(proyecto, truncateDescription: true);
      return;
    }

    Header.ProyectoId = proyecto?.Id;
    ProyectoSearchTerm = proyecto is null
      ? string.Empty
      : FormatProyectoDisplay(proyecto, truncateDescription: true);

    HeaderEditContext?.NotifyFieldChanged(new FieldIdentifier(Header, nameof(Header.ProyectoId)));
  }

  private void SyncProyectoSearchTermWithSelection()
  {
    var proyectoId = Header?.ProyectoId;
    if (proyectoId is null)
    {
      ProyectoSearchTerm = string.Empty;
      return;
    }

    if (_selectedProyectoOption is not null && _selectedProyectoOption.Id == proyectoId.Value)
    {
      ProyectoSearchTerm = FormatProyectoDisplay(_selectedProyectoOption, truncateDescription: true);
      return;
    }

    ProyectoSearchTerm = proyectoId.Value.ToString(CultureInfo.InvariantCulture);
  }

  private static string FormatProyectoDisplay(LookupInt32Dto proyecto, bool truncateDescription)
  {
    var description = proyecto.Description?.Trim();
    if (string.IsNullOrWhiteSpace(description))
    {
      return proyecto.Id.ToString(CultureInfo.InvariantCulture);
    }

    var formattedDescription = truncateDescription
      ? TruncateProyectoDescription(description)
      : description;

    return $"{proyecto.Id} - {formattedDescription}";
  }

  private static string TruncateProyectoDescription(string description)
  {
    if (description.Length <= ProyectoDescriptionMaxLength)
    {
      return description;
    }

    const string ellipsis = "...";
    return $"{description[..(ProyectoDescriptionMaxLength - ellipsis.Length)]}{ellipsis}";
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
    if (_isDisposed)
    {
      return;
    }

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
        ResetLoadedState();
        return;
      }

      Header = CreateHeaderModel(headerDto);
      UpdateMontoInputFromHeader();
      await LoadLookupDataAsync(ct);
      _headerOriginal = Header.Clone();
      HeaderEditContext = new EditContext(Header);

      await ReloadMovimientosAsync(ct);
      await ReloadAttachmentsAsync(ct);
      await ReloadComprobantesAsync(ct);
      await ReloadBancoMovimientosAsync(ct);
      await ReloadReservacionLinksAsync(ct);
      await SearchReservacionesAsync(ct);
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

  private void ResetLoadedState()
  {
    CancelProyectoSearch();
    Header = null;
    HeaderEditContext = null;
    _headerOriginal = null;
    _selectedProyectoOption = null;
    _selectedComprobanteId = null;
    Movimientos.Clear();
    BancoMovimientos.Clear();
    Attachments.Clear();
    Comprobantes.Clear();
    ReservacionLinks.Clear();
    ReservacionCandidates.Clear();
    Totals = new MovimientoTotalsDto();
    ClearReservacionSelection();
    CloseMovimientoModal();
  }

  private static TransaccionHeaderModel CreateHeaderModel(TransaccionHeaderDto headerDto)
    => new()
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

  protected string GetMovimientoRowClass(MovimientoModel movimiento)
    => _movimientoTarget == movimiento ? "table-active" : string.Empty;

  protected async Task ResetHeaderAsync()
  {
    if (_headerOriginal is null || Header is null)
      return;

    Header.CopyFrom(_headerOriginal);
    UpdateMontoInputFromHeader();
    ShowProyectoResults = false;
    await EnsureSelectedProyectoLoadedAsync();
    SyncProyectoSearchTermWithSelection();
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

    if (!CanSaveHeaderWithReservaciones)
    {
      ActivateSection(SectionPanel.Reservaciones);
      UiMessages.ShowWarning("Si hay monto asignado a reservaciones, el total asignado debe dejar 'Por asignar' en 0.");
      return;
    }

    var movimientosSnapshot = Movimientos
      .Select(m => m.Clone())
      .ToList();

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

      var headerResult = await TransaccionService.GuardarYCerrarAsync(request);
      if (!headerResult.Success)
      {
        UiMessages.ShowError(headerResult.Message ?? "No se pudo guardar la transacción.");
        return;
      }

      var movimientosResult = await GuardarMovimientosAsync(movimientosSnapshot);
      await ReloadMovimientosAsync();
      _headerOriginal = Header.Clone();

      if (!movimientosResult.Success)
      {
        UiMessages.ShowWarning($"La póliza se guardó, pero los movimientos no pudieron sincronizarse: {movimientosResult.Message}");
        return;
      }

      UiMessages.ShowSuccess(headerResult.Message ?? "Datos de la transacción guardados.");
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

  protected void OnMontoBlur()
  {
    if (Header is null)
      return;

    if (TryParseMonto(MontoInput, out var parsed))
    {
      Header.Monto = parsed;
      _montoInput = FormatMonto(parsed);
    }
    else
    {
      _montoInput = FormatMonto(Header.Monto);
    }

    HeaderEditContext?.NotifyFieldChanged(new FieldIdentifier(Header, nameof(Header.Monto)));
  }

  protected void OnMontoInput(ChangeEventArgs args)
  {
    MontoInput = args.Value?.ToString() ?? string.Empty;
  }

  protected void OnHeaderConceptoInput(ChangeEventArgs args)
  {
    if (Header is null)
      return;

    Header.Concepto = args.Value?.ToString();
    HeaderEditContext?.NotifyFieldChanged(new FieldIdentifier(Header, nameof(Header.Concepto)));
  }

  private void UpdateMontoInputFromHeader()
  {
    _montoInput = FormatMonto(Header?.Monto ?? 0m);
  }

  private static string FormatMonto(decimal value)
    => value.ToString("N2", CurrencyInputCulture);

  private static bool TryParseMonto(string? value, out decimal result)
    => decimal.TryParse(value, CurrencyNumberStyles, CurrencyInputCulture, out result);

  protected async Task ApplyCategoriaPlantillaAsync()
  {
    if (Header is null)
      return;

    if (Header.CategoriaId is null)
    {
      UiMessages.ShowWarning("Selecciona una categoría antes de aplicar la plantilla.");
      return;
    }

    var confirm = await ConfirmAsync("¿Estás seguro que deseas aplicar esta plantilla a la póliza?");
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

  protected async Task TimbrarCfdiPublicoAsync()
  {
    if (Header is null)
    {
      return;
    }

    if (Header.Monto <= 0m)
    {
      UiMessages.ShowWarning("El monto de la póliza debe ser mayor que cero para timbrar un CFDI público.");
      return;
    }

    var selectedMonth = PublicoMonthOptions.FirstOrDefault(item => item.Code == SelectedPublicoMonthCode);
    if (selectedMonth is null)
    {
      UiMessages.ShowWarning("Selecciona un mes global válido.");
      return;
    }

    if (!PublicoYearOptions.Contains(SelectedPublicoYear))
    {
      UiMessages.ShowWarning("Selecciona un año global válido.");
      return;
    }

    var confirmationMessage =
      "¿Estas seguro que deseas timbrar una factura al Público en General con la siguiente información?" +
      $"\n\nMonto = {FormatCurrency(Header.Monto)}" +
      $"\nMes Global = {selectedMonth.Name}" +
      $"\nAño Global = {SelectedPublicoYear}" +
      $"\nFolio = {Header.Id}";

    var confirmed = await ConfirmAsync(confirmationMessage);
    if (!confirmed)
    {
      return;
    }

    IsTimbrandoPublico = true;
    await InvokeAsync(StateHasChanged);

    try
    {
      var result = await TransaccionService.TimbrarCfdiPublicoAsync(
          new TransaccionTimbrarPublicoRequest
          {
            TransaccionId = Header.Id,
            Monto = Header.Monto,
            FormaPago = Header.FormaPago,
            GlobalMes = selectedMonth.Code,
            GlobalAnio = SelectedPublicoYear
          });

      if (!result.Success)
      {
        UiMessages.ShowError(result.Message ?? "No se pudo timbrar el CFDI público.");
        return;
      }

      await ReloadComprobantesAsync();
      await ReloadAttachmentsAsync();
      UiMessages.ShowSuccess(result.Message ?? "La factura al público en general se generó correctamente.");
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo timbrar el CFDI público: {ex.Message}");
    }
    finally
    {
      IsTimbrandoPublico = false;
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

  private async Task<TransaccionCommandResult> GuardarMovimientosAsync(IReadOnlyList<MovimientoModel>? movimientos = null)
  {
    if (Header is null)
    {
      return TransaccionCommandResult.Fail("No se encontró la transacción actual.");
    }

    var movimientosToSave = movimientos ?? Movimientos;

    var request = new TransaccionMovimientosUpdateRequest
    {
      TransaccionId = Header.Id,
      Movimientos = movimientosToSave.Select(m => new TransaccionMovimientoUpdateItem
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

    return await TransaccionService.GuardarMovimientosAsync(request);
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

    ApplyHeaderConcepto(MovimientoDraft);
    NotifyMovimientoFieldChanged(nameof(MovimientoDraft.Concepto));
  }

  protected void OnMovimientoConceptoInput(ChangeEventArgs args)
  {
    if (MovimientoDraft is null)
      return;

    MovimientoDraft.Concepto = args.Value?.ToString();
    NotifyMovimientoFieldChanged(nameof(MovimientoDraft.Concepto));
  }

  protected void UpdateAllMovimientosConceptoFromHeader()
  {
    if (Header is null)
      return;

    if (Movimientos.Count == 0)
    {
      UiMessages.ShowWarning("No hay movimientos para actualizar.");
      return;
    }

    foreach (var movimiento in Movimientos)
    {
      ApplyHeaderConcepto(movimiento);
    }

    UiMessages.ShowSuccess("Se actualizó el concepto de todos los movimientos. Guarda la póliza para persistir los cambios.");
  }

  protected static string FormatNivelValue(string? value)
    => string.IsNullOrWhiteSpace(value) ? "-" : value;

  protected static string FormatCuentaCodigo(MovimientoModel movimiento)
  {
    if (movimiento is null)
    {
      return "-";
    }

    var segments = new[] { movimiento.Nivel1, movimiento.Nivel2, movimiento.Nivel3 }
      .Where(segment => !string.IsNullOrWhiteSpace(segment))
      .Select(segment => segment!.Trim())
      .ToArray();

    return segments.Length == 0 ? "-" : string.Join('.', segments);
  }

  private void NotifyMovimientoFieldChanged(string propertyName)
  {
    if (MovimientoDraft is null || MovimientoEditContext is null)
      return;

    MovimientoEditContext.NotifyFieldChanged(new FieldIdentifier(MovimientoDraft, propertyName));
  }

  private void ApplyHeaderConcepto(MovimientoModel movimiento)
  {
    if (Header is null)
      return;

    movimiento.Concepto = Header.Concepto;
  }

  private static decimal CalculateSubtotal(decimal amount)
    => amount == 0m
      ? 0m
      : RoundCurrencyAmount(amount / SubtotalDivisor);

  private static decimal CalculateIva(decimal amount)
    => amount == 0m
      ? 0m
      : RoundCurrencyAmount(amount - CalculateSubtotal(amount));

  private static decimal RoundCurrencyAmount(decimal amount)
    => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

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

    UpdateHeaderStatus();
  }

  private async Task ReloadMovimientosAsync(CancellationToken ct = default)
  {
    Movimientos.Clear();
    var movimientosDto = await TransaccionService.GetMovimientosAsync(Id, ct);
    Movimientos.AddRange(movimientosDto.Select(MapMovimiento));

    Totals = await TransaccionService.GetMovimientoTotalsAsync(Id, ct);
    UpdateHeaderStatus();
  }

  private async Task ReloadAttachmentsAsync(CancellationToken ct = default)
  {
    Attachments.Clear();
    var attachmentsDto = await TransaccionService.GetAttachmentsAsync(Id, ct);
    Attachments.AddRange(attachmentsDto.Select(MapAttachment));
  }

  private void UpdateHeaderStatus()
  {
    if (Header is not null)
    {
      Header.Status = HeaderStatus;
    }
  }

  private static MovimientoModel MapMovimiento(TransaccionMovimientoDto movimiento)
    => new()
    {
      Id = movimiento.Id,
      Nivel1 = movimiento.Nivel1,
      Nivel2 = movimiento.Nivel2,
      Nivel3 = movimiento.Nivel3,
      NombreCuenta = movimiento.NombreCuenta,
      Descripcion = movimiento.NombreCuenta,
      Concepto = movimiento.Concepto,
      Debe = movimiento.Debe,
      Haber = movimiento.Haber
    };

  private static AttachmentModel MapAttachment(TransaccionAttachmentDto attachment)
    => new()
    {
      Id = attachment.Id,
      Nombre = string.IsNullOrWhiteSpace(attachment.AttachmentName) ? $"Adjunto {attachment.Id}" : attachment.AttachmentName!,
      Extension = string.IsNullOrWhiteSpace(attachment.AttachmentExtension) ? "-" : attachment.AttachmentExtension!,
      TamanoBytes = attachment.Length ?? 0
    };

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

  private async Task ReloadReservacionLinksAsync(CancellationToken ct = default)
  {
    ReservacionLinks.Clear();
    IsLoadingReservacionLinks = true;
    await InvokeAsync(StateHasChanged);

    try
    {
      var links = await TransaccionService.GetReservacionLinksAsync(Id, ct);
      ReservacionLinks.AddRange(links);
      RefreshReservacionSelection();
    }
    catch (OperationCanceledException)
    {
      // ignored
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudieron cargar las reservaciones ligadas: {ex.Message}");
    }
    finally
    {
      IsLoadingReservacionLinks = false;
      await InvokeAsync(StateHasChanged);
    }
  }

  private static string BuildReservacionPaymentConcepto(IReadOnlyList<TransaccionReservacionLinkDto> reservaciones)
  {
    var ordered = reservaciones
      .OrderBy(item => item.ReservationId)
      .Select(item => new
      {
        item.ReservationId,
        Cliente = SanitizeReservacionClienteForConcepto(item.Cliente)
      })
      .ToList();

    if (ordered.Count == 0)
    {
      return string.Empty;
    }

    if (ordered.Count == 1)
    {
      var single = ordered[0];
      return TrimHeaderConcepto($"Pago de la reservacion#{single.ReservationId} ({single.Cliente})");
    }

    const string prefix = "Pago de las reservaciones: ";
    var current = prefix;

    for (var index = 0; index < ordered.Count; index++)
    {
      var reservacion = ordered[index];
      var segment = $"#{reservacion.ReservationId} ({reservacion.Cliente})";
      var candidate = index == 0
        ? prefix + segment
        : current + " + " + segment;

      if (candidate.Length > HeaderConceptoMaxLength)
      {
        if (index == 0)
        {
          return TrimHeaderConcepto(candidate);
        }

        var truncated = current + " +...";
        return truncated.Length <= HeaderConceptoMaxLength
          ? truncated
          : TrimHeaderConcepto(current);
      }

      current = candidate;
    }

    return current;
  }

  private static string SanitizeReservacionClienteForConcepto(string? cliente)
  {
    if (string.IsNullOrWhiteSpace(cliente))
    {
      return "Sin cliente";
    }

    return string.Join(
      " ",
      cliente
        .Replace('\r', ' ')
        .Replace('\n', ' ')
        .Replace('\t', ' ')
        .Split(' ', StringSplitOptions.RemoveEmptyEntries));
  }

  private static string TrimHeaderConcepto(string value)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return string.Empty;
    }

    var trimmed = value.Trim();
    return trimmed.Length <= HeaderConceptoMaxLength
      ? trimmed
      : trimmed[..HeaderConceptoMaxLength];
  }

  protected async Task SearchReservacionesAsync(CancellationToken ct = default)
  {
    IsSearchingReservaciones = true;
    await InvokeAsync(StateHasChanged);

    try
    {
      var rows = await TransaccionService.SearchReservacionesAsync(Normalize(ReservacionSearchTerm), ct);
      ReservacionCandidates.Clear();
      ReservacionCandidates.AddRange(rows);
      RefreshReservacionSelection();
    }
    catch (OperationCanceledException)
    {
      // ignored
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudieron cargar las reservaciones candidatas: {ex.Message}");
    }
    finally
    {
      IsSearchingReservaciones = false;
      await InvokeAsync(StateHasChanged);
    }
  }

  protected bool IsReservacionCandidateSelected(TransaccionReservacionSearchItemDto reservacion)
    => reservacion is not null && reservacion.ReservationId == _selectedReservacionId;

  protected bool IsReservacionLinkSelected(TransaccionReservacionLinkDto reservacion)
    => reservacion is not null && reservacion.ReservationId == _selectedReservacionId;

  protected bool IsReservacionLinkDeleting(TransaccionReservacionLinkDto reservacion)
    => reservacion is not null && reservacion.ReservationId == _unlinkingReservacionId;

  protected void UpdateHeaderConceptoFromReservaciones()
  {
    if (Header is null)
      return;

    if (ReservacionLinks.Count == 0)
    {
      UiMessages.ShowWarning("No hay reservaciones ligadas para actualizar el concepto.");
      return;
    }

    Header.Concepto = BuildReservacionPaymentConcepto(ReservacionLinks);
    HeaderEditContext?.NotifyFieldChanged(new FieldIdentifier(Header, nameof(Header.Concepto)));
    UiMessages.ShowSuccess("Se actualizo el concepto de la poliza. Guarda cambios para persistirlo.");
  }

  protected void SelectReservacionCandidate(TransaccionReservacionSearchItemDto reservacion)
  {
    if (reservacion is null)
    {
      return;
    }

    var existingLink = ReservacionLinks.FirstOrDefault(item => item.ReservationId == reservacion.ReservationId);
    SetReservacionSelection(
        reservacion.ReservationId,
        reservacion.Cliente,
        reservacion.Status,
        reservacion.CheckIn,
        reservacion.CheckOut,
        reservacion.TotalPrice,
        reservacion.Pagado,
        reservacion.PorPagar,
        reservacion.Notes,
        existingLink?.Amount ?? reservacion.PorPagar);
  }

  protected void EditReservacionLink(TransaccionReservacionLinkDto reservacion)
  {
    if (reservacion is null)
    {
      return;
    }

    SetReservacionSelection(
        reservacion.ReservationId,
        reservacion.Cliente,
        reservacion.Status,
        reservacion.CheckIn,
        reservacion.CheckOut,
        reservacion.TotalPrice,
        reservacion.Pagado,
        reservacion.PorPagar,
        reservacion.Notes,
        reservacion.Amount);
  }

  protected void UsePendingReservacionAmount()
  {
    if (!HasReservacionSelection)
    {
      return;
    }

    ReservacionAmountInput = SelectedReservacionPorPagar;
  }

  protected void ClearReservacionSelection()
  {
    _selectedReservacionId = null;
    SelectedReservacionCliente = null;
    SelectedReservacionStatus = null;
    SelectedReservacionCheckIn = null;
    SelectedReservacionCheckOut = null;
    SelectedReservacionTotal = 0m;
    SelectedReservacionPagado = 0m;
    SelectedReservacionPorPagar = 0m;
    SelectedReservacionNotes = null;
    ReservacionAmountInput = 0m;
  }

  protected async Task SaveReservacionLinkAsync()
  {
    if (Header is null)
    {
      return;
    }

    if (!_selectedReservacionId.HasValue)
    {
      UiMessages.ShowWarning("Selecciona una reservación para asignarla.");
      return;
    }

    if (decimal.Abs(ReservacionAmountInput) < 0.01m)
    {
      UiMessages.ShowWarning("Ingresa un monto distinto de cero.");
      return;
    }

    IsSavingReservacionLink = true;
    await InvokeAsync(StateHasChanged);

    try
    {
      var result = await TransaccionService.UpsertReservacionLinkAsync(new TransaccionReservacionLinkUpsertRequest
      {
        TransaccionId = Header.Id,
        ReservationId = _selectedReservacionId.Value,
        Amount = ReservacionAmountInput
      });

      if (!result.Success)
      {
        UiMessages.ShowError(result.Message ?? "No se pudo guardar la asignación de la reservación.");
        return;
      }

      UiMessages.ShowSuccess(result.Message ?? "Asignación guardada correctamente.");
      await ReloadReservacionLinksAsync();
      await SearchReservacionesAsync();
      RefreshReservacionSelection();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo guardar la asignación de la reservación: {ex.Message}");
    }
    finally
    {
      IsSavingReservacionLink = false;
      await InvokeAsync(StateHasChanged);
    }
  }

  protected async Task DeleteReservacionLinkAsync(TransaccionReservacionLinkDto reservacion)
  {
    if (Header is null || reservacion is null)
    {
      return;
    }

    var confirmed = await ConfirmAsync($"¿Estás seguro que deseas desligar la reservación {reservacion.ReservationId} de esta póliza?");
    if (!confirmed)
    {
      return;
    }

    _unlinkingReservacionId = reservacion.ReservationId;
    await InvokeAsync(StateHasChanged);

    try
    {
      var result = await TransaccionService.DeleteReservacionLinkAsync(Header.Id, reservacion.ReservationId);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message ?? "No se pudo eliminar la asignación de la reservación.");
        return;
      }

      UiMessages.ShowSuccess(result.Message ?? "Asignación eliminada correctamente.");
      await ReloadReservacionLinksAsync();
      await SearchReservacionesAsync();
      RefreshReservacionSelection();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo eliminar la asignación de la reservación: {ex.Message}");
    }
    finally
    {
      _unlinkingReservacionId = null;
      await InvokeAsync(StateHasChanged);
    }
  }

  private async Task ReloadBancoMovimientosAsync(CancellationToken ct = default)
  {
    BancoMovimientos.Clear();
    IsLoadingBancoMovimientos = true;
    await InvokeAsync(StateHasChanged);

    try
    {
      var movimientos = await BancosService.GetMovementsByTransactionAsync(Id, ct);
      BancoMovimientos.AddRange(movimientos);
    }
    catch (OperationCanceledException)
    {
      // ignored
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudieron cargar los movimientos bancarios: {ex.Message}");
    }
    finally
    {
      IsLoadingBancoMovimientos = false;
      await InvokeAsync(StateHasChanged);
    }
  }

  private void RefreshReservacionSelection()
  {
    if (!_selectedReservacionId.HasValue)
    {
      return;
    }

    var linked = ReservacionLinks.FirstOrDefault(item => item.ReservationId == _selectedReservacionId.Value);
    if (linked is not null)
    {
      SetReservacionSelection(
          linked.ReservationId,
          linked.Cliente,
          linked.Status,
          linked.CheckIn,
          linked.CheckOut,
          linked.TotalPrice,
          linked.Pagado,
          linked.PorPagar,
          linked.Notes,
          linked.Amount);
      return;
    }

    var candidate = ReservacionCandidates.FirstOrDefault(item => item.ReservationId == _selectedReservacionId.Value);
    if (candidate is not null)
    {
      SetReservacionSelection(
          candidate.ReservationId,
          candidate.Cliente,
          candidate.Status,
          candidate.CheckIn,
          candidate.CheckOut,
          candidate.TotalPrice,
          candidate.Pagado,
          candidate.PorPagar,
          candidate.Notes,
          candidate.PorPagar);
      return;
    }

    ClearReservacionSelection();
  }

  private void SetReservacionSelection(
      int reservationId,
      string? cliente,
      string? status,
      DateTime? checkIn,
      DateTime? checkOut,
      decimal totalPrice,
      decimal pagado,
      decimal porPagar,
      string? notes,
      decimal amount)
  {
    _selectedReservacionId = reservationId;
    SelectedReservacionCliente = string.IsNullOrWhiteSpace(cliente) ? "(Sin cliente)" : cliente;
    SelectedReservacionStatus = status;
    SelectedReservacionCheckIn = checkIn;
    SelectedReservacionCheckOut = checkOut;
    SelectedReservacionTotal = totalPrice;
    SelectedReservacionPagado = pagado;
    SelectedReservacionPorPagar = porPagar;
    SelectedReservacionNotes = notes;
    ReservacionAmountInput = amount;
  }

  private static string? Normalize(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private static IReadOnlyList<PublicoMonthOption> CreatePublicoMonthOptions()
    => new[]
    {
      new PublicoMonthOption("01", "ENERO"),
      new PublicoMonthOption("02", "FEBRERO"),
      new PublicoMonthOption("03", "MARZO"),
      new PublicoMonthOption("04", "ABRIL"),
      new PublicoMonthOption("05", "MAYO"),
      new PublicoMonthOption("06", "JUNIO"),
      new PublicoMonthOption("07", "JULIO"),
      new PublicoMonthOption("08", "AGOSTO"),
      new PublicoMonthOption("09", "SEPTIEMBRE"),
      new PublicoMonthOption("10", "OCTUBRE"),
      new PublicoMonthOption("11", "NOVIEMBRE"),
      new PublicoMonthOption("12", "DICIEMBRE")
    };

  private static IReadOnlyList<int> CreatePublicoYearOptions()
  {
    const int startYear = 2020;
    var currentYear = DateTime.Today.Year;
    var yearCount = Math.Max(1, currentYear - startYear + 1);
    return Enumerable.Range(startYear, yearCount).ToArray();
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

  protected bool IsComprobanteCancelling(TransaccionCfdiCandidateDto comprobante)
    => comprobante is not null && comprobante.ComprobanteId == _cancellingComprobanteId;

  protected bool CanCancelComprobante(TransaccionCfdiCandidateDto comprobante)
  {
    if (Header is null || comprobante is null)
    {
      return false;
    }

    return !string.IsNullOrWhiteSpace(comprobante.Uuid)
      && string.Equals(Header.Rfc?.Trim(), comprobante.EmisorRfc?.Trim(), StringComparison.OrdinalIgnoreCase);
  }

  protected bool IsBancoMovimientoUnlinking(BankMovementDto movimiento)
    => movimiento is not null && movimiento.MovimientoId == _unlinkingBancoMovimientoId;

  protected bool IsComprobanteSelected(TransaccionCfdiCandidateDto comprobante)
    => comprobante is not null && comprobante.ComprobanteId == _selectedComprobanteId;

  protected void SelectComprobante(TransaccionCfdiCandidateDto comprobante)
  {
    _selectedComprobanteId = comprobante.ComprobanteId;
  }

  protected bool CanRegenerarMovimientos()
    => !IsRegeneratingMovimientos && Comprobantes.Count > 0;

  protected async Task RegenerarMovimientosDesdeComprobanteAsync()
  {
    if (Header is null)
    {
      return;
    }

    if (Comprobantes.Count == 0)
    {
      UiMessages.ShowWarning("No hay comprobantes vinculados para regenerar los movimientos.");
      return;
    }

    var confirmed = await ConfirmAsync("Estas Seguro que deseas crear los movimientos contables desde este CFDI?, Todos los Movimientos existentes seran borrados...");
    if (!confirmed)
    {
      return;
    }

    var comprobante = Comprobantes.FirstOrDefault(item => item.ComprobanteId == _selectedComprobanteId);
    if (comprobante is null)
    {
      UiMessages.ShowWarning("Selecciona un comprobante antes de regenerar los movimientos.");
      return;
    }

    var tipo = comprobante.Tipo?.Trim();
    if (string.IsNullOrWhiteSpace(tipo))
    {
      UiMessages.ShowWarning("No se pudo determinar el tipo del comprobante.");
      return;
    }

    IsRegeneratingMovimientos = true;
    await InvokeAsync(StateHasChanged);

    try
    {
      TransaccionCommandResult result;
      if (string.Equals(tipo, "CFDI", StringComparison.OrdinalIgnoreCase))
      {
        result = await TransaccionService.RegenerarPolizaDesdeComprobanteEnTransaccionAsync(
            Header.Id,
            comprobante.ComprobanteId);
      }
      else if (string.Equals(tipo, "COMP", StringComparison.OrdinalIgnoreCase))
      {
        result = await TransaccionService.RegenerarPolizaDesdeComplementoEnTransaccionAsync(
            Header.Id,
            comprobante.ComprobanteId);
      }
      else
      {
        UiMessages.ShowWarning($"Tipo de comprobante no soportado: {comprobante.Tipo}.");
        return;
      }

      if (!result.Success)
      {
        UiMessages.ShowError(result.Message ?? "No se pudieron regenerar los movimientos.");
        return;
      }

      UiMessages.ShowSuccess(result.Message ?? "Movimientos regenerados correctamente.");
      await PerformLoadAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudieron regenerar los movimientos: {ex.Message}");
    }
    finally
    {
      IsRegeneratingMovimientos = false;
      await InvokeAsync(StateHasChanged);
    }
  }

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

    _unlinkingComprobanteId = comprobante.ComprobanteId;
    await InvokeAsync(StateHasChanged);

    try
    {
      var request = new TransaccionComprobanteUnlinkRequest
      {
        CurrentTransaccionId = Header.Id,
        ComprobanteId = comprobante.ComprobanteId,
        Tipo = comprobante.Tipo
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

  protected async Task CancelComprobanteCfdiAsync(TransaccionCfdiCandidateDto comprobante)
  {
    if (Header is null || comprobante is null)
    {
      return;
    }

    if (!CanCancelComprobante(comprobante))
    {
      UiMessages.ShowWarning("Solo se pueden cancelar CFDIs emitidos por el RFC actual y con UUID.");
      return;
    }

    var confirmed = await ConfirmAsync($"¿Seguro que deseas cancelar el CFDI {comprobante.Uuid}? Esta acción se enviará a Facturama y no se puede deshacer.");
    if (!confirmed)
    {
      return;
    }

    _cancellingComprobanteId = comprobante.ComprobanteId;
    await InvokeAsync(StateHasChanged);

    try
    {
      await DeclaracionPreviaService.CancelEmitidaAsync(comprobante.Uuid!, (int)comprobante.ComprobanteId);
      var unlinkResult = await TransaccionService.UnlinkComprobanteAsync(new TransaccionComprobanteUnlinkRequest
      {
        CurrentTransaccionId = Header.Id,
        ComprobanteId = comprobante.ComprobanteId,
        Tipo = comprobante.Tipo
      });

      if (!unlinkResult.Success)
      {
        UiMessages.ShowWarning($"Cancelación solicitada para CFDI {comprobante.Uuid}, pero no se pudo desligar de esta póliza: {unlinkResult.Message}");
      }
      else
      {
        UiMessages.ShowSuccess($"Cancelación solicitada para CFDI {comprobante.Uuid} y comprobante desligado.");
      }

      await ReloadComprobantesAsync();
      await ReloadAttachmentsAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cancelar el CFDI: {ex.Message}");
    }
    finally
    {
      _cancellingComprobanteId = null;
      await InvokeAsync(StateHasChanged);
    }
  }

  protected async Task UnlinkBancoMovimientoAsync(BankMovementDto movimiento)
  {
    if (movimiento is null)
    {
      return;
    }

    var confirmed = await ConfirmAsync("¿Estás seguro que deseas desligar este movimiento bancario de la póliza?");
    if (!confirmed)
    {
      return;
    }

    _unlinkingBancoMovimientoId = movimiento.MovimientoId;
    await InvokeAsync(StateHasChanged);

    try
    {
      await BancosService.UnlinkMovementAsync(movimiento.MovimientoId);
      UiMessages.ShowSuccess("Movimiento bancario desligado correctamente.");
      await ReloadBancoMovimientosAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo desligar el movimiento bancario: {ex.Message}");
    }
    finally
    {
      _unlinkingBancoMovimientoId = null;
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

    var moveConfirmMessage =
      $"Este XML está ligado a un comprobante, pero no a esta Póliza.\n" +
      "No se borrará; quedará sin póliza asignada hasta que se vuelva a ligar.\n" +
      "¿Deseas continuar?";

    var moveConfirmed = await ConfirmAsync(moveConfirmMessage);
    if (!moveConfirmed)
      return;

    await ExecuteAttachmentMutationAsync(
        attachment.Id,
        () => TransaccionService.SetAttachmentTransaccionAsync(attachment.Id, null),
        "XML retirado de la póliza actual.");
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

  protected async Task DeleteMovimientoAsync(MovimientoModel movimiento)
  {
    if (movimiento is null || Header is null)
      return;

    var movementName = string.IsNullOrWhiteSpace(movimiento.NombreCuenta)
      ? movimiento.Concepto ?? "seleccionado"
      : movimiento.NombreCuenta;
    var confirm = await ConfirmAsync($"¿Deseas eliminar el movimiento '{movementName}'?");
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
      await ResetAttachmentInputAsync();
      return;
    }

    var file = args.File;
    if (file is null)
    {
      await ResetAttachmentInputAsync();
      return;
    }

    if (file.Size == 0)
    {
      UiMessages.ShowError("El archivo seleccionado está vacío.");
      await ResetAttachmentInputAsync();
      return;
    }

    if (file.Size > AttachmentMaxFileSize)
    {
      UiMessages.ShowError("El archivo excede el tamaño máximo permitido (5 MB).");
      await ResetAttachmentInputAsync();
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
      await ResetAttachmentInputAsync();
    }
  }

  private async Task ResetAttachmentInputAsync()
  {
    _attachmentInputKey++;
    await InvokeAsync(StateHasChanged);
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
    CancelProyectoSearch();
    GC.SuppressFinalize(this);
  }

  private void OnRfcStateChanged()
  {
    if (_isDisposed)
    {
      return;
    }

    _ = InvokeAsync(HandleRfcStateChangedAsync);
  }

  private async Task HandleRfcStateChangedAsync()
  {
    if (_isDisposed)
    {
      return;
    }

    await PerformLoadAsync();
    StateHasChanged();
  }

  protected sealed class TransaccionHeaderModel
  {
    private string? _concepto;

    public int Id { get; set; }
    public string? Folio { get; set; }
    public string? Rfc { get; set; }

    [Required(ErrorMessage = "La fecha es obligatoria.")]
    public DateTime Fecha { get; set; }

    public string? Cuenta { get; set; }

    [Required(ErrorMessage = "Captura un concepto.")]
    public string? Concepto
    {
      get => _concepto;
      set => _concepto = NormalizeConceptoValue(value);
    }

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
    private string? _concepto;

    public int Id { get; set; }

    public int? CuentaId { get; set; }

    [Required(ErrorMessage = "La cuenta es obligatoria.")]

    public string? NombreCuenta { get; set; }

    public string? Nivel1 { get; set; }

    public string? Nivel2 { get; set; }

    public string? Nivel3 { get; set; }

    public string? Descripcion { get; set; }

    [Required(ErrorMessage = "El concepto es obligatorio.")]
    public string? Concepto
    {
      get => _concepto;
      set => _concepto = NormalizeConceptoValue(value);
    }

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

  protected sealed record class PublicoMonthOption(string Code, string Name);

  private static string FormatSize(long bytes)
  {
    if (bytes <= 0)
      return "0 B";

    var units = new[] { "B", "KB", "MB", "GB", "TB" };
    var magnitude = (int)Math.Min(units.Length - 1, Math.Log(bytes, 1024));
    var adjusted = bytes / Math.Pow(1024, magnitude);
    return $"{adjusted:0.##} {units[magnitude]}";
  }

  private static string? NormalizeConceptoValue(string? value)
    => value is null ? null : value.ToUpperInvariant();
}
