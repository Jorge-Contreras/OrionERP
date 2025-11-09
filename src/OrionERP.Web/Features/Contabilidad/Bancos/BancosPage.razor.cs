using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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
  protected string? TextFilter { get; private set; }
  protected ProcessBbvaResult? LastProcessResult { get; private set; }
  protected int FileInputKey { get; private set; }

  protected IReadOnlyList<KeyValuePair<int, string>> MonthOptions => MonthOptionsInternal;

  protected bool CanLink => SelectedPendingTransactionId.HasValue && SelectedMovimientoId.HasValue;

  protected bool HasPendingTransactions => PendingTransactions.Count > 0;

  protected string? AutoPolizasTooltip
    => HasPendingTransactions ? "Disponible cuando no haya transacciones pendientes por ligar." : null;

  protected string SelectedAccountLabel
    => Accounts.FirstOrDefault(a => a.CuentaBancoId == SelectedAccountId) is { } account
      ? $"{account.NombreBanco} · {account.NumeroCuenta}"
      : "Ninguna";

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
    await LoadMovementsAsync();
    await InvokeAsync(StateHasChanged);
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
      await LoadPendingTransactionsAsync();
      await LoadMovementsAsync();
    }
    catch (Exception)
    {
      UiMessages.ShowError("No se pudo ligar el movimiento seleccionado.");
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

  protected async Task OnFileSelectedAsync(InputFileChangeEventArgs args)
  {
    try
    {
      if (!SelectedAccountId.HasValue)
      {
        UiMessages.ShowWarning("Selecciona primero una cuenta bancaria.");
        return;
      }

      if (args.File is null)
      {
        return;
      }

      if (args.File.Size == 0)
      {
        UiMessages.ShowError("El archivo está vacío o no se pudo leer.");
        return;
      }

      IsProcessingFile = true;
      await InvokeAsync(StateHasChanged);

      var content = await ReadFileAsTextAsync(args.File);

      if (string.IsNullOrWhiteSpace(content))
      {
        UiMessages.ShowError("El archivo está vacío o no se pudo leer.");
        return;
      }

      var result = await BancosService.ProcessBbvaFileAsync(content, SelectedAccountId.Value);

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
    catch (Exception)
    {
      UiMessages.ShowError("Ocurrió un error al procesar el archivo.");
    }
    finally
    {
      IsProcessingFile = false;
      FileInputKey++;
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
    if (string.IsNullOrWhiteSpace(_currentRfc))
    {
      Movements.Clear();
      SelectedMovimientoId = null;
      return;
    }

    var previousCts = _movementsCts;
    previousCts?.Cancel();
    previousCts?.Dispose();

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

  private static async Task<string> ReadFileAsTextAsync(IBrowserFile file)
  {
    await using var stream = file.OpenReadStream(maxAllowedSize: 10 * 1024 * 1024);
    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
    return await reader.ReadToEndAsync();
  }
}
