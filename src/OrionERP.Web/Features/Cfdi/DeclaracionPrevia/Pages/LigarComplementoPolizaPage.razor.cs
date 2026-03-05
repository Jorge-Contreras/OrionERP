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
public partial class LigarComplementoPolizaPage : ComponentBase, IDisposable
{
  [Parameter]
  public int DoctoRelacionado_Id { get; set; }

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

  protected Pago20ResumenDetalleDto? DoctoRelacionado { get; set; }
  protected bool IsLoading { get; set; }
  protected bool IsPolizasLoading { get; set; }
  protected bool IsTransaccionesLoading { get; set; }
  protected bool IsLinking { get; set; }
  protected List<TransaccionListItemDto> Polizas { get; } = new();
  protected List<TransaccionListItemDto> Transacciones { get; set; } = new();
  protected TransaccionFilter Filter { get; set; } = new();
  protected TransaccionListItemDto? SelectedTransaccion { get; set; }
  protected string? InlineError { get; set; }
  private bool _disposed;

  protected bool CanLigar => DoctoRelacionado is not null && SelectedTransaccion is not null;

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
    DoctoRelacionado = null;

    try
    {
      DoctoRelacionado = await DeclaracionPreviaService.GetPago20ResumenByDoctoRelacionadoIdAsync(DoctoRelacionado_Id);
      if (DoctoRelacionado is not null)
      {
        Filter.Monto = DoctoRelacionado.ImpPagado != 0 ? DoctoRelacionado.ImpPagado : DoctoRelacionado.MontoPago;
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
    if (DoctoRelacionado is null)
    {
      return;
    }

    IsPolizasLoading = true;
    Polizas.Clear();

    try
    {
      var linkedPolizas = await TransaccionService.GetTransaccionesByDoctoRelacionadoIdAsync(DoctoRelacionado.DoctoRelacionado_Id);
      Polizas.AddRange(linkedPolizas);
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

  protected async Task BuscarAsync() => await LoadTransaccionesAsync();

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
    if (!CanLigar || SelectedTransaccion is null || DoctoRelacionado is null)
    {
      return;
    }

    InlineError = null;
    var confirm = await Js.InvokeAsync<bool>("confirm", "¿Deseas ligar la transacción seleccionada a este complemento de pago?");
    if (!confirm)
    {
      return;
    }

    IsLinking = true;
    try
    {
      var monto = DoctoRelacionado.ImpPagado != 0 ? DoctoRelacionado.ImpPagado : SelectedTransaccion.Monto;
      var result = await TransaccionService.InsertTransaccionDoctoRelacionadoAsync(
        SelectedTransaccion.Id,
        DoctoRelacionado.DoctoRelacionado_Id,
        monto);

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

  private void ResetFilters()
  {
    var monto = DoctoRelacionado?.ImpPagado != 0 ? DoctoRelacionado?.ImpPagado : DoctoRelacionado?.MontoPago;

    Filter = new TransaccionFilter
    {
      Rfc = RfcState.CurrentRfc,
      Year = DateTime.Now.Year,
      Month = DateTime.Now.Month,
      Monto = monto
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
