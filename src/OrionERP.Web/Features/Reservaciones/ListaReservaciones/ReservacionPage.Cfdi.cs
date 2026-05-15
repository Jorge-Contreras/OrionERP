using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using OrionERP.Application.Features.Cfdi.DeclaracionPrevia;
using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.Application.Features.Reservaciones.Cfdi;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.Web.Features.Reservaciones.ListaReservaciones;

public partial class ReservacionPage
{
  private const string NewCfdiPolizaOption = "new";
  private const string DefaultCfdiUse = "G03";
  private const string DefaultCfdiFormaPago = "03";
  private const string DeferredCfdiFormaPago = "99";
  private const string DefaultCfdiMetodoPago = "PUE";
  private const string DeferredCfdiMetodoPago = "PPD";

  private static readonly IReadOnlyList<FormaPagoLookupDto> FallbackCfdiFormaPagoOptions =
  [
    new() { Clave = "01", Descripcion = "Efectivo" },
    new() { Clave = "03", Descripcion = "Transferencia electronica de fondos" },
    new() { Clave = "04", Descripcion = "Tarjeta de credito" },
    new() { Clave = "28", Descripcion = "Tarjeta de debito" },
    new() { Clave = "99", Descripcion = "Por definir" }
  ];

  private static readonly IReadOnlyList<LookupStringDto> CfdiMetodoCatalogOptions =
  [
    new() { Id = DefaultCfdiMetodoPago, Description = "Pago en una sola exhibicion" },
    new() { Id = DeferredCfdiMetodoPago, Description = "Pago en parcialidades o diferido" }
  ];

  private static readonly IReadOnlyList<LookupStringDto> CfdiUseCatalogOptions =
  [
    new() { Id = "G01", Description = "Adquisicion de mercancias" },
    new() { Id = "G02", Description = "Devoluciones, descuentos o bonificaciones" },
    new() { Id = "G03", Description = "Gastos en general" },
    new() { Id = "I01", Description = "Construcciones" },
    new() { Id = "I02", Description = "Mobiliario y equipo de oficina por inversiones" },
    new() { Id = "I03", Description = "Equipo de transporte" },
    new() { Id = "I04", Description = "Equipo de computo y accesorios" },
    new() { Id = "I05", Description = "Dados, troqueles, moldes, matrices y herramental" },
    new() { Id = "I06", Description = "Comunicaciones telefonicas" },
    new() { Id = "I07", Description = "Comunicaciones satelitales" },
    new() { Id = "I08", Description = "Otra maquinaria y equipo" },
    new() { Id = "D01", Description = "Honorarios medicos, dentales y gastos hospitalarios" },
    new() { Id = "D02", Description = "Gastos medicos por incapacidad o discapacidad" },
    new() { Id = "D03", Description = "Gastos funerales" },
    new() { Id = "D04", Description = "Donativos" },
    new() { Id = "D05", Description = "Intereses reales efectivamente pagados por creditos hipotecarios" },
    new() { Id = "D06", Description = "Aportaciones voluntarias al SAR" },
    new() { Id = "D07", Description = "Primas por seguros de gastos medicos" },
    new() { Id = "D08", Description = "Gastos de transportacion escolar obligatoria" },
    new() { Id = "D09", Description = "Depositos en cuentas para el ahorro, primas o planes de pensiones" },
    new() { Id = "D10", Description = "Pagos por servicios educativos" },
    new() { Id = "S01", Description = "Sin efectos fiscales" },
    new() { Id = "CP01", Description = "Pagos" },
    new() { Id = "CN01", Description = "Nomina" }
  ];

  [Inject] public IReservationCfdiService ReservationCfdiService { get; set; } = default!;
  [Inject] public IDeclaracionPreviaService DeclaracionPreviaService { get; set; } = default!;

  protected ReservationCfdiContextDto? CfdiContext { get; set; }
  protected ReservationFacturacionStatusDto? FacturacionStatus { get; set; }
  protected List<FormaPagoLookupDto> CfdiFormaPagoOptions { get; } = [];
  protected List<ReservationCfdiCustomerSuggestionDto> CfdiCustomerSuggestions { get; } = new();
  protected ReservationCfdiCustomerUpsertRequest CfdiReceiver { get; set; } = new();
  protected ReservationCfdiReceiverValidationDto? CfdiReceiverValidation { get; set; }
  protected string CfdiCustomerSearchText { get; set; } = string.Empty;
  protected string SelectedCfdiPolizaOption { get; set; } = NewCfdiPolizaOption;
  protected string SelectedCfdiFormaPago { get; set; } = DefaultCfdiFormaPago;
  protected string SelectedCfdiMetodoPago { get; set; } = DefaultCfdiMetodoPago;
  protected string? LastCfdiReceiverValidationSignature { get; set; }
  protected bool ShowCfdiPanel { get; set; }
  protected bool ShowCfdiCustomerResults { get; set; }
  protected bool PersistCfdiCustomer { get; set; } = true;
  protected bool IsLoadingCfdiContext { get; set; }
  protected bool IsSearchingCfdiCustomers { get; set; }
  protected bool IsSavingCfdiCustomer { get; set; }
  protected bool IsValidatingCfdiReceiver { get; set; }
  protected bool IsCreatingCfdi { get; set; }
  protected long? CancellingReservationCfdiId { get; set; }
  protected string? CfdiErrorMessage { get; set; }

  protected bool HasCfdiDiscounts => CfdiContext?.Items.Any(item => item.Discount > 0m) == true;

  protected bool ShouldShowCfdiCreationPanel
    => ShowCfdiPanel && !HasReservationFacturacionEvidence;

  protected bool HasExistingReservationCfdis
    => CfdiContext?.ExistingDocuments.Count > 0;

  protected bool HasReservationFacturacionEvidence
    => FacturacionStatus?.HasAnyFacturacionEvidence == true || HasExistingReservationCfdis;

  protected bool IsCfdiPaymentFormLocked
    => string.Equals(SelectedCfdiMetodoPago, DeferredCfdiMetodoPago, StringComparison.OrdinalIgnoreCase);

  protected bool CanValidateCfdiReceiver
    => !string.IsNullOrWhiteSpace(CfdiReceiver.Rfc)
       && !string.IsNullOrWhiteSpace(CfdiReceiver.FiscalName)
       && !string.IsNullOrWhiteSpace(CfdiReceiver.TaxZipCode)
       && !string.IsNullOrWhiteSpace(CfdiReceiver.FiscalRegime)
       && !string.IsNullOrWhiteSpace(CfdiReceiver.CfdiUse);

  protected bool HasFreshCfdiReceiverValidation
    => CfdiReceiverValidation is not null
       && string.Equals(
           LastCfdiReceiverValidationSignature,
           BuildCfdiReceiverValidationSignature(CfdiReceiver),
           StringComparison.Ordinal);

  protected bool IsCfdiReceiverValidationStale
    => CfdiReceiverValidation is not null && !HasFreshCfdiReceiverValidation;

  protected bool CanCreateReservationCfdi
    => CfdiContext is not null
       && !CfdiContext.HasUnsupportedIsh
       && !HasReservationFacturacionEvidence
       && !string.IsNullOrWhiteSpace(CfdiReceiver.CfdiUse)
       && !string.IsNullOrWhiteSpace(SelectedCfdiFormaPago)
       && !string.IsNullOrWhiteSpace(SelectedCfdiMetodoPago);

  protected IReadOnlyList<LookupStringDto> CfdiMetodoPagoOptions => CfdiMetodoCatalogOptions;
  protected IReadOnlyList<LookupStringDto> CfdiUsoCfdiOptions => CfdiUseCatalogOptions;

  protected bool IsCreatingNewCfdiPoliza
    => string.Equals(SelectedCfdiPolizaOption, NewCfdiPolizaOption, StringComparison.OrdinalIgnoreCase);

  protected bool IsCancellingReservationCfdi(ReservationCfdiLinkedDocumentDto document)
    => document is not null && CancellingReservationCfdiId == document.ComprobanteId;

  protected async Task ToggleCfdiPanelAsync()
  {
    if (ShowCfdiPanel)
    {
      ShowCfdiPanel = false;
      ShowCfdiCustomerResults = false;
      return;
    }

    await OpenCfdiPanelAsync(forceReload: true);
  }

  protected async Task ReloadCfdiContextAsync()
  {
    await OpenCfdiPanelAsync(forceReload: true);
  }

  protected Task OnCfdiFormaPagoChanged(ChangeEventArgs args)
  {
    SelectedCfdiFormaPago = NormalizeCfdiSelection(args.Value?.ToString(), DefaultCfdiFormaPago);
    EnsureCfdiSelectionDefaults();
    return Task.CompletedTask;
  }

  protected Task OnCfdiMetodoPagoChanged(ChangeEventArgs args)
  {
    SelectedCfdiMetodoPago = NormalizeCfdiSelection(args.Value?.ToString(), DefaultCfdiMetodoPago);

    if (!string.Equals(SelectedCfdiMetodoPago, DeferredCfdiMetodoPago, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(SelectedCfdiFormaPago, DeferredCfdiFormaPago, StringComparison.OrdinalIgnoreCase))
    {
      SelectedCfdiFormaPago = DefaultCfdiFormaPago;
    }

    EnsureCfdiSelectionDefaults();
    return Task.CompletedTask;
  }

  protected async Task OnCfdiCustomerInputChangedAsync(ChangeEventArgs args)
  {
    CfdiCustomerSearchText = args.Value?.ToString() ?? string.Empty;
    CfdiReceiver.BusinessPartnerId = null;

    if (string.IsNullOrWhiteSpace(CfdiCustomerSearchText))
    {
      ShowCfdiCustomerResults = false;
      CfdiCustomerSuggestions.Clear();
      return;
    }

    await SearchCfdiCustomersAsync(allowEmptySearch: false);
  }

  protected async Task OnCfdiCustomerInputKeyDownAsync(KeyboardEventArgs args)
  {
    if (!IsClienteSearchTriggerKey(args))
    {
      return;
    }

    await SearchCfdiCustomersAsync(allowEmptySearch: true);
  }

  protected async Task SearchCfdiCustomersAsync(bool allowEmptySearch)
  {
    var searchText = string.IsNullOrWhiteSpace(CfdiCustomerSearchText)
      ? null
      : CfdiCustomerSearchText.Trim();

    if (!allowEmptySearch && string.IsNullOrWhiteSpace(searchText))
    {
      ShowCfdiCustomerResults = false;
      CfdiCustomerSuggestions.Clear();
      return;
    }

    IsSearchingCfdiCustomers = true;
    CfdiErrorMessage = null;

    try
    {
      var results = await ReservationCfdiService.SearchCustomersAsync(searchText);
      CfdiCustomerSuggestions.Clear();
      CfdiCustomerSuggestions.AddRange(results);
      ShowCfdiCustomerResults = allowEmptySearch || !string.IsNullOrWhiteSpace(searchText);
    }
    catch (Exception ex)
    {
      CfdiErrorMessage = ex.Message;
      UiMessages.ShowError($"No se pudieron cargar clientes fiscales. {ex.Message}");
    }
    finally
    {
      IsSearchingCfdiCustomers = false;
    }
  }

  protected void SelectCfdiCustomer(ReservationCfdiCustomerSuggestionDto suggestion)
  {
    CfdiReceiver = new ReservationCfdiCustomerUpsertRequest
    {
      BusinessPartnerId = suggestion.BusinessPartnerId,
      DisplayName = suggestion.DisplayName,
      Rfc = suggestion.Rfc,
      FiscalName = suggestion.FiscalName,
      TaxZipCode = suggestion.TaxZipCode,
      FiscalRegime = suggestion.FiscalRegime,
      CfdiUse = suggestion.CfdiUse,
      Email = suggestion.Email
    };

    CfdiCustomerSearchText = !string.IsNullOrWhiteSpace(suggestion.DisplayName)
      ? suggestion.DisplayName
      : suggestion.Rfc;
    PersistCfdiCustomer = !suggestion.IsPersisted;
    ShowCfdiCustomerResults = false;
    CfdiErrorMessage = null;
    ResetCfdiReceiverValidation();
    EnsureCfdiSelectionDefaults();
  }

  protected async Task SaveCfdiCustomerAsync()
  {
    IsSavingCfdiCustomer = true;
    CfdiErrorMessage = null;

    try
    {
      var result = await ReservationCfdiService.SaveCustomerAsync(CloneReceiver(CfdiReceiver));
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      CfdiReceiver.BusinessPartnerId = result.BusinessPartnerId;
      PersistCfdiCustomer = false;
      UiMessages.ShowSuccess(result.Message);
      await SearchCfdiCustomersAsync(allowEmptySearch: true);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo guardar el cliente fiscal. {ex.Message}");
    }
    finally
    {
      IsSavingCfdiCustomer = false;
    }
  }

  protected Task ValidateCfdiReceiverAsync()
    => RefreshCfdiReceiverValidationAsync(showToast: true);

  protected async Task CrearCfdiReservacionAsync()
  {
    if (string.IsNullOrWhiteSpace(RfcState.CurrentRfc))
    {
      UiMessages.ShowError("Selecciona un RFC antes de crear el CFDI.");
      return;
    }

    var saveOk = await SaveReservationStateAsync(showSuccessMessage: false);
    if (!saveOk)
    {
      return;
    }

    var currentReceiver = CloneReceiver(CfdiReceiver);
    var currentSearchText = CfdiCustomerSearchText;
    var currentPersistFlag = PersistCfdiCustomer;
    var currentPolizaSelection = SelectedCfdiPolizaOption;
    var currentFormaPago = SelectedCfdiFormaPago;
    var currentMetodoPago = SelectedCfdiMetodoPago;
    var currentValidation = CfdiReceiverValidation;
    var currentValidationSignature = LastCfdiReceiverValidationSignature;

    await LoadCfdiContextAsync(forceReload: true);
    if (CfdiContext is null)
    {
      return;
    }

    CfdiReceiver = currentReceiver;
    CfdiCustomerSearchText = currentSearchText;
    PersistCfdiCustomer = currentPersistFlag;
    SelectedCfdiPolizaOption = currentPolizaSelection;
    SelectedCfdiFormaPago = currentFormaPago;
    SelectedCfdiMetodoPago = currentMetodoPago;
    CfdiReceiverValidation = currentValidation;
    LastCfdiReceiverValidationSignature = currentValidationSignature;
    EnsureCfdiSelectionDefaults();

    if (IsCfdiReceiverValidationStale)
    {
      UiMessages.ShowWarning("Los datos del receptor cambiaron después de la última validación. Vuelve a validar antes de timbrar.");
      return;
    }

    if (HasFreshCfdiReceiverValidation && CfdiReceiverValidation?.BlocksStamping == true)
    {
      UiMessages.ShowWarning(CfdiReceiverValidation.Message);
      return;
    }

    IsCreatingCfdi = true;
    CfdiErrorMessage = null;

    try
    {
      var result = await ReservationCfdiService.CreateCfdiAsync(new ReservationCfdiCreateRequest
      {
        ReservationId = ReservationId,
        IssuerRfc = RfcState.CurrentRfc!,
        CreateNewPoliza = IsCreatingNewCfdiPoliza,
        TransaccionId = ResolveSelectedCfdiTransaccionId(),
        PersistCustomer = PersistCfdiCustomer,
        FormaPago = SelectedCfdiFormaPago,
        MetodoPago = SelectedCfdiMetodoPago,
        Receiver = CloneReceiver(CfdiReceiver)
      });

      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      await LoadAllAsync();
      ShowCfdiPanel = false;
      await LoadCfdiContextAsync(forceReload: true);
      UiMessages.ShowSuccess(result.Message);
    }
    catch (Exception ex)
    {
      if (ShouldRefreshCfdiReceiverValidation(ex))
      {
        await RefreshCfdiReceiverValidationAsync(showToast: false, suppressErrorToast: true);
      }

      UiMessages.ShowError($"No se pudo crear el CFDI. {ex.Message}");
    }
    finally
    {
      IsCreatingCfdi = false;
    }
  }

  protected async Task CancelarCfdiReservacionAsync(ReservationCfdiLinkedDocumentDto document)
  {
    if (document is null)
    {
      return;
    }

    if (string.IsNullOrWhiteSpace(document.Uuid))
    {
      UiMessages.ShowWarning("El CFDI seleccionado no tiene UUID para solicitar la cancelación.");
      return;
    }

    var confirmed = await Js.InvokeAsync<bool>(
      "confirm",
      $"¿Seguro que deseas cancelar el CFDI {document.Uuid}?\nEsta acción se enviará a Facturama y no se puede deshacer.");

    if (!confirmed)
    {
      return;
    }

    CancellingReservationCfdiId = document.ComprobanteId;
    CfdiErrorMessage = null;

    try
    {
      await DeclaracionPreviaService.CancelEmitidaAsync(document.Uuid, (int)document.ComprobanteId);
      UiMessages.ShowSuccess($"Cancelación solicitada para CFDI {document.Uuid}.");
      await LoadCfdiContextAsync(forceReload: true);
      await LoadAllAsync();
    }
    catch (Exception ex)
    {
      CfdiErrorMessage = ex.Message;
      UiMessages.ShowError($"No se pudo cancelar el CFDI. {ex.Message}");
    }
    finally
    {
      CancellingReservationCfdiId = null;
    }
  }

  protected string GetCfdiPolizaStatusClass(ReservationCfdiPolizaOptionDto option)
  {
    if (option.IsEligible)
    {
      return "text-bg-success";
    }

    if (option.HasExistingCfdi)
    {
      return "text-bg-danger";
    }

    if (!option.MatchesReservationTotal)
    {
      return "text-bg-warning";
    }

    return "text-bg-secondary";
  }

  protected string FormatCfdiPolizaLabel(ReservationCfdiPolizaOptionDto option)
  {
    var amount = option.Monto.ToString("C", CultureInfo.CurrentCulture);
    return $"Poliza {option.TransaccionId} | {option.Fecha:yyyy-MM-dd} | {amount}";
  }

  protected string GetCfdiReceiverValidationAlertClass()
  {
    if (CfdiReceiverValidation is null)
    {
      return "alert-secondary";
    }

    if (IsCfdiReceiverValidationStale)
    {
      return "alert-warning";
    }

    if (CfdiReceiverValidation.IsValid)
    {
      return "alert-success";
    }

    return CfdiReceiverValidation.BlocksStamping ? "alert-danger" : "alert-warning";
  }

  protected string GetCfdiReceiverValidationStatusBadgeClass()
  {
    if (CfdiReceiverValidation is null)
    {
      return "text-bg-secondary";
    }

    if (IsCfdiReceiverValidationStale)
    {
      return "text-bg-warning";
    }

    if (CfdiReceiverValidation.IsValid)
    {
      return "text-bg-success";
    }

    return CfdiReceiverValidation.BlocksStamping ? "text-bg-danger" : "text-bg-warning";
  }

  protected string GetCfdiReceiverValidationStatusLabel()
  {
    if (CfdiReceiverValidation is null)
    {
      return "Sin validar";
    }

    if (IsCfdiReceiverValidationStale)
    {
      return "Cambios pendientes";
    }

    if (CfdiReceiverValidation.IsValid)
    {
      return "Valido";
    }

    return CfdiReceiverValidation.BlocksStamping ? "Bloquea timbrado" : "Advertencia";
  }

  protected static string GetCfdiReceiverFlagBadgeClass(bool matches)
    => matches ? "text-bg-success" : "text-bg-danger";

  protected string GetFacturacionStatusBadgeClass()
    => FacturacionStatus?.Status switch
    {
      ReservationFacturacionStatuses.Facturada => "text-bg-success",
      ReservationFacturacionStatuses.Parcial => "text-bg-warning",
      _ => "text-bg-secondary"
    };

  protected string GetPaymentFacturacionBadgeClass(ReservacionPagoDto pago)
  {
    var payment = GetPaymentFacturacionStatus(pago.TransaccionId);
    if (payment?.IsFacturado == true)
    {
      return payment.RegularCfdiCount > 0 && payment.Pago20Count > 0
          ? "text-bg-success"
          : "text-bg-info";
    }

    return "text-bg-secondary";
  }

  protected string GetPaymentFacturacionLabel(ReservacionPagoDto pago)
  {
    var payment = GetPaymentFacturacionStatus(pago.TransaccionId);
    if (payment is null || !payment.IsFacturado)
    {
      return "Sin CFDI";
    }

    if (payment.RegularCfdiCount > 0 && payment.Pago20Count > 0)
    {
      return "CFDI + Pago20";
    }

    return payment.Pago20Count > 0 ? "Pago20" : "CFDI";
  }

  protected string GetPaymentFacturacionTitle(ReservacionPagoDto pago)
  {
    var payment = GetPaymentFacturacionStatus(pago.TransaccionId);
    if (payment is null || payment.Documents.Count == 0)
    {
      return "No se encontraron comprobantes activos ligados a esta poliza.";
    }

    return string.Join(
        Environment.NewLine,
        payment.Documents.Select(document =>
            $"{document.EvidenceType} {document.ComprobanteId}"
            + (document.DoctoRelacionadoId.HasValue ? $" / Docto {document.DoctoRelacionadoId}" : string.Empty)
            + (!string.IsNullOrWhiteSpace(document.Uuid) ? $" / {document.Uuid}" : string.Empty)));
  }

  protected string GetFacturacionSummaryLabel()
  {
    var status = FacturacionStatus;
    if (status is null)
    {
      return "Sin revisar";
    }

    return $"{status.Status} ({status.FacturadoPaymentCount}/{status.PaymentCount} pagos)";
  }

  protected async Task LoadReservationFacturacionStatusAsync()
  {
    try
    {
      FacturacionStatus = await ReservationCfdiService.GetFacturacionStatusAsync(ReservationId);
    }
    catch (Exception ex)
    {
      FacturacionStatus = ReservationFacturacionStatusCalculator.Calculate(Array.Empty<ReservationPaymentFacturacionStatusDto>());
      UiMessages.ShowError($"No se pudo cargar el estado de facturacion. {ex.Message}");
    }
  }

  private async Task OpenCfdiPanelAsync(bool forceReload)
  {
    if (!forceReload && ShowCfdiPanel && CfdiContext is not null)
    {
      return;
    }

    ShowCfdiPanel = true;
    ShowCfdiCustomerResults = false;
    await LoadCfdiContextAsync(forceReload);
  }

  private async Task LoadCfdiContextAsync(bool forceReload)
  {
    if (!forceReload && CfdiContext is not null)
    {
      return;
    }

    if (string.IsNullOrWhiteSpace(RfcState.CurrentRfc))
    {
      UiMessages.ShowError("Selecciona un RFC antes de preparar el CFDI.");
      return;
    }

    IsLoadingCfdiContext = true;
    CfdiErrorMessage = null;

    try
    {
      await EnsureCfdiCatalogsLoadedAsync();
      CfdiContext = await ReservationCfdiService.GetContextAsync(ReservationId, RfcState.CurrentRfc!);
      if (CfdiContext is null)
      {
        CfdiErrorMessage = "No se pudo preparar el contexto del CFDI para esta reservación.";
        return;
      }

      ApplyCfdiContext(CfdiContext);
      if (HasReservationFacturacionEvidence)
      {
        ShowCfdiPanel = false;
      }
    }
    catch (Exception ex)
    {
      CfdiErrorMessage = ex.Message;
      UiMessages.ShowError($"No se pudo preparar el CFDI. {ex.Message}");
    }
    finally
    {
      IsLoadingCfdiContext = false;
    }
  }

  private void ApplyCfdiContext(ReservationCfdiContextDto context)
  {
    CfdiCustomerSuggestions.Clear();
    CfdiCustomerSuggestions.AddRange(context.SuggestedCustomers);
    CfdiReceiver = CloneReceiver(context.ReceiverDraft);
    CfdiCustomerSearchText = FirstNonEmpty(context.ReceiverDraft.DisplayName, context.ReceiverDraft.Rfc);
    PersistCfdiCustomer = !context.ReceiverDraft.BusinessPartnerId.HasValue;
    SelectedCfdiPolizaOption = context.AutoSelectedTransaccionId?.ToString(CultureInfo.InvariantCulture)
      ?? NewCfdiPolizaOption;
    ShowCfdiCustomerResults = false;
    ResetCfdiReceiverValidation();
    EnsureCfdiSelectionDefaults();
  }

  private ReservationPaymentFacturacionStatusDto? GetPaymentFacturacionStatus(int transaccionId)
    => FacturacionStatus?.Payments.FirstOrDefault(payment => payment.TransaccionId == transaccionId);

  private static int? ResolveSelectedCfdiTransaccionId(string? selectedValue)
  {
    return int.TryParse(selectedValue, NumberStyles.None, CultureInfo.InvariantCulture, out var transaccionId)
      ? transaccionId
      : (int?)null;
  }

  private int? ResolveSelectedCfdiTransaccionId()
    => ResolveSelectedCfdiTransaccionId(SelectedCfdiPolizaOption);

  private static ReservationCfdiCustomerUpsertRequest CloneReceiver(ReservationCfdiCustomerUpsertRequest source)
    => new()
    {
      BusinessPartnerId = source.BusinessPartnerId,
      DisplayName = source.DisplayName,
      Rfc = source.Rfc,
      FiscalName = source.FiscalName,
      TaxZipCode = source.TaxZipCode,
      FiscalRegime = source.FiscalRegime,
      CfdiUse = source.CfdiUse,
      Email = source.Email
    };

  private async Task<bool> RefreshCfdiReceiverValidationAsync(bool showToast, bool suppressErrorToast = false)
  {
    if (!CanValidateCfdiReceiver)
    {
      if (!suppressErrorToast)
      {
        UiMessages.ShowWarning("Completa RFC, razón social, CP fiscal, régimen y uso CFDI antes de validar el receptor.");
      }

      return false;
    }

    IsValidatingCfdiReceiver = true;
    CfdiErrorMessage = null;

    try
    {
      var validation = await ReservationCfdiService.ValidateReceiverAsync(CloneReceiver(CfdiReceiver));
      CfdiReceiverValidation = validation;
      LastCfdiReceiverValidationSignature = BuildCfdiReceiverValidationSignature(CfdiReceiver);

      if (showToast)
      {
        if (validation.IsValid)
        {
          UiMessages.ShowSuccess(validation.Message);
        }
        else if (validation.BlocksStamping)
        {
          UiMessages.ShowWarning(validation.Message);
        }
        else
        {
          UiMessages.ShowInfo(validation.Message);
        }
      }

      return true;
    }
    catch (Exception ex)
    {
      CfdiErrorMessage = ex.Message;
      if (!suppressErrorToast)
      {
        UiMessages.ShowError($"No se pudo validar el receptor. {ex.Message}");
      }

      return false;
    }
    finally
    {
      IsValidatingCfdiReceiver = false;
    }
  }

  private async Task EnsureCfdiCatalogsLoadedAsync()
  {
    if (CfdiFormaPagoOptions.Count > 0)
    {
      return;
    }

    var options = await TransaccionService.GetFormasPagoAsync();
    CfdiFormaPagoOptions.Clear();
    CfdiFormaPagoOptions.AddRange(options);

    if (CfdiFormaPagoOptions.Count == 0)
    {
      CfdiFormaPagoOptions.AddRange(FallbackCfdiFormaPagoOptions);
    }
  }

  private void EnsureCfdiSelectionDefaults()
  {
    if (string.IsNullOrWhiteSpace(CfdiReceiver.CfdiUse))
    {
      CfdiReceiver.CfdiUse = DefaultCfdiUse;
    }

    if (string.IsNullOrWhiteSpace(SelectedCfdiMetodoPago))
    {
      SelectedCfdiMetodoPago = DefaultCfdiMetodoPago;
    }

    if (string.IsNullOrWhiteSpace(SelectedCfdiFormaPago))
    {
      SelectedCfdiFormaPago = DefaultCfdiFormaPago;
    }

    if (IsCfdiPaymentFormLocked)
    {
      SelectedCfdiFormaPago = DeferredCfdiFormaPago;
      return;
    }

    if (string.Equals(SelectedCfdiFormaPago, DeferredCfdiFormaPago, StringComparison.OrdinalIgnoreCase))
    {
      SelectedCfdiMetodoPago = DeferredCfdiMetodoPago;
      SelectedCfdiFormaPago = DeferredCfdiFormaPago;
      return;
    }

    if (string.IsNullOrWhiteSpace(SelectedCfdiFormaPago))
    {
      SelectedCfdiFormaPago = DefaultCfdiFormaPago;
    }
  }

  private void ResetCfdiReceiverValidation()
  {
    CfdiReceiverValidation = null;
    LastCfdiReceiverValidationSignature = null;
  }

  private static bool ShouldRefreshCfdiReceiverValidation(Exception ex)
  {
    var message = ex.Message;
    if (string.IsNullOrWhiteSpace(message))
    {
      return false;
    }

    return message.Contains("Facturama no validó al receptor", StringComparison.OrdinalIgnoreCase)
           || message.Contains("codigo postal no coincide", StringComparison.OrdinalIgnoreCase)
           || message.Contains("razon social no coincide", StringComparison.OrdinalIgnoreCase)
           || message.Contains("regimen fiscal no coincide", StringComparison.OrdinalIgnoreCase)
           || message.Contains("RFC no localizado", StringComparison.OrdinalIgnoreCase);
  }

  private static string NormalizeCfdiSelection(string? value, string fallbackValue)
    => string.IsNullOrWhiteSpace(value)
      ? fallbackValue
      : value.Trim().ToUpperInvariant();

  private static string BuildCfdiReceiverValidationSignature(ReservationCfdiCustomerUpsertRequest receiver)
    => string.Join("|",
        NormalizeCfdiSignaturePart(receiver.Rfc, true),
        NormalizeCfdiSignaturePart(receiver.FiscalName),
        NormalizeCfdiSignaturePart(receiver.TaxZipCode),
        NormalizeCfdiSignaturePart(receiver.FiscalRegime, true),
        NormalizeCfdiSignaturePart(receiver.CfdiUse, true));

  private static string NormalizeCfdiSignaturePart(string? value, bool upperCase = false)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return string.Empty;
    }

    var normalized = value.Trim();
    return upperCase ? normalized.ToUpperInvariant() : normalized;
  }

  private static string FirstNonEmpty(params string?[] values)
  {
    foreach (var value in values)
    {
      if (!string.IsNullOrWhiteSpace(value))
      {
        return value.Trim();
      }
    }

    return string.Empty;
  }
}
