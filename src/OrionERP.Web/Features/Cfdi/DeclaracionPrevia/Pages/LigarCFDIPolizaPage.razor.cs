using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
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

  protected CfdiPolizaLinkingWorkspaceDto Workspace { get; private set; } = new();
  protected CfdiPolizaLinkingSummaryDto? Summary => Workspace.Summary;
  protected List<CfdiPolizaLinkedPolizaDto> Polizas => Workspace.Polizas;
  protected List<CfdiPolizaCandidateDto> Candidates => Workspace.Candidates;
  protected bool IsLoading { get; set; }
  protected bool IsCandidatesLoading { get; set; }
  protected bool IsLinking { get; set; }
  protected bool IsCreatingPoliza { get; set; }
  protected decimal LinkMonto { get; set; }
  protected TransaccionFilter Filter { get; set; } = new();
  protected CfdiPolizaCandidateDto? SelectedCandidate { get; set; }
  protected string? InlineError { get; set; }
  protected int? HighlightedTransaccionId { get; set; }
  private readonly Dictionary<int, LinkedMontoEditor> _linkedMontoEditors = new();
  private int? _savingLinkedMontoTransaccionId;
  private bool _disposed;

  protected bool CanLigar => Summary is not null && SelectedCandidate is not null && LinkMonto > 0m;
  protected bool CanCreatePoliza => Summary is not null;

  protected override async Task OnInitializedAsync()
  {
    RfcState.Changed += OnRfcChanged;
    ResetFilters();
    await LoadWorkspaceAsync();
  }

  private async void OnRfcChanged()
  {
    if (_disposed)
    {
      return;
    }

    await InvokeAsync(async () =>
    {
      ResetFilters();
      await LoadWorkspaceAsync();
    });
  }

  private async Task LoadWorkspaceAsync(bool candidatesOnly = false)
  {
    if (candidatesOnly)
    {
      IsCandidatesLoading = true;
    }
    else
    {
      IsLoading = true;
    }

    InlineError = null;
    SelectedCandidate = null;
    LinkMonto = 0m;

    try
    {
      Filter.Rfc = RfcState.CurrentRfc;
      Workspace = await TransaccionService.GetCfdiPolizaLinkingWorkspaceAsync(
        Comprobante_Id,
        RfcState.CurrentRfc,
        Filter);

      if (Workspace.Summary is not null && Filter.Monto is null)
      {
        Filter.Monto = GetObjectiveMonto();
      }

      SyncLinkedMontoInputs();
    }
    catch (Exception ex)
    {
      InlineError = $"No se pudo cargar el espacio de ligado CFDI. {ex.Message}";
      UiMessages.ShowError(InlineError);
    }
    finally
    {
      IsLoading = false;
      IsCandidatesLoading = false;
    }
  }

  protected Task BuscarAsync()
    => LoadWorkspaceAsync(candidatesOnly: true);

  protected Task HandleFilterSubmitAsync(EditContext _)
    => BuscarAsync();

  protected async Task LimpiarFiltrosAsync()
  {
    ResetFilters();
    await LoadWorkspaceAsync(candidatesOnly: true);
  }

  protected void SeleccionarCandidate(CfdiPolizaCandidateDto item)
  {
    if (IsCandidatesLoading)
    {
      return;
    }

    SelectedCandidate = item;
    LinkMonto = GetSuggestedMonto(item);
    InlineError = null;
  }

  protected string GetCandidateRowClass(CfdiPolizaCandidateDto item)
  {
    var classes = new List<string>();

    if (SelectedCandidate?.Id == item.Id)
    {
      classes.Add("table-active");
    }

    if (HighlightedTransaccionId == item.Id)
    {
      classes.Add("linking-row-highlight");
    }

    return string.Join(" ", classes);
  }

  protected async Task LigarAsync()
  {
    if (!CanLigar || SelectedCandidate is null || Summary is null)
    {
      return;
    }

    InlineError = null;
    var confirm = await Js.InvokeAsync<bool>("confirm", "¿Deseas ligar la póliza seleccionada a este CFDI?");
    if (!confirm)
    {
      return;
    }

    var maxMonto = GetMaxAllowedMonto(SelectedCandidate);
    if (LinkMonto <= 0m)
    {
      InlineError = "El monto a ligar debe ser mayor que cero.";
      UiMessages.ShowError(InlineError);
      return;
    }

    if (maxMonto > 0m && LinkMonto > maxMonto)
    {
      InlineError = $"El monto a ligar no puede exceder {FormatCurrency(maxMonto)}.";
      UiMessages.ShowError(InlineError);
      return;
    }

    IsLinking = true;
    try
    {
      var result = await TransaccionService.LinkCfdiAndRelinkAttachmentAsync(
        SelectedCandidate.Id,
        Summary.ComprobanteId,
        LinkMonto);

      if (!result.Success)
      {
        InlineError = result.Message;
        UiMessages.ShowError(result.Message);
        return;
      }

      HighlightedTransaccionId = SelectedCandidate.Id;
      UiMessages.ShowSuccess(result.Message);
      ResetFilters();
      await LoadWorkspaceAsync();
    }
    catch (Exception ex)
    {
      InlineError = $"No se pudo ligar la póliza: {ex.Message}";
      UiMessages.ShowError(InlineError);
    }
    finally
    {
      IsLinking = false;
    }
  }

  protected async Task CrearPolizaConComprobanteAsync()
  {
    if (!CanCreatePoliza || Summary is null)
    {
      return;
    }

    InlineError = null;
    IsCreatingPoliza = true;

    try
    {
      var transaccionId = await DeclaracionPreviaService.GenerarPolizaDesdeComprobanteAsync(
        Summary.ComprobanteId,
        RfcState.CurrentRfc ?? string.Empty);

      HighlightedTransaccionId = transaccionId;
      UiMessages.ShowSuccess($"Póliza {transaccionId} creada y ligada al CFDI.");
      ResetFilters();
      await LoadWorkspaceAsync();
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

  private void ResetFilters()
  {
    var now = DateTime.Now;
    Filter = new TransaccionFilter
    {
      Rfc = RfcState.CurrentRfc,
      Year = Summary?.Fecha.Year ?? now.Year,
      Month = Summary?.Fecha.Month ?? now.Month,
      Monto = null
    };
  }

  protected decimal GetObjectiveMonto()
  {
    if (Summary is null)
    {
      return 0m;
    }

    return Summary.Pendiente > 0m ? Summary.Pendiente : Summary.Total;
  }

  protected decimal GetSuggestedMonto(CfdiPolizaCandidateDto? candidate)
  {
    if (candidate is null)
    {
      return 0m;
    }

    return candidate.MontoSugerido > 0m ? candidate.MontoSugerido : GetMaxAllowedMonto(candidate);
  }

  protected decimal GetMaxAllowedMonto(CfdiPolizaCandidateDto? candidate)
  {
    if (Summary is null || candidate is null)
    {
      return 0m;
    }

    var cfdiRemaining = Summary.Pendiente > 0m ? Summary.Pendiente : Summary.Total;
    var candidateAvailable = candidate.Disponible > 0m ? candidate.Disponible : decimal.Abs(candidate.Monto);

    return decimal.Min(cfdiRemaining, candidateAvailable);
  }

  protected LinkedMontoEditor GetLinkedMontoEditor(CfdiPolizaLinkedPolizaDto poliza)
  {
    if (_linkedMontoEditors.TryGetValue(poliza.TransaccionId, out var editor))
    {
      return editor;
    }

    editor = new LinkedMontoEditor { Monto = poliza.MontoAsignado };
    _linkedMontoEditors[poliza.TransaccionId] = editor;
    return editor;
  }

  protected bool IsSavingLinkedMonto(CfdiPolizaLinkedPolizaDto poliza)
    => _savingLinkedMontoTransaccionId == poliza.TransaccionId;

  protected async Task GuardarMontoLigadoAsync(CfdiPolizaLinkedPolizaDto poliza)
  {
    if (Summary is null)
    {
      return;
    }

    var monto = GetLinkedMontoEditor(poliza).Monto;
    if (monto <= 0m)
    {
      InlineError = "El monto asignado debe ser mayor que cero.";
      UiMessages.ShowError(InlineError);
      return;
    }

    var maxMonto = decimal.Max(Summary.Total, decimal.Abs(poliza.TransaccionMonto));
    if (monto > maxMonto)
    {
      InlineError = $"El monto asignado para la póliza {poliza.TransaccionId} no puede exceder {FormatCurrency(maxMonto)}.";
      UiMessages.ShowError(InlineError);
      return;
    }

    InlineError = null;
    _savingLinkedMontoTransaccionId = poliza.TransaccionId;

    try
    {
      var result = await TransaccionService.UpdateComprobanteMontoAsync(
        poliza.TransaccionId,
        Summary.ComprobanteId,
        monto);

      if (!result.Success)
      {
        InlineError = result.Message;
        UiMessages.ShowError(result.Message);
        return;
      }

      HighlightedTransaccionId = poliza.TransaccionId;
      UiMessages.ShowSuccess(result.Message);
      await LoadWorkspaceAsync();
    }
    catch (Exception ex)
    {
      InlineError = $"No se pudo actualizar el monto ligado: {ex.Message}";
      UiMessages.ShowError(InlineError);
    }
    finally
    {
      _savingLinkedMontoTransaccionId = null;
    }
  }

  private void SyncLinkedMontoInputs()
  {
    _linkedMontoEditors.Clear();

    foreach (var poliza in Polizas)
    {
      _linkedMontoEditors[poliza.TransaccionId] = new LinkedMontoEditor
      {
        Monto = poliza.MontoAsignado
      };
    }
  }

  protected static string FormatCurrency(decimal value)
    => value.ToString("C", CultureInfo.CurrentCulture);

  protected static string FormatStatus(string? status)
    => status switch
    {
      "OK" => "Correcto",
      "DIFERENCIA" => "Diferencia",
      "NA" => "N/A",
      "FUERTE" => "Fuerte",
      "POSIBLE" => "Posible",
      "AMPLIA" => "Amplia",
      "SIN_DISPONIBLE" => "Sin disponible",
      _ => string.IsNullOrWhiteSpace(status) ? "Sin dato" : status
    };

  protected static string GetStatusBadgeClass(string? status)
    => status switch
    {
      "OK" or "FUERTE" => "text-bg-success",
      "POSIBLE" => "text-bg-primary",
      "AMPLIA" or "NA" => "text-bg-secondary",
      "SIN_DISPONIBLE" => "text-bg-dark",
      "DIFERENCIA" => "text-bg-danger",
      _ => "text-bg-secondary"
    };

  protected sealed class LinkedMontoEditor
  {
    public decimal Monto { get; set; }
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
