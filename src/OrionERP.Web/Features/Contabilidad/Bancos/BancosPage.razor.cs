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

  private CancellationTokenSource? _movementsCts;
  private CancellationTokenSource? _pendingTransactionsCts;
  private CancellationTokenSource? _textFilterDebounceCts;

  private string? _currentRfc;

  [Inject] public IBancosService BancosService { get; set; } = default!;
  [Inject] public IUserRfcState RfcState { get; set; } = default!;
  [Inject] public IUiMessageService UiMessages { get; set; } = default!;
  [Inject] public IJSRuntime JsRuntime { get; set; } = default!;

  protected List<BankAccountDto> Accounts { get; } = new();
  protected List<BankMovementDto> Movements { get; } = new();
  protected List<PendingBankTransactionDto> PendingTransactions { get; } = new();
  protected List<int> AvailableYears { get; } = new();

  protected bool IsInitializing { get; private set; } = true;
  protected bool IsLoadingAccounts { get; private set; }
  protected bool IsLoadingMovements { get; private set; }
  protected bool IsLoadingPendingTransactions { get; private set; }
  protected bool IsProcessingFile { get; private set; }

  protected string? ErrorMessage { get; private set; }
  protected int? SelectedAccountId { get; private set; }
  protected int? SelectedPendingTransactionId { get; private set; }
  protected long? SelectedMovimientoId { get; private set; }
  protected int SelectedMonth { get; set; } = DateTime.Today.Month;
  protected int SelectedYear { get; set; } = DateTime.Today.Year;
  protected decimal? InitialBalance { get; set; } = 0m;
  private bool _showOnlyUnlinkedMovements;
  protected string? TextFilter { get; private set; }
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

      if (_showOnlyUnlinkedMovements && GetSelectedMovement() is { Policy: not null and > 0 })
      {
        SelectedMovimientoId = null;
      }

      _ = InvokeAsync(StateHasChanged);
    }
  }

  protected IReadOnlyList<KeyValuePair<int, string>> MonthOptions => MonthOptionsInternal;

  protected bool CanLink => SelectedPendingTransactionId.HasValue && SelectedMovimientoId.HasValue;

  protected bool CanUnlink => GetSelectedMovement() is { Policy: int policyValue } && policyValue > 0;

  protected bool HasPendingTransactions => PendingTransactions.Count > 0;

  protected string? AutoPolizasTooltip
    => HasPendingTransactions ? "Disponible cuando no haya transacciones pendientes por ligar." : null;

  protected string SelectedAccountLabel
    => Accounts.FirstOrDefault(a => a.CuentaBancoId == SelectedAccountId) is { } account
      ? $"{account.NombreBanco} · {account.NumeroCuenta}"
      : "Ninguna";

  protected IEnumerable<BankMovementDto> VisibleMovements
    => ShowOnlyUnlinkedMovements
      ? Movements.Where(m => m.Policy is null or <= 0)
      : Movements;

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

  protected async Task ReloadMovementsAsync()
  {
    await LoadPendingTransactionsAsync();
    await LoadMovementsAsync();
  }

  protected async Task OnMonthChangedAsync()
  {
    await LoadPendingTransactionsAsync();
    await LoadMovementsAsync();
  }

  protected async Task OnYearChangedAsync()
  {
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

  protected async Task OnLinkClicked()
  {
    if (!CanLink)
    {
      UiMessages.ShowWarning("Selecciona una transacción y un movimiento.");
      return;
    }

    var movement = Movements.FirstOrDefault(m => m.MovimientoId == SelectedMovimientoId);
    var transaction = PendingTransactions.FirstOrDefault(t => t.TransaccionId == SelectedPendingTransactionId);

    if (movement is null || transaction is null)
    {
      UiMessages.ShowWarning("Selecciona una transacción y un movimiento válidos.");
      return;
    }

    var hasExistingPolicy = movement.Policy.HasValue && movement.Policy.Value > 0;
    var confirmationMessage = hasExistingPolicy
      ? "El movimiento ya tiene una póliza ligada. ¿Deseas reemplazarla con la transacción seleccionada?"
      : "¿Deseas ligar la transacción seleccionada con el movimiento?";

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

    try
    {
      await BancosService.LinkMovementToTransactionAsync(movement.MovimientoId, transaction.TransaccionId);
      UiMessages.ShowSuccess("Movimiento ligado correctamente.");
      SelectedMovimientoId = null;
      SelectedPendingTransactionId = null;
      await LoadPendingTransactionsAsync();
      await LoadMovementsAsync();
    }
    catch (Exception)
    {
      UiMessages.ShowError("No se pudo ligar el movimiento seleccionado.");
    }
  }

  protected async Task OnUnlinkClicked()
  {
    var movement = GetSelectedMovement();

    if (movement is null)
    {
      UiMessages.ShowWarning("Selecciona un movimiento válido.");
      return;
    }

    if (movement.Policy is null or <= 0)
    {
      UiMessages.ShowWarning("El movimiento seleccionado no tiene una póliza ligada.");
      return;
    }

    bool confirm;
    try
    {
      confirm = await JsRuntime.InvokeAsync<bool>("confirm", "¿Estas Seguro que deseas desligar este Movimiento?");
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
      await BancosService.UnlinkMovementAsync(movement.MovimientoId);
      UiMessages.ShowSuccess("Movimiento desligado correctamente.");
      SelectedMovimientoId = null;
      await LoadPendingTransactionsAsync();
      await LoadMovementsAsync();
    }
    catch (Exception)
    {
      UiMessages.ShowError("No se pudo desligar el movimiento seleccionado.");
    }
  }

  protected async Task OnAutoPolizasClicked()
  {
    if (HasPendingTransactions)
    {
      UiMessages.ShowWarning("Disponible cuando no haya transacciones pendientes por ligar.");
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
        UiMessages.ShowSuccess("Archivo procesado correctamente.");
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
