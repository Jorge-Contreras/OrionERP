using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using OrionERP.Application.Features.Cfdi.ContabilidadRegistros;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OrionERP.Web.Components.CuentasContables;

public partial class CuentasContablesPicker : ComponentBase
{
  private readonly List<CuentasContablesDto> nivel1Results = new();
  private readonly List<CuentasContablesDto> nivel2Results = new();
  private readonly List<CuentasContablesDto> nivel3Results = new();

  private string nivel1Term = string.Empty;
  private string nivel2Term = string.Empty;
  private string nivel3Term = string.Empty;
  private string? currentRfc;
  private CuentasContablesSelection? currentSelection;
  private bool _hasLoadedDefaults;

  [Inject] public ICuentasContablesRepository Repository { get; set; } = default!;

  [Parameter] public string? Rfc { get; set; }
  [Parameter] public EventCallback<string?> RfcChanged { get; set; }
  [Parameter] public CuentasContablesSelection? Selection { get; set; }
  [Parameter] public EventCallback<CuentasContablesSelection?> SelectionChanged { get; set; }
  [Parameter] public EventCallback<string> OnError { get; set; }
  [Parameter] public bool Disabled { get; set; }
  [Parameter] public int DefaultTake { get; set; } = 25;
  [Parameter] public int SearchTake { get; set; } = 200;

  public bool HasSelection => currentSelection is not null;
  public bool HasCompleteSelection => currentSelection is not null
                                      && !string.IsNullOrWhiteSpace(currentSelection.Nivel1)
                                      && !string.IsNullOrWhiteSpace(currentSelection.Nivel2)
                                      && !string.IsNullOrWhiteSpace(currentSelection.Nivel3);
  public CuentasContablesSelection? CurrentSelection => currentSelection;

  protected override async Task OnParametersSetAsync()
  {
    if (!string.Equals(currentRfc, Rfc, System.StringComparison.OrdinalIgnoreCase))
    {
      currentRfc = Rfc;
      _hasLoadedDefaults = false;
      ClearSearchResults();
      if (Selection is null)
      {
        ApplySelectionInternal(null);
      }
    }

    if (!SelectionsEqual(Selection, currentSelection))
    {
      ApplySelectionInternal(Selection);
    }

    if (!_hasLoadedDefaults && !string.IsNullOrWhiteSpace(currentRfc) && !Disabled)
    {
      _hasLoadedDefaults = true;
      await SearchNivel1Async(loadDefaults: true);
    }
  }

  public async Task<bool> ResolveAccountByIdAsync(int accountId)
  {
    if (accountId <= 0)
    {
      return false;
    }

    try
    {
      var dto = await Repository.GetByIdAsync(accountId);
      if (dto is null)
      {
        await ReportErrorAsync("No se encontró la cuenta contable.");
        return false;
      }

      await ApplyAccountAsync(dto);
      return true;
    }
    catch (System.Exception ex)
    {
      await ReportErrorAsync("No se pudo resolver la cuenta contable.", ex);
      return false;
    }
  }

  public Task ClearAsync()
  {
    ApplySelectionInternal(null);
    ClearSearchResults();
    return NotifySelectionChangedAsync();
  }

  private bool CanSearchNivel2 => !string.IsNullOrWhiteSpace(currentRfc)
                                  && !string.IsNullOrWhiteSpace(currentSelection?.Nivel1);

  private bool CanSearchNivel3 => CanSearchNivel2
                                  && !string.IsNullOrWhiteSpace(currentSelection?.Nivel2);

  private async Task OnNivel1KeyDown(KeyboardEventArgs args)
  {
    if (Disabled)
      return;

    if (args.Key is not ("Enter" or "NumpadEnter"))
    {
      return;
    }

    if (HasSelection && Nivel1MatchesTerm())
    {
      return;
    }

    await SearchNivel1Async(loadDefaults: string.IsNullOrWhiteSpace(nivel1Term));
  }

  private async Task OnNivel2KeyDown(KeyboardEventArgs args)
  {
    if (Disabled)
      return;

    if (args.Key is not ("Enter" or "NumpadEnter"))
    {
      return;
    }

    if (!CanSearchNivel2)
    {
      await SearchNivel1Async(loadDefaults: string.IsNullOrWhiteSpace(nivel1Term));
      return;
    }

    if (HasSelection && Nivel2MatchesTerm())
    {
      return;
    }

    await SearchNivel2Async(loadDefaults: string.IsNullOrWhiteSpace(nivel2Term));
  }

  private async Task OnNivel3KeyDown(KeyboardEventArgs args)
  {
    if (Disabled)
      return;

    if (args.Key is not ("Enter" or "NumpadEnter"))
    {
      return;
    }

    if (!CanSearchNivel3)
    {
      if (!CanSearchNivel2)
      {
        await SearchNivel1Async(loadDefaults: string.IsNullOrWhiteSpace(nivel1Term));
      }
      else
      {
        await SearchNivel2Async(loadDefaults: string.IsNullOrWhiteSpace(nivel2Term));
      }
      return;
    }

    if (HasSelection && Nivel3MatchesTerm())
    {
      return;
    }

    await SearchNivel3Async(loadDefaults: string.IsNullOrWhiteSpace(nivel3Term));
  }

  private async Task SearchNivel1Async(bool loadDefaults)
  {
    nivel1Results.Clear();
    nivel2Results.Clear();
    nivel3Results.Clear();

    if (string.IsNullOrWhiteSpace(currentRfc))
    {
      return;
    }

    var term = loadDefaults ? string.Empty : (nivel1Term?.Trim() ?? string.Empty);
    if (!loadDefaults && string.IsNullOrWhiteSpace(term))
    {
      return;
    }

    try
    {
      var take = loadDefaults ? DefaultTake : SearchTake;
      var results = await Repository.SearchNivel1Async(currentRfc!, term, take);
      nivel1Results.AddRange(results);
    }
    catch (System.Exception ex)
    {
      await ReportErrorAsync("No se pudo buscar cuentas de nivel 1.", ex);
    }
  }

  private async Task SearchNivel2Async(bool loadDefaults)
  {
    nivel2Results.Clear();
    nivel3Results.Clear();

    if (!CanSearchNivel2 || currentSelection?.Nivel1 is null || string.IsNullOrWhiteSpace(currentRfc))
    {
      return;
    }

    var term = loadDefaults ? string.Empty : (nivel2Term?.Trim() ?? string.Empty);
    if (!loadDefaults && string.IsNullOrWhiteSpace(term))
    {
      return;
    }

    try
    {
      var results = await Repository.SearchNivel2Async(currentRfc!, currentSelection.Nivel1!, term);
      nivel2Results.AddRange(results);
    }
    catch (System.Exception ex)
    {
      await ReportErrorAsync("No se pudo buscar cuentas de nivel 2.", ex);
    }
  }

  private async Task SearchNivel3Async(bool loadDefaults)
  {
    nivel3Results.Clear();

    if (!CanSearchNivel3 || currentSelection?.Nivel1 is null || currentSelection.Nivel2 is null || string.IsNullOrWhiteSpace(currentRfc))
    {
      return;
    }

    var term = loadDefaults ? string.Empty : (nivel3Term?.Trim() ?? string.Empty);
    if (!loadDefaults && string.IsNullOrWhiteSpace(term))
    {
      return;
    }

    try
    {
      var results = await Repository.SearchNivel3Async(currentRfc!, currentSelection.Nivel1!, currentSelection.Nivel2!, term);
      nivel3Results.AddRange(results);
    }
    catch (System.Exception ex)
    {
      await ReportErrorAsync("No se pudo buscar cuentas de nivel 3.", ex);
    }
  }

  private async Task SelectNivel1Async(CuentasContablesDto dto)
  {
    if (Disabled)
      return;

    await SetRfcAsync(dto.RazonSocial);

    var selection = new CuentasContablesSelection
    {
      Id = dto.Id,
      Rfc = dto.RazonSocial,
      Nivel1 = dto.Nivel1,
      Nivel2 = null,
      Nivel3 = null,
      Descripcion = dto.Descripcion
    };

    ApplySelectionInternal(selection);
    nivel1Results.Clear();
    await NotifySelectionChangedAsync();

    if (CanSearchNivel2)
    {
      await SearchNivel2Async(loadDefaults: true);
    }
  }

  private async Task SelectNivel2Async(CuentasContablesDto dto)
  {
    if (Disabled)
      return;

    var selection = new CuentasContablesSelection
    {
      Id = dto.Id,
      Rfc = currentSelection?.Rfc ?? Rfc,
      Nivel1 = dto.Nivel1,
      Nivel2 = NormalizeTwoDigits(dto.Nivel2),
      Nivel3 = null,
      Descripcion = dto.Descripcion
    };

    ApplySelectionInternal(selection);
    nivel2Results.Clear();
    nivel3Results.Clear();
    await NotifySelectionChangedAsync();

    if (CanSearchNivel3)
    {
      await SearchNivel3Async(loadDefaults: true);
    }
  }

  private async Task SelectNivel3(CuentasContablesDto dto)
  {
    if (Disabled)
      return;

    var selection = new CuentasContablesSelection
    {
      Id = dto.Id,
      Rfc = currentSelection?.Rfc ?? Rfc,
      Nivel1 = dto.Nivel1,
      Nivel2 = NormalizeTwoDigits(dto.Nivel2),
      Nivel3 = NormalizeTwoDigits(dto.Nivel3),
      Descripcion = dto.Descripcion
    };

    ApplySelectionInternal(selection);
    nivel3Results.Clear();
    await NotifySelectionChangedAsync();
  }

  private async Task ApplyAccountAsync(CuentasContablesDto dto)
  {
    await SetRfcAsync(dto.RazonSocial);

    var selection = new CuentasContablesSelection
    {
      Id = dto.Id,
      Rfc = dto.RazonSocial,
      Nivel1 = dto.Nivel1,
      Nivel2 = NormalizeTwoDigits(dto.Nivel2),
      Nivel3 = NormalizeTwoDigits(dto.Nivel3),
      Descripcion = dto.Descripcion
    };

    ApplySelectionInternal(selection);
    nivel1Results.Clear();
    nivel2Results.Clear();
    nivel3Results.Clear();
    await NotifySelectionChangedAsync();
  }

  private void ApplySelectionInternal(CuentasContablesSelection? selection)
  {
    currentSelection = selection is null ? null : selection with { };

    nivel1Term = selection?.Nivel1 ?? string.Empty;
    nivel2Term = selection?.Nivel2 ?? string.Empty;
    nivel3Term = selection?.Nivel3 ?? string.Empty;
  }

  private async Task SetRfcAsync(string razonSocial)
  {
    if (string.IsNullOrWhiteSpace(razonSocial))
    {
      return;
    }

    if (!string.Equals(currentRfc, razonSocial, System.StringComparison.OrdinalIgnoreCase))
    {
      currentRfc = razonSocial;
      _hasLoadedDefaults = false;
      if (RfcChanged.HasDelegate)
      {
        await RfcChanged.InvokeAsync(razonSocial);
      }
    }
  }

  private Task NotifySelectionChangedAsync()
  {
    if (SelectionChanged.HasDelegate)
    {
      return InvokeAsync(() => SelectionChanged.InvokeAsync(currentSelection is null ? null : currentSelection with { }));
    }

    return Task.CompletedTask;
  }

  private static string NormalizeTwoDigits(string value)
  {
    var trimmed = value?.Trim() ?? string.Empty;
    if (trimmed.Length == 1 && char.IsDigit(trimmed[0]))
    {
      return trimmed.PadLeft(2, '0');
    }

    return trimmed;
  }

  private bool Nivel1MatchesTerm()
    => currentSelection is not null
       && !string.IsNullOrWhiteSpace(currentSelection.Nivel1)
       && string.Equals((nivel1Term ?? string.Empty).Trim(), currentSelection.Nivel1.Trim(), System.StringComparison.OrdinalIgnoreCase);

  private bool Nivel2MatchesTerm()
    => currentSelection is not null
       && !string.IsNullOrWhiteSpace(currentSelection.Nivel2)
       && string.Equals(NormalizeTwoDigits(nivel2Term), currentSelection.Nivel2, System.StringComparison.OrdinalIgnoreCase);

  private bool Nivel3MatchesTerm()
    => currentSelection is not null
       && !string.IsNullOrWhiteSpace(currentSelection.Nivel3)
       && string.Equals(NormalizeTwoDigits(nivel3Term), currentSelection.Nivel3, System.StringComparison.OrdinalIgnoreCase);

  private static bool SelectionsEqual(CuentasContablesSelection? left, CuentasContablesSelection? right)
  {
    if (left is null && right is null)
      return true;
    if (left is null || right is null)
      return false;

    return left.Id == right.Id
           && string.Equals(left.Rfc, right.Rfc, System.StringComparison.OrdinalIgnoreCase)
           && string.Equals(left.Nivel1, right.Nivel1, System.StringComparison.OrdinalIgnoreCase)
           && string.Equals(left.Nivel2, right.Nivel2, System.StringComparison.OrdinalIgnoreCase)
           && string.Equals(left.Nivel3, right.Nivel3, System.StringComparison.OrdinalIgnoreCase)
           && string.Equals(left.Descripcion, right.Descripcion, System.StringComparison.Ordinal);
  }

  private void ClearSearchResults()
  {
    nivel1Results.Clear();
    nivel2Results.Clear();
    nivel3Results.Clear();
  }

  private async Task ReportErrorAsync(string message, System.Exception? ex = null)
  {
    if (ex is not null)
    {
      System.Console.Error.WriteLine($"[CuentasContablesPicker] {message}: {ex}");
    }
    else
    {
      System.Console.Error.WriteLine($"[CuentasContablesPicker] {message}");
    }

    if (OnError.HasDelegate)
    {
      await OnError.InvokeAsync(message);
    }
  }
}
