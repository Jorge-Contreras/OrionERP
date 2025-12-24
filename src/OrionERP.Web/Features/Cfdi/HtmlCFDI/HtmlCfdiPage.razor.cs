using Microsoft.AspNetCore.Components;
using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.Application.Features.Cfdi.HtmlCFDI;
using OrionERP.Web.Services;

namespace OrionERP.Web.Features.Cfdi.HtmlCFDI;

public partial class HtmlCfdiPage : ComponentBase
{
  [Parameter]
  public int Id { get; set; }

  [Inject]
  public IHtmlCfdiService HtmlCfdiService { get; set; } = default!;

  [Inject]
  public IUiMessageService UiMessages { get; set; } = default!;

  [Inject]
  public ITransaccionService TransaccionService { get; set; } = default!;

  protected CfdiReadableDocument? Document { get; set; }
  protected string? ErrorMessage { get; set; }
  protected bool IsLoading { get; set; }
  protected bool IsPolizasCollapsed { get; set; } = false;
  protected bool IsPolizasLoading { get; set; }
  protected List<TransaccionListItemDto> Polizas { get; } = new();

  protected override async Task OnParametersSetAsync()
  {
    IsLoading = true;
    ErrorMessage = null;
    Document = null;
    Polizas.Clear();

    try
    {
      Document = await HtmlCfdiService.GetHtmlCfdiAsync(Id);
      await LoadPolizasAsync();
    }
    catch (Exception ex)
    {
      ErrorMessage = ex.Message;
      UiMessages.ShowError(ErrorMessage);
    }
    finally
    {
      IsLoading = false;
    }
  }

  protected void TogglePolizas()
  {
    IsPolizasCollapsed = !IsPolizasCollapsed;
  }

  private async Task LoadPolizasAsync()
  {
    var uuid = Document?.Timbre?.Uuid;
    if (string.IsNullOrWhiteSpace(uuid))
      return;

    IsPolizasLoading = true;

    try
    {
      var polizas = await TransaccionService.GetTransaccionesByUuidAsync(uuid);
      Polizas.Clear();
      Polizas.AddRange(polizas);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError("No se pudieron cargar las pólizas relacionadas.");
    }
    finally
    {
      IsPolizasLoading = false;
    }
  }
}
