using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using OrionERP.Application.Features.Contabilidad.Bancos;

namespace OrionERP.Web.Features.Contabilidad.Bancos;

public partial class TransaccionBancoLinkingPage : ComponentBase
{
  private static readonly CultureInfo CurrencyCulture = new("es-MX");

  [Parameter]
  public int TransaccionId { get; set; }

  [Inject]
  public IBancosService BancosService { get; set; } = default!;

  [Inject]
  public NavigationManager NavManager { get; set; } = default!;

  protected BankTransactionMovementWorkspaceDto Workspace { get; private set; } = new();
  protected BankTransactionMovementSummaryDto? Summary => Workspace.Summary;
  protected FilterModel Filters { get; } = new();
  protected bool IsLoading { get; private set; }
  protected bool IsSearching { get; private set; }
  protected string? ErrorMessage { get; private set; }

  protected override async Task OnParametersSetAsync()
  {
    await LoadAsync();
  }

  protected Task HandleSearchSubmitAsync(EditContext _)
    => SearchAsync();

  protected async Task SearchAsync()
  {
    IsSearching = true;

    try
    {
      await LoadWorkspaceAsync();
    }
    finally
    {
      IsSearching = false;
    }
  }

  protected async Task ClearSearchAsync()
  {
    Filters.Search = null;
    Filters.IncludeFullyLinkedMovements = false;
    await SearchAsync();
  }

  protected void GoBack()
  {
    NavManager.NavigateTo($"/contabilidad/transacciones/{TransaccionId}");
  }

  protected void OpenMovementWorkspace(long movimientoId)
  {
    NavManager.NavigateTo($"/contabilidad/bancos/movimientos/{movimientoId}/ligar?transaccionId={TransaccionId}");
  }

  protected string FormatCurrency(decimal value)
    => value.ToString("C2", CurrencyCulture);

  protected string FormatMovementAmount(decimal cargo, decimal abono)
    => cargo > 0m ? FormatCurrency(cargo) : FormatCurrency(abono);

  protected static string FormatStatus(string? status)
    => status switch
    {
      "FUERTE" => "Fuerte",
      "POSIBLE" => "Posible",
      "OK" => "Ligado",
      "REVISAR" => "Revisar",
      _ => string.IsNullOrWhiteSpace(status) ? "Sin dato" : status
    };

  protected static string GetCandidateBadgeClass(BankTransactionMovementCandidateDto candidate)
    => candidate.MatchStatus switch
    {
      "OK" => "text-bg-success",
      "FUERTE" => "text-bg-success",
      "POSIBLE" => "text-bg-primary",
      "REVISAR" => "text-bg-warning",
      _ => "text-bg-secondary"
    };

  private async Task LoadAsync()
  {
    IsLoading = true;
    ErrorMessage = null;

    try
    {
      await LoadWorkspaceAsync();
    }
    catch (Exception ex)
    {
      ErrorMessage = $"No se pudo cargar el ligado bancario de la póliza: {ex.Message}";
    }
    finally
    {
      IsLoading = false;
    }
  }

  private async Task LoadWorkspaceAsync()
  {
    Workspace = await BancosService.GetTransactionMovementLinkingWorkspaceAsync(
      TransaccionId,
      Filters.Search,
      Filters.IncludeFullyLinkedMovements);
  }

  protected sealed class FilterModel
  {
    public string? Search { get; set; }
    public bool IncludeFullyLinkedMovements { get; set; }
  }
}
