using System.Globalization;
using Microsoft.AspNetCore.Components;
using System.Linq;
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
  protected bool IsPolizasCollapsed { get; set; } = true;
  protected bool IsPolizasLoading { get; set; }
  protected int ComprobanteId { get; set; }
  protected List<TransaccionListItemDto> Polizas { get; } = new();
  protected decimal TotalMontoAsignado => Polizas.Sum(p => p.MontoAsignado);
  private readonly Dictionary<int, LinkedMontoEditor> _linkedMontoEditors = new();
  private int? _savingLinkedMontoTransaccionId;

  protected override async Task OnParametersSetAsync()
  {
    IsLoading = true;
    ErrorMessage = null;
    Document = null;
    ComprobanteId = 0;
    Polizas.Clear();

    try
    {
      Document = await HtmlCfdiService.GetHtmlCfdiAsync(Id);
      ComprobanteId = await TransaccionService.GetComprobanteIdByXmlAttachmentAsync(Id);
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
    if (ComprobanteId <= 0 && string.IsNullOrWhiteSpace(Document?.Timbre?.Uuid))
      return;

    IsPolizasLoading = true;

    try
    {
      IReadOnlyList<TransaccionListItemDto> polizas;
      if (ComprobanteId > 0)
      {
        polizas = await TransaccionService.GetTransaccionesByComprobanteIdAsync(ComprobanteId);
      }
      else
      {
        polizas = await TransaccionService.GetTransaccionesByUuidAsync(Document!.Timbre!.Uuid!);
      }

      Polizas.Clear();
      Polizas.AddRange(polizas);
      SyncLinkedMontoInputs();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudieron cargar las pólizas relacionadas. {ex.Message}");
    }
    finally
    {
      IsPolizasLoading = false;
    }
  }

  protected decimal GetMaxAllowedMonto(TransaccionListItemDto? transaccion)
  {
    if (transaccion is null)
    {
      return 0m;
    }

    var totalCfdi = GetDocumentTotal();
    if (totalCfdi <= 0m)
    {
      return decimal.Abs(transaccion.Monto);
    }

    return decimal.Min(totalCfdi, decimal.Abs(transaccion.Monto));
  }

  protected LinkedMontoEditor GetLinkedMontoEditor(TransaccionListItemDto poliza)
  {
    if (_linkedMontoEditors.TryGetValue(poliza.Id, out var editor))
    {
      return editor;
    }

    editor = new LinkedMontoEditor { Monto = poliza.MontoAsignado };
    _linkedMontoEditors[poliza.Id] = editor;
    return editor;
  }

  protected bool IsSavingLinkedMonto(TransaccionListItemDto poliza)
    => _savingLinkedMontoTransaccionId == poliza.Id;

  protected async Task GuardarMontoLigadoAsync(TransaccionListItemDto poliza)
  {
    if (ComprobanteId <= 0)
    {
      UiMessages.ShowError("No se identificó el comprobante para actualizar el monto asignado.");
      return;
    }

    var monto = GetLinkedMontoEditor(poliza).Monto;
    if (monto <= 0m)
    {
      UiMessages.ShowError("El monto asignado debe ser mayor que cero.");
      return;
    }

    var maxMonto = GetMaxAllowedMonto(poliza);
    if (maxMonto > 0m && monto > maxMonto)
    {
      UiMessages.ShowError($"El monto asignado para la póliza {poliza.Id} no puede exceder {maxMonto:C}.");
      return;
    }

    _savingLinkedMontoTransaccionId = poliza.Id;

    try
    {
      var result = await TransaccionService.UpdateComprobanteMontoAsync(poliza.Id, ComprobanteId, monto);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      await LoadPolizasAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo actualizar el monto asignado. {ex.Message}");
    }
    finally
    {
      _savingLinkedMontoTransaccionId = null;
    }
  }

  private decimal GetDocumentTotal()
  {
    var total = Document?.Total;
    if (string.IsNullOrWhiteSpace(total))
    {
      return 0m;
    }

    if (decimal.TryParse(total, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
    {
      return parsed;
    }

    if (decimal.TryParse(total, NumberStyles.Number, new CultureInfo("es-MX"), out parsed))
    {
      return parsed;
    }

    return decimal.TryParse(total, NumberStyles.Number, CultureInfo.CurrentCulture, out parsed)
      ? parsed
      : 0m;
  }

  private void SyncLinkedMontoInputs()
  {
    _linkedMontoEditors.Clear();

    foreach (var poliza in Polizas)
    {
      _linkedMontoEditors[poliza.Id] = new LinkedMontoEditor
      {
        Monto = poliza.MontoAsignado
      };
    }
  }

  protected sealed class LinkedMontoEditor
  {
    public decimal Monto { get; set; }
  }
}
