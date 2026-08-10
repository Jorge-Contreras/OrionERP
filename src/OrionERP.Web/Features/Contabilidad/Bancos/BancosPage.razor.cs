using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using OrionERP.Application.Features.Contabilidad.Bancos;
using OrionERP.Application.Features.Contabilidad.ContabilidadRegistros;
using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.Web.Services;
using OrionERP.Web.State;

namespace OrionERP.Web.Features.Contabilidad.Bancos;

public partial class BancosPage : ComponentBase, IDisposable
{
  private static readonly IReadOnlyList<KeyValuePair<int, string>> MonthOptionsInternal = new List<KeyValuePair<int, string>>(12)
  {
    new(1, "Enero"),
    new(2, "Febrero"),
    new(3, "Marzo"),
    new(4, "Abril"),
    new(5, "Mayo"),
    new(6, "Junio"),
    new(7, "Julio"),
    new(8, "Agosto"),
    new(9, "Septiembre"),
    new(10, "Octubre"),
    new(11, "Noviembre"),
    new(12, "Diciembre"),
  };

  private static readonly CultureInfo CurrencyCulture = new("es-MX");
  private const decimal BankAccountingDifferenceTolerance = 1m;
  private const string MovementOrderBankNewest = "bank-newest";
  private const string MovementOrderAccounting = "accounting";

  private CancellationTokenSource? _movementsCts;
  private CancellationTokenSource? _pendingTransactionsCts;
  private CancellationTokenSource? _textFilterDebounceCts;

  private string? _currentRfc;
  private readonly Dictionary<int, IReadOnlyList<TransaccionMovimientoDto>> _accountingDetailsByPolicy = new();
  private readonly HashSet<int> _expandedPolicyIds = new();
  private readonly HashSet<int> _loadingPolicyDetailIds = new();
  private readonly HashSet<int> _reorderingPolicyIds = new();
  private bool _bankImportWarningAcknowledged;

  [Inject] public IBancosService BancosService { get; set; } = default!;
  [Inject] public ITransaccionService TransaccionService { get; set; } = default!;
  [Inject] public IContabilidadRegistrosService RegistrosService { get; set; } = default!;
  [Inject] public IUserRfcState RfcState { get; set; } = default!;
  [Inject] public IUiMessageService UiMessages { get; set; } = default!;
  [Inject] public IJSRuntime JsRuntime { get; set; } = default!;
  [Inject] public NavigationManager NavManager { get; set; } = default!;

  protected List<BankAccountDto> Accounts { get; } = new();
  protected List<BankMovementDto> Movements { get; } = new();
  protected List<PendingBankTransactionDto> PendingTransactions { get; } = new();
  protected List<int> AvailableYears { get; } = new();

  protected bool IsInitializing { get; private set; } = true;
  protected bool IsLoadingAccounts { get; private set; }
  protected bool IsLoadingMovements { get; private set; }
  protected bool IsLoadingPendingTransactions { get; private set; }
  protected bool IsProcessingFile { get; private set; }
  protected bool IsAligningTransactions { get; private set; }

  protected string? ErrorMessage { get; private set; }
  protected int? SelectedAccountId { get; private set; }
  protected int? SelectedPendingTransactionId { get; private set; }
  protected long? SelectedMovimientoId { get; private set; }
  protected int SelectedMonth { get; set; } = DateTime.Today.Month;
  protected int SelectedYear { get; set; } = DateTime.Today.Year;
  protected decimal? InitialBalance { get; set; } = 0m;
  private bool _showOnlyUnlinkedMovements;
  private bool _showOnlyAccountingIssues;
  private bool _showOnlyBalanceDifferences;
  protected string? TextFilter { get; private set; }
  protected string MovementOrder { get; private set; } = MovementOrderBankNewest;
  protected ProcessBbvaResult? LastProcessResult { get; private set; }
  protected EditContext? AccountEditContext { get; private set; }
  protected BankAccountInputModel? AccountDraft { get; private set; }
  protected string AccountModalTitle { get; private set; } = string.Empty;
  protected bool IsAccountModalVisible { get; private set; }
  protected bool IsSavingAccount { get; private set; }
  protected bool IsDeletingAccount { get; private set; }
  protected bool ShowOnlyUnlinkedMovements
  {
    get => _showOnlyUnlinkedMovements;
    set
    {
      if (_showOnlyUnlinkedMovements == value)
      {
        return;
      }

      _showOnlyUnlinkedMovements = value;

      EnsureSelectedMovementIsVisible();

      _ = InvokeAsync(StateHasChanged);
    }
  }

  protected bool ShowOnlyAccountingIssues
  {
    get => _showOnlyAccountingIssues;
    set
    {
      if (_showOnlyAccountingIssues == value)
      {
        return;
      }

      _showOnlyAccountingIssues = value;
      EnsureSelectedMovementIsVisible();
      _ = InvokeAsync(StateHasChanged);
    }
  }

  protected bool ShowOnlyBalanceDifferences
  {
    get => _showOnlyBalanceDifferences;
    set
    {
      if (_showOnlyBalanceDifferences == value)
      {
        return;
      }

      _showOnlyBalanceDifferences = value;
      EnsureSelectedMovementIsVisible();
      _ = InvokeAsync(StateHasChanged);
    }
  }

  protected IReadOnlyList<KeyValuePair<int, string>> MonthOptions => MonthOptionsInternal;

  protected bool CanLink => SelectedMovimientoId.HasValue;

  protected bool CanUnlink => GetSelectedMovement() is { PolicyCount: > 0 };

  protected bool CanAlignTransactionsToBankOrder
    => SelectedAccountId.HasValue &&
       !IsAligningTransactions &&
       Movements.Any(m => m.PolicyCount > 0);

  protected bool HasPendingTransactions => PendingTransactions.Count > 0;

  protected bool RequiresBankImportWarningAcknowledgement
    => LastProcessResult is { CambiosSaldoHistorico: > 0 } && !_bankImportWarningAcknowledged;

  protected bool AutoPolizasDisabled
    => HasPendingTransactions || RequiresBankImportWarningAcknowledgement;

  protected string? AutoPolizasTooltip
    => HasPendingTransactions
      ? "Disponible cuando no haya transacciones pendientes por ligar."
      : RequiresBankImportWarningAcknowledgement
        ? "Revisa y confirma la advertencia de saldos históricos antes de crear pólizas."
        : null;

  protected string SelectedAccountLabel
    => Accounts.FirstOrDefault(a => a.CuentaBancoId == SelectedAccountId) is { } account
      ? $"{account.NombreBanco} · {account.NumeroCuenta}"
      : "Ninguna";

  protected IEnumerable<BankMovementDto> VisibleMovements
  {
    get
    {
      IEnumerable<BankMovementDto> movements = Movements;

      if (ShowOnlyUnlinkedMovements)
      {
        movements = movements.Where(m => m.PolicyCount <= 0);
      }

      if (ShowOnlyAccountingIssues)
      {
        movements = movements.Where(HasHardAccountingIssue);
      }

      if (ShowOnlyBalanceDifferences)
      {
        movements = movements.Where(HasMeaningfulBankAccountingDifference);
      }

      return ApplyMovementOrder(movements);
    }
  }

  protected string? SelectedAccountLedgerUrl
    => BuildSelectedAccountLedgerUrl();

  protected bool IsBankNewestOrderSelected
    => string.Equals(MovementOrder, MovementOrderBankNewest, StringComparison.Ordinal);

  protected bool IsAccountingOrderSelected
    => string.Equals(MovementOrder, MovementOrderAccounting, StringComparison.Ordinal);

  protected override void OnInitialized()
  {
    base.OnInitialized();
    RfcState.Changed += OnRfcStateChanged;
  }

  protected override async Task OnInitializedAsync()
  {
    await base.OnInitializedAsync();
    await InitializeAsync();
  }

  public void Dispose()
  {
    _movementsCts?.Cancel();
    _movementsCts?.Dispose();
    _pendingTransactionsCts?.Cancel();
    _pendingTransactionsCts?.Dispose();
    _textFilterDebounceCts?.Cancel();
    _textFilterDebounceCts?.Dispose();
    RfcState.Changed -= OnRfcStateChanged;
  }

  protected string FormatCurrency(decimal value)
    => value.ToString("C2", CurrencyCulture);

  protected string FormatCurrency(decimal? value)
    => value.HasValue ? FormatCurrency(value.Value) : "-";

  protected string FormatDateTime(DateTime? value)
    => value.HasValue ? value.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "-";

  protected string FormatPendingBankRegistro(PendingBankTransactionDto pending)
  {
    if (pending.BankRegistroLineCount <= 0)
    {
      return "-";
    }

    var hasDebe = pending.BankRegistroDebe != 0m;
    var hasHaber = pending.BankRegistroHaber != 0m;

    if (hasDebe && hasHaber)
    {
      return $"Debe {FormatCurrency(pending.BankRegistroDebe)} / Haber {FormatCurrency(pending.BankRegistroHaber)}";
    }

    if (hasDebe)
    {
      return $"Debe {FormatCurrency(pending.BankRegistroDebe)}";
    }

    if (hasHaber)
    {
      return $"Haber {FormatCurrency(pending.BankRegistroHaber)}";
    }

    return FormatCurrency(0m);
  }

  protected string FormatAccountingLevels(BankMovementDto movement)
    => string.IsNullOrWhiteSpace(movement.BankAccountNivel1)
      ? "-"
      : $"{movement.BankAccountNivel1}.{movement.BankAccountNivel2}.{movement.BankAccountNivel3}";

  protected string GetAuditBadgeClass(BankMovementDto movement)
    => string.Equals(movement.AuditSeverity, "Hard", StringComparison.OrdinalIgnoreCase)
      ? "text-bg-danger"
      : HasMeaningfulBankAccountingDifference(movement)
        ? "text-bg-warning"
        : "text-bg-success";

  protected string GetAuditLabel(BankMovementDto movement)
    => string.Equals(movement.AuditSeverity, "Hard", StringComparison.OrdinalIgnoreCase)
      ? "Problema contable"
      : HasMeaningfulBankAccountingDifference(movement)
        ? "Diferencia banco"
        : "OK";

  protected string GetDetailToggleIconClass(BankMovementDto movement)
    => IsPolicyExpanded(movement) ? "bi bi-chevron-up" : "bi bi-chevron-down";

  protected string GetMovementRowClass(BankMovementDto movement, bool isSelected)
  {
    if (isSelected)
    {
      return "table-primary";
    }

    if (HasHardAccountingIssue(movement))
    {
      return "movement-has-issues";
    }

    return HasMeaningfulBankAccountingDifference(movement) ? "movement-has-soft-issues" : string.Empty;
  }

  protected IReadOnlyList<string> GetIssueCodes(BankMovementDto movement)
    => string.IsNullOrWhiteSpace(movement.Issues) ||
       string.Equals(movement.Issues, "OK", StringComparison.OrdinalIgnoreCase)
      ? Array.Empty<string>()
      : movement.Issues
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(issue => !string.Equals(issue, "OK", StringComparison.OrdinalIgnoreCase))
        .ToArray();

  protected bool HasHardAccountingIssue(BankMovementDto movement)
    => GetIssueCodes(movement).Count > 0;

  protected bool HasMeaningfulBankAccountingDifference(BankMovementDto movement)
    => movement.BankAccountingVariance.HasValue &&
       Math.Abs(movement.BankAccountingVariance.Value) > BankAccountingDifferenceTolerance;

  protected bool IsPolicyExpanded(BankMovementDto movement)
    => movement.Policy is int policy && _expandedPolicyIds.Contains(policy);

  protected bool IsPolicyDetailLoading(BankMovementDto movement)
    => movement.Policy is int policy && _loadingPolicyDetailIds.Contains(policy);

  protected IReadOnlyList<TransaccionMovimientoDto> GetAccountingDetailRows(BankMovementDto movement)
    => movement.Policy is int policy && _accountingDetailsByPolicy.TryGetValue(policy, out var rows)
      ? rows
      : Array.Empty<TransaccionMovimientoDto>();

  protected bool IsPolicyReordering(BankMovementDto movement)
    => movement.Policy is int policy && _reorderingPolicyIds.Contains(policy);

  protected bool CanMoveAccountingEarlier(BankMovementDto movement)
    => CanMoveAccountingOrder(movement, -1);

  protected bool CanMoveAccountingLater(BankMovementDto movement)
    => CanMoveAccountingOrder(movement, 1);

  protected string? GetSelectedAccountDescription()
    => Accounts.FirstOrDefault(a => a.CuentaBancoId == SelectedAccountId)?.CuentaContableDescripcion;

  protected void SetBankNewestOrder()
    => SetMovementOrder(MovementOrderBankNewest);

  protected void SetAccountingOrder()
    => SetMovementOrder(MovementOrderAccounting);

  protected string GetMovementOrderButtonClass(string order)
    => string.Equals(MovementOrder, order, StringComparison.Ordinal)
      ? "btn btn-primary btn-sm"
      : "btn btn-outline-primary btn-sm";

  protected string GetBankNewestOrderButtonClass()
    => GetMovementOrderButtonClass(MovementOrderBankNewest);

  protected string GetAccountingOrderButtonClass()
    => GetMovementOrderButtonClass(MovementOrderAccounting);

  protected async Task ReloadMovementsAsync()
  {
    await LoadPendingTransactionsAsync();
    await LoadMovementsAsync();
  }

  protected async Task OnMonthChangedAsync()
  {
    ClearAccountingDetailState();
    await LoadPendingTransactionsAsync();
    await LoadMovementsAsync();
  }

  protected async Task OnYearChangedAsync()
  {
    ClearAccountingDetailState();
    await LoadPendingTransactionsAsync();
    await LoadMovementsAsync();
  }

  protected async Task OnAccountSelected(int accountId)
  {
    if (SelectedAccountId == accountId)
    {
      return;
    }

    SelectedAccountId = accountId;
    LastProcessResult = null;
    SelectedMovimientoId = null;
    ClearAccountingDetailState();
    await LoadPendingTransactionsAsync();
    await LoadMovementsAsync();
    await InvokeAsync(StateHasChanged);
  }

  protected void ShowCreateAccountModal()
  {
    if (string.IsNullOrWhiteSpace(_currentRfc))
    {
      UiMessages.ShowWarning("Selecciona un RFC antes de registrar cuentas bancarias.");
      return;
    }

    AccountDraft = BankAccountInputModel.CreateNew(_currentRfc);
    AccountModalTitle = "Agregar cuenta bancaria";
    AccountEditContext = new EditContext(AccountDraft);
    IsAccountModalVisible = true;
  }

  protected void ShowEditAccountModal(BankAccountDto account)
  {
    if (account is null)
    {
      return;
    }

    AccountDraft = BankAccountInputModel.FromAccount(account);
    AccountModalTitle = $"Editar cuenta bancaria – {account.NombreBanco}";
    AccountEditContext = new EditContext(AccountDraft);
    IsAccountModalVisible = true;
  }

  protected void CloseAccountModal()
  {
    IsAccountModalVisible = false;
    AccountEditContext = null;
    AccountDraft = null;
  }

  protected async Task HandleAccountValidSubmit()
  {
    if (AccountDraft is null)
    {
      return;
    }

    if (IsSavingAccount)
    {
      return;
    }

    var draft = AccountDraft;
    var request = draft.ToRequest();
    var isNewAccount = !draft.CuentaBancoId.HasValue;
    var wasSelected = draft.CuentaBancoId.HasValue && SelectedAccountId == draft.CuentaBancoId.Value;

    IsSavingAccount = true;

    try
    {
      BankAccountDto? savedAccount;

      if (draft.CuentaBancoId.HasValue)
      {
        savedAccount = await BancosService.UpdateAccountAsync(draft.CuentaBancoId.Value, request);

        if (savedAccount is null)
        {
          UiMessages.ShowError("La cuenta bancaria ya no existe.");
          return;
        }

        UiMessages.ShowSuccess("Cuenta bancaria actualizada correctamente.");
      }
      else
      {
        savedAccount = await BancosService.CreateAccountAsync(request);
        UiMessages.ShowSuccess("Cuenta bancaria registrada correctamente.");
      }

      var savedAccountId = savedAccount.CuentaBancoId;

      CloseAccountModal();

      await LoadAccountsInternalAsync();

      SelectedAccountId = savedAccountId;

      if (isNewAccount)
      {
        SelectedMovimientoId = null;
        LastProcessResult = null;
        await LoadMovementsAsync();
      }
      else if (wasSelected)
      {
        await InvokeAsync(StateHasChanged);
      }
    }
    catch (Exception)
    {
      UiMessages.ShowError("No se pudo guardar la cuenta bancaria.");
    }
    finally
    {
      IsSavingAccount = false;
      await InvokeAsync(StateHasChanged);
    }
  }

  protected async Task DeleteAccountAsync(BankAccountDto account)
  {
    if (account is null)
    {
      return;
    }

    if (IsDeletingAccount)
    {
      return;
    }

    if (string.IsNullOrWhiteSpace(_currentRfc))
    {
      UiMessages.ShowWarning("Selecciona un RFC válido antes de eliminar cuentas bancarias.");
      return;
    }

    var confirmationMessage = $"¿Deseas eliminar la cuenta {account.NombreBanco} · {account.NumeroCuenta}?";

    bool confirm;
    try
    {
      confirm = await JsRuntime.InvokeAsync<bool>("confirm", confirmationMessage);
    }
    catch
    {
      confirm = true;
    }

    if (!confirm)
    {
      return;
    }

    IsDeletingAccount = true;

    try
    {
      await BancosService.DeleteAccountAsync(account.CuentaBancoId, _currentRfc);

      if (SelectedAccountId == account.CuentaBancoId)
      {
        SelectedAccountId = null;
        SelectedMovimientoId = null;
        LastProcessResult = null;
        Movements.Clear();
      }

      await LoadAccountsInternalAsync();

      UiMessages.ShowSuccess("Cuenta bancaria eliminada correctamente.");
    }
    catch (Exception)
    {
      UiMessages.ShowError("No se pudo eliminar la cuenta bancaria.");
    }
    finally
    {
      IsDeletingAccount = false;
      await InvokeAsync(StateHasChanged);
    }
  }

  protected void OnPendingTransactionSelected(int transaccionId)
  {
    SelectedPendingTransactionId = SelectedPendingTransactionId == transaccionId
      ? null
      : transaccionId;

    _ = InvokeAsync(StateHasChanged);
  }

  protected void OnMovementSelected(long movimientoId)
  {
    SelectedMovimientoId = SelectedMovimientoId == movimientoId
      ? null
      : movimientoId;

    _ = InvokeAsync(StateHasChanged);
  }

  private BankMovementDto? GetSelectedMovement()
    => SelectedMovimientoId.HasValue
      ? Movements.FirstOrDefault(m => m.MovimientoId == SelectedMovimientoId.Value)
      : null;

  protected async Task ToggleAccountingDetailsAsync(BankMovementDto movement)
  {
    if (movement.Policy is not int policy || policy <= 0)
    {
      return;
    }

    if (_expandedPolicyIds.Contains(policy))
    {
      _expandedPolicyIds.Remove(policy);
      await InvokeAsync(StateHasChanged);
      return;
    }

    _expandedPolicyIds.Add(policy);

    if (_accountingDetailsByPolicy.ContainsKey(policy) || _loadingPolicyDetailIds.Contains(policy))
    {
      await InvokeAsync(StateHasChanged);
      return;
    }

    _loadingPolicyDetailIds.Add(policy);
    await InvokeAsync(StateHasChanged);

    try
    {
      var rows = await TransaccionService.GetMovimientosAsync(policy);
      _accountingDetailsByPolicy[policy] = rows;
    }
    catch (Exception)
    {
      UiMessages.ShowError("No se pudieron cargar los registros contables de la póliza.");
      _expandedPolicyIds.Remove(policy);
    }
    finally
    {
      _loadingPolicyDetailIds.Remove(policy);
      await InvokeAsync(StateHasChanged);
    }
  }

  protected async Task MoveAccountingEarlierAsync(BankMovementDto movement)
    => await MoveAccountingOrderAsync(movement, -1);

  protected async Task MoveAccountingLaterAsync(BankMovementDto movement)
    => await MoveAccountingOrderAsync(movement, 1);

  private async Task MoveAccountingOrderAsync(BankMovementDto movement, int direction)
  {
    if (movement.Policy is not int policy || policy <= 0)
    {
      return;
    }

    var target = GetAccountingNeighbor(movement, direction);
    if (target?.Policy is not int targetPolicy || targetPolicy <= 0)
    {
      return;
    }

    _reorderingPolicyIds.Add(policy);
    ErrorMessage = null;
    await InvokeAsync(StateHasChanged);

    try
    {
      await RegistrosService.ReorderTransaccionAsync(policy, targetPolicy);
      MovementOrder = MovementOrderAccounting;
      UiMessages.ShowSuccess("Orden contable actualizado.");
      await LoadMovementsAsync();
    }
    catch (Exception)
    {
      UiMessages.ShowError("No se pudo reordenar la póliza. Solo se permite entre pólizas del mismo día.");
    }
    finally
    {
      _reorderingPolicyIds.Remove(policy);
      await InvokeAsync(StateHasChanged);
    }
  }

  private bool CanMoveAccountingOrder(BankMovementDto movement, int direction)
  {
    if (movement.Policy is not int policy || policy <= 0 || IsPolicyReordering(movement))
    {
      return false;
    }

    return GetAccountingNeighbor(movement, direction) is not null;
  }

  private BankMovementDto? GetAccountingNeighbor(BankMovementDto movement, int direction)
  {
    if (movement.Policy is not int policy || !movement.PolicyDate.HasValue)
    {
      return null;
    }

    var ordered = GetDistinctPoliciesInAccountingOrder();
    var index = ordered.FindIndex(item => item.Policy == policy);
    var neighborIndex = index + direction;
    if (index < 0 || neighborIndex < 0 || neighborIndex >= ordered.Count)
    {
      return null;
    }

    var neighbor = ordered[neighborIndex];
    return neighbor.PolicyDate.HasValue && movement.PolicyDate.Value.Date == neighbor.PolicyDate.Value.Date ? neighbor : null;
  }

  private List<BankMovementDto> GetDistinctPoliciesInAccountingOrder()
  {
    var seen = new HashSet<int>();
    var ordered = Movements
      .Where(m => m.Policy is int policy && policy > 0 && m.PolicyDate.HasValue)
      .OrderBy(m => m.PolicyDate!.Value.Date)
      .ThenBy(m => m.OrdenBalance ?? long.MaxValue)
      .ThenBy(m => m.PolicyDate)
      .ThenBy(m => m.Policy)
      .ThenBy(m => m.MovimientoId)
      .Where(m => seen.Add(m.Policy!.Value))
      .ToList();

    return ordered;
  }

  private void EnsureSelectedMovementIsVisible()
  {
    if (!SelectedMovimientoId.HasValue)
    {
      return;
    }

    if (!VisibleMovements.Any(m => m.MovimientoId == SelectedMovimientoId.Value))
    {
      SelectedMovimientoId = null;
    }
  }

  private IEnumerable<BankMovementDto> ApplyMovementOrder(IEnumerable<BankMovementDto> movements)
  {
    if (IsAccountingOrderSelected)
    {
      return movements
        .OrderBy(m => m.PolicyDate?.Date ?? m.Dia.Date)
        .ThenBy(m => m.Policy.HasValue ? 0 : 1)
        .ThenBy(m => m.OrdenBalance ?? long.MaxValue)
        .ThenBy(m => m.PolicyDate ?? DateTime.MaxValue)
        .ThenBy(m => m.Policy ?? int.MaxValue)
        .ThenBy(m => m.SecuenciaClave)
        .ThenBy(m => m.MovimientoId)
        .ToList();
    }

    return movements
      .OrderByDescending(m => m.SecuenciaClave)
      .ThenByDescending(m => m.MovimientoId)
      .ToList();
  }

  private void SetMovementOrder(string order)
  {
    if (!string.Equals(order, MovementOrderBankNewest, StringComparison.Ordinal) &&
        !string.Equals(order, MovementOrderAccounting, StringComparison.Ordinal))
    {
      return;
    }

    if (string.Equals(MovementOrder, order, StringComparison.Ordinal))
    {
      return;
    }

    MovementOrder = order;
    EnsureSelectedMovementIsVisible();
  }

  private void ClearAccountingDetailState()
  {
    _accountingDetailsByPolicy.Clear();
    _expandedPolicyIds.Clear();
    _loadingPolicyDetailIds.Clear();
    _reorderingPolicyIds.Clear();
  }

  private void PruneAccountingDetailState()
  {
    var currentPolicies = Movements
      .Where(m => m.Policy is int policy && policy > 0)
      .Select(m => m.Policy!.Value)
      .ToHashSet();

    _expandedPolicyIds.RemoveWhere(policy => !currentPolicies.Contains(policy));
    _loadingPolicyDetailIds.RemoveWhere(policy => !currentPolicies.Contains(policy));
    _reorderingPolicyIds.RemoveWhere(policy => !currentPolicies.Contains(policy));

    foreach (var policy in _accountingDetailsByPolicy.Keys.Where(policy => !currentPolicies.Contains(policy)).ToList())
    {
      _accountingDetailsByPolicy.Remove(policy);
    }
  }

  private string? BuildSelectedAccountLedgerUrl()
  {
    var account = Accounts.FirstOrDefault(a => a.CuentaBancoId == SelectedAccountId);
    if (account is null ||
        string.IsNullOrWhiteSpace(_currentRfc) ||
        string.IsNullOrWhiteSpace(account.CuentaContableNivel1) ||
        string.IsNullOrWhiteSpace(account.CuentaContableNivel2))
    {
      return null;
    }

    var nivel3 = string.IsNullOrWhiteSpace(account.CuentaContableNivel3)
      ? "00"
      : account.CuentaContableNivel3.Trim();

    return string.Create(
      CultureInfo.InvariantCulture,
      $"/contabilidad/registros-contables?rfc={Uri.EscapeDataString(_currentRfc)}&anio={SelectedYear:0000}&mes={SelectedMonth:00}&nivel1={Uri.EscapeDataString(account.CuentaContableNivel1.Trim())}&nivel2={Uri.EscapeDataString(account.CuentaContableNivel2.Trim())}&nivel3={Uri.EscapeDataString(nivel3)}");
  }

  protected async Task OnLinkClicked()
  {
    if (!CanLink)
    {
      UiMessages.ShowWarning("Selecciona un movimiento bancario.");
      return;
    }

    var movement = Movements.FirstOrDefault(m => m.MovimientoId == SelectedMovimientoId);
    if (movement is null)
    {
      UiMessages.ShowWarning("Selecciona un movimiento válido.");
      return;
    }

    OpenMovementLinkingWorkspace(movement.MovimientoId, SelectedPendingTransactionId);
    await Task.CompletedTask;
  }

  protected async Task OnUnlinkClicked()
  {
    var movement = GetSelectedMovement();

    if (movement is null)
    {
      UiMessages.ShowWarning("Selecciona un movimiento válido.");
      return;
    }

    OpenMovementLinkingWorkspace(movement.MovimientoId);
    await Task.CompletedTask;
  }

  protected void OpenMovementLinkingWorkspace(long movimientoId, int? transaccionId = null)
  {
    var url = $"/contabilidad/bancos/movimientos/{movimientoId}/ligar";
    if (transaccionId.HasValue && transaccionId.Value > 0)
    {
      url += $"?transaccionId={transaccionId.Value}";
    }

    NavManager.NavigateTo(url);
  }

  protected async Task OnAutoPolizasClicked()
  {
    if (HasPendingTransactions)
    {
      UiMessages.ShowWarning("Disponible cuando no haya transacciones pendientes por ligar.");
      return;
    }

    if (RequiresBankImportWarningAcknowledgement)
    {
      UiMessages.ShowWarning("Revisa y confirma la advertencia de saldos históricos antes de crear pólizas automáticas.");
      return;
    }

    if (string.IsNullOrWhiteSpace(_currentRfc))
    {
      UiMessages.ShowWarning("Selecciona un RFC válido antes de continuar.");
      return;
    }

    bool confirm;
    try
    {
      confirm = await JsRuntime.InvokeAsync<bool>("confirm", "¿Estas seguro que quieres Crear Polizas para cada una de los Movimientos sin Poliza?");
    }
    catch
    {
      confirm = true;
    }

    if (!confirm)
    {
      return;
    }

    try
    {
      var created = await BancosService.CreateAutoPoliciesAsync(
          _currentRfc,
          SelectedYear,
          SelectedMonth,
          SelectedAccountId);

      if (created == 0)
      {
        UiMessages.ShowInfo("No se encontraron movimientos pendientes para crear pólizas.");
      }
      else
      {
        UiMessages.ShowSuccess($"Se crearon {created} póliza(s) automáticamente.");
      }
    }
    catch (Exception)
    {
      UiMessages.ShowError("Ocurrió un error al crear las pólizas automáticas.");
    }
    finally
    {
      await LoadPendingTransactionsAsync();
      await LoadMovementsAsync();
    }
  }

  protected async Task OnAlignTransactionsToBankOrderClicked()
  {
    if (!SelectedAccountId.HasValue)
    {
      UiMessages.ShowWarning("Selecciona una cuenta bancaria antes de alinear pólizas.");
      return;
    }

    if (string.IsNullOrWhiteSpace(_currentRfc))
    {
      UiMessages.ShowWarning("Selecciona un RFC válido antes de continuar.");
      return;
    }

    if (!Movements.Any(m => m.PolicyCount > 0))
    {
      UiMessages.ShowInfo("No hay pólizas ligadas para alinear en este periodo.");
      return;
    }

    bool confirm;
    try
    {
      confirm = await JsRuntime.InvokeAsync<bool>(
        "confirm",
        "Esto ajustará la fecha de las pólizas ligadas al día del movimiento bancario y su OrdenBalance al orden del banco para la cuenta y periodo seleccionados. ¿Deseas continuar?");
    }
    catch
    {
      confirm = true;
    }

    if (!confirm)
    {
      return;
    }

    IsAligningTransactions = true;
    await InvokeAsync(StateHasChanged);

    try
    {
      var aligned = await BancosService.AlignTransactionsToBankMovementsAsync(
        _currentRfc,
        SelectedYear,
        SelectedMonth,
        SelectedAccountId.Value);

      MovementOrder = MovementOrderAccounting;

      if (aligned == 0)
      {
        UiMessages.ShowInfo("Las pólizas ligadas ya estaban alineadas al banco.");
      }
      else
      {
        UiMessages.ShowSuccess($"Se alinearon {aligned} póliza(s) al orden bancario.");
      }

      await LoadPendingTransactionsAsync();
      await LoadMovementsAsync();
    }
    catch (Exception)
    {
      UiMessages.ShowError("No se pudieron alinear las pólizas al orden bancario.");
    }
    finally
    {
      IsAligningTransactions = false;
      await InvokeAsync(StateHasChanged);
    }
  }

  protected void OnTextFilterChanged(ChangeEventArgs args)
  {
    TextFilter = args.Value?.ToString();

    _textFilterDebounceCts?.Cancel();
    _textFilterDebounceCts?.Dispose();
    _textFilterDebounceCts = new CancellationTokenSource();

    var localCts = _textFilterDebounceCts;

    _ = Task.Run(async () =>
    {
      try
      {
        await Task.Delay(TimeSpan.FromMilliseconds(300), localCts.Token);
        if (!localCts.IsCancellationRequested)
        {
          await InvokeAsync(() => LoadMovementsAsync());
        }
      }
      catch (TaskCanceledException)
      {
        // Ignore
      }
    });
  }

  protected async Task OpenBankFilePickerAsync()
  {
    if (IsProcessingFile)
    {
      return;
    }

    if (!SelectedAccountId.HasValue)
    {
      UiMessages.ShowWarning("Selecciona primero una cuenta bancaria.");
      return;
    }

    try
    {
      var pickedFile = await JsRuntime.InvokeAsync<SelectedBankFile?>("pickBankFile");

      if (pickedFile is null)
      {
        return;
      }

      if (pickedFile.Size <= 0 || string.IsNullOrWhiteSpace(pickedFile.Content))
      {
        UiMessages.ShowError("El archivo está vacío o no se pudo leer.");
        return;
      }

      await ProcessSelectedBankFileAsync(pickedFile.Content);
    }
    catch (JSException ex)
    {
      var baseMessage = ex.GetBaseException().Message;
      UiMessages.ShowError(
        string.IsNullOrWhiteSpace(baseMessage)
          ? "No se pudo abrir el selector de archivos."
          : $"No se pudo abrir el selector de archivos: {baseMessage}");
    }
  }

  private async Task ProcessSelectedBankFileAsync(string content)
  {
    try
    {
      if (!SelectedAccountId.HasValue)
      {
        UiMessages.ShowWarning("Selecciona primero una cuenta bancaria.");
        return;
      }

      IsProcessingFile = true;
      await InvokeAsync(StateHasChanged);

      if (string.IsNullOrWhiteSpace(content))
      {
        UiMessages.ShowError("El archivo está vacío o no se pudo leer.");
        return;
      }

      var initialBalance = InitialBalance ?? 0m;
      var result = await BancosService.ProcessBbvaFileAsync(content, SelectedAccountId.Value, initialBalance);

      if (result is null)
      {
        UiMessages.ShowWarning("El procedimiento no devolvió información.");
        LastProcessResult = null;
      }
      else
      {
        LastProcessResult = result;
        _bankImportWarningAcknowledged = false;

        if (result.CambiosSaldoHistorico > 0)
        {
          UiMessages.ShowWarning(
            $"Se reconocieron {result.CambiosSaldoHistorico} movimiento(s) con cambios de saldo histórico. No se duplicaron ni se sobrescribieron sus saldos guardados.");
        }
        else
        {
          UiMessages.ShowSuccess("Archivo procesado correctamente.");
        }
      }

      await LoadMovementsAsync();
      await LoadPendingTransactionsAsync();
    }
    catch (OperationCanceledException)
    {
      // Ignore cancellation
    }
    catch (Exception ex)
    {
      var baseMessage = ex.GetBaseException().Message;
      UiMessages.ShowError(
        string.IsNullOrWhiteSpace(baseMessage)
          ? "Ocurrio un error al procesar el archivo."
          : $"No se pudo procesar el archivo: {baseMessage}");
    }
    finally
    {
      IsProcessingFile = false;
      await InvokeAsync(StateHasChanged);
    }
  }

  protected void AcknowledgeBankImportWarning()
  {
    if (LastProcessResult is not { CambiosSaldoHistorico: > 0 })
    {
      return;
    }

    _bankImportWarningAcknowledged = true;
    UiMessages.ShowInfo("Advertencia revisada. Ya puedes crear las pólizas automáticas cuando estés listo.");
  }

  private async Task InitializeAsync()
  {
    try
    {
      _currentRfc = RfcState.CurrentRfc;
      if (string.IsNullOrWhiteSpace(_currentRfc))
      {
        RfcState.ResetToDefault();
        _currentRfc = RfcState.CurrentRfc;
      }

      await LoadAccountsInternalAsync();
      await LoadYearsInternalAsync();
      await LoadPendingTransactionsAsync();
      await LoadMovementsAsync();
    }
    catch (Exception)
    {
      ErrorMessage = "No se pudo cargar la información inicial.";
      UiMessages.ShowError("No se pudo cargar la información inicial.");
    }
    finally
    {
      IsInitializing = false;
      await InvokeAsync(StateHasChanged);
    }
  }

  private void OnRfcStateChanged()
  {
    _ = InvokeAsync(HandleRfcChangedAsync);
  }

  private async Task HandleRfcChangedAsync()
  {
    var nextRfc = RfcState.CurrentRfc;

    if (string.Equals(_currentRfc, nextRfc, StringComparison.OrdinalIgnoreCase))
    {
      return;
    }

    _currentRfc = nextRfc;
    CloseAccountModal();
    SelectedAccountId = null;
    SelectedPendingTransactionId = null;
    SelectedMovimientoId = null;
    LastProcessResult = null;
    ErrorMessage = null;
    ClearAccountingDetailState();

    _movementsCts?.Cancel();
    _pendingTransactionsCts?.Cancel();
    _textFilterDebounceCts?.Cancel();

    if (string.IsNullOrWhiteSpace(_currentRfc))
    {
      Accounts.Clear();
      Movements.Clear();
      PendingTransactions.Clear();
      AvailableYears.Clear();
      await InvokeAsync(StateHasChanged);
      return;
    }

    await LoadAccountsInternalAsync();
    await LoadYearsInternalAsync();
    await LoadPendingTransactionsAsync();
    await LoadMovementsAsync();
  }

  private async Task LoadAccountsInternalAsync()
  {
    IsLoadingAccounts = true;
    await InvokeAsync(StateHasChanged);

    try
    {
      Accounts.Clear();
      var accounts = await BancosService.GetAccountsAsync(_currentRfc ?? string.Empty);
      Accounts.AddRange(accounts);
    }
    catch (Exception)
    {
      UiMessages.ShowError("No se pudieron cargar las cuentas bancarias.");
      ErrorMessage ??= "No se pudieron cargar las cuentas bancarias.";
    }
    finally
    {
      IsLoadingAccounts = false;
      await InvokeAsync(StateHasChanged);
    }
  }

  private async Task LoadYearsInternalAsync()
  {
    AvailableYears.Clear();

    if (string.IsNullOrWhiteSpace(_currentRfc))
    {
      return;
    }

    try
    {
      var years = await BancosService.GetAvailableYearsAsync(_currentRfc);
      if (years.Count > 0)
      {
        AvailableYears.AddRange(years);
      }
    }
    catch (Exception)
    {
      UiMessages.ShowWarning("No se pudieron obtener los años disponibles.");
    }

    if (AvailableYears.Count == 0)
    {
      AvailableYears.Add(DateTime.Today.Year);
    }

    if (!AvailableYears.Contains(SelectedYear))
    {
      SelectedYear = AvailableYears[0];
    }
  }

  private async Task LoadMovementsAsync()
  {
    var previousCts = _movementsCts;
    previousCts?.Cancel();
    previousCts?.Dispose();

    if (string.IsNullOrWhiteSpace(_currentRfc) || !SelectedAccountId.HasValue)
    {
      _movementsCts = null;
      Movements.Clear();
      SelectedMovimientoId = null;
      IsLoadingMovements = false;
      await InvokeAsync(StateHasChanged);
      return;
    }

    _movementsCts = new CancellationTokenSource();
    var localCts = _movementsCts;

    IsLoadingMovements = true;
    await InvokeAsync(StateHasChanged);

    try
    {
      Movements.Clear();
      var rows = await BancosService.GetMovementsAsync(
          _currentRfc,
          SelectedAccountId,
          SelectedYear,
          SelectedMonth,
          TextFilter,
          localCts.Token);

      Movements.AddRange(rows);
      PruneAccountingDetailState();
      if (!Movements.Any(m => m.MovimientoId == SelectedMovimientoId))
      {
        SelectedMovimientoId = null;
      }
    }
    catch (OperationCanceledException)
    {
      // Ignore cancellation
    }
    catch (Exception)
    {
      UiMessages.ShowError("No se pudieron cargar los movimientos.");
    }
    finally
    {
      if (ReferenceEquals(_movementsCts, localCts))
      {
        IsLoadingMovements = false;
        localCts.Dispose();
        _movementsCts = null;
        await InvokeAsync(StateHasChanged);
      }
    }
  }

  private async Task LoadPendingTransactionsAsync()
  {
    if (string.IsNullOrWhiteSpace(_currentRfc))
    {
      PendingTransactions.Clear();
      SelectedPendingTransactionId = null;
      return;
    }

    var previousCts = _pendingTransactionsCts;
    previousCts?.Cancel();
    previousCts?.Dispose();

    _pendingTransactionsCts = new CancellationTokenSource();
    var localCts = _pendingTransactionsCts;

    IsLoadingPendingTransactions = true;
    await InvokeAsync(StateHasChanged);

    try
    {
      PendingTransactions.Clear();
      var rows = await BancosService.GetPendingTransactionsAsync(
          _currentRfc,
          SelectedAccountId,
          SelectedYear,
          SelectedMonth,
          localCts.Token);

      PendingTransactions.AddRange(rows);

      if (!PendingTransactions.Any(t => t.TransaccionId == SelectedPendingTransactionId))
      {
        SelectedPendingTransactionId = null;
      }
    }
    catch (OperationCanceledException)
    {
      // Ignore cancellation
    }
    catch (Exception)
    {
      UiMessages.ShowError("No se pudieron cargar las transacciones pendientes.");
    }
    finally
    {
      if (ReferenceEquals(_pendingTransactionsCts, localCts))
      {
        IsLoadingPendingTransactions = false;
        localCts.Dispose();
        _pendingTransactionsCts = null;
        await InvokeAsync(StateHasChanged);
      }
    }
  }

  protected sealed class BankAccountInputModel
  {
    public int? CuentaBancoId { get; init; }

    public DateTime? FechaAlta { get; init; }

    [Required]
    [StringLength(100)]
    public string NombreBanco { get; set; } = string.Empty;

    [Required]
    [StringLength(50)]
    public string NumeroCuenta { get; set; } = string.Empty;

    [StringLength(100)]
    public string? TipoCuenta { get; set; }

    [StringLength(200)]
    public string? NombreTitular { get; set; }

    [StringLength(50)]
    public string? ClabeCuenta { get; set; }

    [Required]
    [StringLength(50)]
    public string Rfc { get; set; } = string.Empty;

    public bool Activo { get; set; } = true;

    public int? CuentaContableId { get; set; }

    public int? CuentaContableEgreso { get; set; }

    public int? CuentaContableIngreso { get; set; }

    public static BankAccountInputModel CreateNew(string rfc)
      => new()
      {
        Rfc = rfc,
        Activo = true,
      };

    public static BankAccountInputModel FromAccount(BankAccountDto account)
      => new()
      {
        CuentaBancoId = account.CuentaBancoId,
        FechaAlta = account.FechaAlta,
        NombreBanco = account.NombreBanco,
        NumeroCuenta = account.NumeroCuenta,
        TipoCuenta = string.IsNullOrWhiteSpace(account.TipoCuenta) ? null : account.TipoCuenta,
        NombreTitular = string.IsNullOrWhiteSpace(account.NombreTitular) ? null : account.NombreTitular,
        ClabeCuenta = string.IsNullOrWhiteSpace(account.ClabeCuenta) ? null : account.ClabeCuenta,
        Rfc = account.Rfc,
        Activo = account.Activo,
        CuentaContableId = NormalizeCuenta(account.CuentaContableId),
        CuentaContableEgreso = NormalizeCuenta(account.CuentaContableEgreso),
        CuentaContableIngreso = NormalizeCuenta(account.CuentaContableIngreso),
      };

    public BankAccountRequest ToRequest()
      => new()
      {
        NombreBanco = NombreBanco.Trim(),
        NumeroCuenta = NumeroCuenta.Trim(),
        TipoCuenta = NormalizeString(TipoCuenta),
        NombreTitular = NormalizeString(NombreTitular),
        ClabeCuenta = NormalizeString(ClabeCuenta),
        Rfc = Rfc.Trim(),
        Activo = Activo,
        CuentaContableId = NormalizeCuenta(CuentaContableId),
        CuentaContableEgreso = NormalizeCuenta(CuentaContableEgreso),
        CuentaContableIngreso = NormalizeCuenta(CuentaContableIngreso),
      };

    private static string? NormalizeString(string? value)
    {
      if (string.IsNullOrWhiteSpace(value))
      {
        return null;
      }

      return value.Trim();
    }

    private static int? NormalizeCuenta(int? value)
      => value.HasValue && value.Value > 0 ? value : null;
  }

  protected sealed class SelectedBankFile
  {
    public string Name { get; init; } = string.Empty;

    public long Size { get; init; }

    public string Content { get; init; } = string.Empty;
  }
}
