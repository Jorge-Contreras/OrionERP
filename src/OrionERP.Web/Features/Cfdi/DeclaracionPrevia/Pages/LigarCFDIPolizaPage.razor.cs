using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OrionERP.Application.Features.Cfdi.DeclaracionPrevia;
using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.Web.Services;
using OrionERP.Web.State;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages;

[Authorize(Roles = "Administrador,SatOperator")]
public partial class LigarCFDIPolizaPage : ComponentBase, IDisposable
{
  [Parameter]
  public int Comprobante_Id { get; set; }

  [Inject]
  public IDeclaracionPreviaService DeclaracionPreviaService { get; set; } = default!;

  [Inject]
  public ITransaccionService TransaccionService { get; set; } = default!;

  [Inject]
  public IUserRfcState RfcState { get; set; } = default!;

  [Inject]
  public IUiMessageService UiMessages { get; set; } = default!;

  [Inject]
  public IJSRuntime Js { get; set; } = default!;

  [Inject]
  public NavigationManager Nav { get; set; } = default!;

  protected ComprobanteDetalleDto? Comprobante { get; set; }
  protected bool IsLoading { get; set; }
  protected bool IsPolizasLoading { get; set; }
  protected bool IsTransaccionesLoading { get; set; }
  protected bool IsLinking { get; set; }
  protected bool IsCreatingPoliza { get; set; }
  protected List<TransaccionListItemDto> Polizas { get; } = new();
  protected List<TransaccionListItemDto> Transacciones { get; set; } = new();
  protected TransaccionFilter Filter { get; set; } = new();
  protected TransaccionListItemDto? SelectedTransaccion { get; set; }
  protected string? InlineError { get; set; }
  private bool _disposed;

  protected bool CanLigar => Comprobante is not null && SelectedTransaccion is not null;
  protected bool CanCreatePoliza => Comprobante is not null;

  protected override async Task OnInitializedAsync()
  {
    RfcState.Changed += OnRfcChanged;
    ResetFilters();
    await LoadDataAsync();
  }

  private async void OnRfcChanged()
  {
    if (_disposed)
    {
      return;
    }

    await InvokeAsync(async () =>
    {
      Filter.Rfc = RfcState.CurrentRfc;
      await LoadTransaccionesAsync();
    });
  }

  private async Task LoadDataAsync()
  {
    IsLoading = true;
    InlineError = null;
    Comprobante = null;
    try
    {
      Comprobante = await DeclaracionPreviaService.GetComprobanteDetalleAsync(Comprobante_Id);
      if (Comprobante is not null)
      {
        Filter.Monto = Comprobante.Total;
        await LoadPolizasAsync();
        await LoadTransaccionesAsync();
      }
    }
    catch (Exception ex)
    {
      InlineError = ex.Message;
    }
    finally
    {
      IsLoading = false;
    }
  }

  private async Task LoadPolizasAsync()
  {
    if (Comprobante is null)
    {
      return;
    }

    IsPolizasLoading = true;
    Polizas.Clear();

    try
    {
      if (!string.IsNullOrWhiteSpace(Comprobante.FOLIO_FISCAL))
      {
        var polizasByUuid = await TransaccionService.GetTransaccionesByUuidAsync(Comprobante.FOLIO_FISCAL);
        Polizas.AddRange(polizasByUuid);
      }

      if (Polizas.Count == 0)
      {
        var polizasById = await TransaccionService.GetTransaccionesByComprobanteIdAsync(Comprobante.Comprobante_Id);
        Polizas.AddRange(polizasById);
      }
    }
    catch (Exception ex)
    {
      InlineError = $"No se pudieron cargar las pólizas ligadas. {ex.Message}";
      UiMessages.ShowError(InlineError);
    }
    finally
    {
      IsPolizasLoading = false;
    }
  }

  private async Task LoadTransaccionesAsync()
  {
    IsTransaccionesLoading = true;
    InlineError = null;
    SelectedTransaccion = null;

    try
    {
      Filter.Rfc = RfcState.CurrentRfc;
      var result = await TransaccionService.GetTransaccionesListAsync(Filter);
      Transacciones = result.ToList();
    }
    catch (Exception ex)
    {
      InlineError = $"No se pudieron cargar las transacciones. {ex.Message}";
      UiMessages.ShowError(InlineError);
    }
    finally
    {
      IsTransaccionesLoading = false;
    }
  }

  protected async Task BuscarAsync()
  {
    await LoadTransaccionesAsync();
  }

  protected async Task LimpiarFiltrosAsync()
  {
    ResetFilters();
    await LoadTransaccionesAsync();
  }

  protected async Task Ordenar(string columnName)
  {
    if (Filter.SortBy == columnName)
    {
      Filter.SortAsc = !Filter.SortAsc;
    }
    else
    {
      Filter.SortBy = columnName;
      Filter.SortAsc = true;
    }

    await LoadTransaccionesAsync();
  }

  protected string GetRowClass(TransaccionListItemDto item)
    => SelectedTransaccion?.Id == item.Id ? "table-active" : string.Empty;

  protected void SeleccionarTransaccion(TransaccionListItemDto item)
  {
    if (IsTransaccionesLoading)
    {
      return;
    }

    SelectedTransaccion = item;
    InlineError = null;
  }

  protected async Task LigarAsync()
  {
    if (!CanLigar || SelectedTransaccion is null || Comprobante is null)
    {
      return;
    }

    InlineError = null;
    var confirm = await Js.InvokeAsync<bool>("confirm", "¿Deseas ligar la transacción seleccionada a este CFDI?");
    if (!confirm)
    {
      return;
    }

    IsLinking = true;
    try
    {
      var result = await TransaccionService.LinkCfdiReplacingPlaceholderAndRelinkAttachmentAsync(
        SelectedTransaccion.Id,
        Comprobante.Comprobante_Id,
        SelectedTransaccion.Monto);

      if (!result.Success)
      {
        InlineError = result.Message;
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      ResetFilters();
      SelectedTransaccion = null;
      await LoadPolizasAsync();
      await LoadTransaccionesAsync();
    }
    catch (Exception ex)
    {
      InlineError = $"No se pudo ligar la transacción: {ex.Message}";
      UiMessages.ShowError(InlineError);
    }
    finally
    {
      IsLinking = false;
    }
  }

  protected async Task CrearPolizaConComprobanteAsync()
  {
    if (!CanCreatePoliza || Comprobante is null)
    {
      return;
    }

    InlineError = null;
    IsCreatingPoliza = true;

    try
    {
      await DeclaracionPreviaService.GenerarPolizaDesdeComprobanteAsync(Comprobante.Comprobante_Id, RfcState.CurrentRfc ?? string.Empty);
      await OpenLinkedTransactionAsync();
      await LoadPolizasAsync();
    }
    catch (Exception ex)
    {
      InlineError = $"Error al crear la póliza: {ex.Message}";
      UiMessages.ShowError(InlineError);
    }
    finally
    {
      IsCreatingPoliza = false;
    }
  }

  private async Task OpenLinkedTransactionAsync()
  {
    var transId = await DeclaracionPreviaService.GetLinkedTransactionIdAsync(Comprobante_Id);

    if (!transId.HasValue)
    {
      UiMessages.ShowWarning("No se encontró una Transacción vinculada a este CFDI.");
      return;
    }

    var url = $"/Contabilidad/transacciones/{transId.Value}";

    try
    {
      await Js.InvokeVoidAsync("open", url, "_blank", "noopener,noreferrer");
    }
    catch
    {
      Nav.NavigateTo(url);
    }
  }

  private void ResetFilters()
  {
    Filter = new TransaccionFilter
    {
      Rfc = RfcState.CurrentRfc,
      Year = DateTime.Now.Year,
      Month = DateTime.Now.Month,
      Monto = Comprobante?.Total
    };
  }

  public void Dispose()
  {
    if (_disposed)
    {
      return;
    }

    RfcState.Changed -= OnRfcChanged;
    _disposed = true;
  }
}
