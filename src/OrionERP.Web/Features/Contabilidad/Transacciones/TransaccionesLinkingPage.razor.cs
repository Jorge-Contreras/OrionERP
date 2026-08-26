using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.Web.Services;

namespace OrionERP.Web.Features.Contabilidad.Transacciones;

public partial class TransaccionesLinkingPage : ComponentBase
{
  private static readonly CultureInfo CurrencyCulture = new("es-MX");

  [Parameter] public int Id { get; set; }

  [Inject] public ITransaccionService TransaccionService { get; set; } = default!;
  [Inject] public IUiMessageService UiMessages { get; set; } = default!;
  [Inject] public IJSRuntime JsRuntime { get; set; } = default!;
  [Inject] public NavigationManager Nav { get; set; } = default!;

  protected TransaccionHeaderDto? Header { get; private set; }
  protected TransaccionCfdiLinkingWorkspaceDto Workspace { get; private set; } = new();
  protected CandidateFilterState Filters { get; } = new();
  protected CandidateSelectionKind SelectedKind { get; private set; } = CandidateSelectionKind.None;
  protected TransaccionRegularCfdiLinkCandidateDto? SelectedRegularCandidate { get; private set; }
  protected TransaccionPago20LinkCandidateDto? SelectedPago20Candidate { get; private set; }
  protected decimal LinkMonto { get; set; }

  protected bool IsLoading { get; private set; } = true;
  protected bool IsRefreshingWorkspace { get; private set; }
  protected bool IsLinking { get; private set; }
  protected int? UnlinkingPago20DocumentId { get; private set; }
  protected long? UnlinkingLegacyPago20Id { get; private set; }
  protected string? ErrorMessage { get; private set; }

  protected int LinkedCount => Workspace.Linked.Comprobantes.Count
    + Workspace.Linked.ComplementosPago.Sum(item => item.Documentos.Count)
    + Workspace.Linked.LegacyComplementosPago.Count;
  protected int CandidateCount => Workspace.RegularCandidates.Count + Workspace.Pago20Candidates.Count;
  protected bool HasSelection => SelectedKind != CandidateSelectionKind.None;
  protected bool CanLink => Header is not null
    && HasSelection
    && LinkMonto > 0m
    && !IsLinking
    && (SelectedRegularCandidate?.CanLink ?? SelectedPago20Candidate?.CanLink ?? false);
  protected decimal HeaderMontoAbs => Header is null ? 0m : Math.Abs(Header.Monto);
  protected decimal RegularAsignado => Workspace.Linked.Comprobantes.Sum(item => item.AsignadoCfdi);
  protected decimal Pago20Asignado => Workspace.Linked.ComplementosPago.Sum(item => item.MontoAsignado)
    + Workspace.Linked.LegacyComplementosPago.Sum(item => item.MontoAsignado);
  protected decimal RegularPendiente => HeaderMontoAbs - RegularAsignado;
  protected decimal Pago20Pendiente => HeaderMontoAbs - Pago20Asignado;

  protected override async Task OnParametersSetAsync()
  {
    await LoadAsync();
  }

  protected string FormatCurrency(decimal value)
    => value.ToString("C2", CurrencyCulture);

  protected string FormatRemaining(decimal value)
    => value < -0.01m
      ? $"Excedido {FormatCurrency(Math.Abs(value))}"
      : $"Pendiente {FormatCurrency(Math.Max(0m, value))}";

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

  protected static string SummarizeConcepts(string? concepts, int maxLength = 160)
  {
    if (string.IsNullOrWhiteSpace(concepts))
    {
      return "Sin conceptos";
    }

    var normalized = string.Join(' ', concepts.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    return normalized.Length <= maxLength
      ? normalized
      : $"{normalized[..(maxLength - 1)].TrimEnd()}…";
  }

  protected static string PartyName(string? name)
    => string.IsNullOrWhiteSpace(name) ? "Nombre no disponible" : name.Trim();

  protected bool IsRegularCandidateSelected(TransaccionRegularCfdiLinkCandidateDto candidate)
    => SelectedKind == CandidateSelectionKind.Regular && SelectedRegularCandidate?.ComprobanteId == candidate.ComprobanteId;

  protected bool IsPago20CandidateSelected(TransaccionPago20LinkCandidateDto candidate)
    => SelectedKind == CandidateSelectionKind.Pago20 && SelectedPago20Candidate?.DoctoRelacionadoId == candidate.DoctoRelacionadoId;

  protected void SelectRegularCandidate(TransaccionRegularCfdiLinkCandidateDto candidate)
  {
    if (!candidate.CanLink)
    {
      UiMessages.ShowWarning(candidate.BlockReason ?? "Este CFDI no se puede ligar a la póliza.");
      return;
    }

    SelectedKind = CandidateSelectionKind.Regular;
    SelectedRegularCandidate = candidate;
    SelectedPago20Candidate = null;
    LinkMonto = candidate.MontoSugerido > 0m ? candidate.MontoSugerido : candidate.Total;
  }

  protected void SelectPago20Candidate(TransaccionPago20LinkCandidateDto candidate)
  {
    if (!candidate.CanLink)
    {
      UiMessages.ShowWarning(candidate.BlockReason ?? "Este documento Pago20 no se puede ligar a la póliza.");
      return;
    }

    SelectedKind = CandidateSelectionKind.Pago20;
    SelectedPago20Candidate = candidate;
    SelectedRegularCandidate = null;
    LinkMonto = candidate.MontoSugerido > 0m ? candidate.MontoSugerido : candidate.ImpPagado;
  }

  protected async Task SearchCandidatesAsync()
  {
    if (IsRefreshingWorkspace || Header is null)
    {
      return;
    }

    UiMessages.Clear();
    await LoadWorkspaceAsync();
  }

  protected Task HandleFilterSubmitAsync(EditContext _)
    => SearchCandidatesAsync();

  protected async Task ResetFiltersAsync()
  {
    if (Header is null)
    {
      return;
    }

    Filters.ResetFromHeader(Header);
    await LoadWorkspaceAsync();
  }

  protected async Task LinkSelectedAsync()
  {
    if (Header is null || !CanLink)
    {
      return;
    }

    var label = SelectedKind == CandidateSelectionKind.Pago20
      ? $"docto relacionado {SelectedPago20Candidate?.DoctoRelacionadoId}"
      : $"CFDI {SelectedRegularCandidate?.ComprobanteId}";

    var confirmationMessage = $"¿Deseas ligar la transacción {Header.Id} con {label} por {FormatCurrency(LinkMonto)}?";

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

    UiMessages.Clear();
    IsLinking = true;

    try
    {
      var result = SelectedKind switch
      {
        CandidateSelectionKind.Regular when SelectedRegularCandidate is not null
          => await TransaccionService.LinkRegularCfdiAsync(new TransaccionRegularCfdiLinkRequest(
              Header.Id,
              SelectedRegularCandidate.ComprobanteId,
              LinkMonto)),
        CandidateSelectionKind.Pago20 when SelectedPago20Candidate is not null
          => await TransaccionService.LinkPago20DoctoRelacionadoAsync(new TransaccionPago20LinkRequest(
              Header.Id,
              SelectedPago20Candidate.DoctoRelacionadoId,
              LinkMonto)),
        _ => TransaccionCommandResult.Fail("Selecciona un CFDI o complemento antes de ligar.")
      };

      if (result.Success)
      {
        UiMessages.ShowSuccess(result.Message);
        ClearSelection();
        await LoadWorkspaceAsync();
      }
      else
      {
        UiMessages.ShowError(result.Message);
      }
    }
    catch
    {
      UiMessages.ShowError("No se pudo ligar la transacción. Revisa duplicados o restricciones.");
    }
    finally
    {
      IsLinking = false;
      await InvokeAsync(StateHasChanged);
    }
  }

  protected async Task UnlinkPago20DocumentAsync(TransaccionPago20DoctoRelacionadoDto document)
  {
    if (Header is null || UnlinkingPago20DocumentId.HasValue)
      return;

    var confirmed = await JsRuntime.InvokeAsync<bool>(
        "confirm",
        $"¿Desligar únicamente el documento relacionado {document.DoctoRelacionadoId} de la póliza {Header.Id}?");
    if (!confirmed)
      return;

    UnlinkingPago20DocumentId = document.DoctoRelacionadoId;
    try
    {
      var result = await TransaccionService.UnlinkPago20DoctoRelacionadoAsync(Header.Id, document.DoctoRelacionadoId);
      if (result.Success)
      {
        UiMessages.ShowSuccess(result.Message);
        await LoadWorkspaceAsync();
      }
      else
      {
        UiMessages.ShowError(result.Message);
      }
    }
    finally
    {
      UnlinkingPago20DocumentId = null;
    }
  }

  protected async Task UnlinkLegacyPago20Async(TransaccionPago20LegacyLinkDto legacy)
  {
    if (Header is null || UnlinkingLegacyPago20Id.HasValue)
      return;

    var confirmed = await JsRuntime.InvokeAsync<bool>(
        "confirm",
        $"¿Desligar el vínculo Pago20 legado al comprobante {legacy.ComprobanteId}? Esta acción no afecta vínculos por documento.");
    if (!confirmed)
      return;

    UnlinkingLegacyPago20Id = legacy.ComprobanteId;
    try
    {
      var result = await TransaccionService.UnlinkLegacyPago20Async(Header.Id, legacy.ComprobanteId);
      if (result.Success)
      {
        UiMessages.ShowSuccess(result.Message);
        await LoadWorkspaceAsync();
      }
      else
      {
        UiMessages.ShowError(result.Message);
      }
    }
    finally
    {
      UnlinkingLegacyPago20Id = null;
    }
  }

  protected async Task OpenCfdiAsync(int? xmlAttachmentId)
  {
    if (!xmlAttachmentId.HasValue)
    {
      return;
    }

    var url = $"/cfdi/html-cfdi/{xmlAttachmentId.Value}";

    try
    {
      await JsRuntime.InvokeVoidAsync("open", url, "_blank", "noopener,noreferrer");
    }
    catch
    {
      Nav.NavigateTo(url);
    }
  }

  private async Task LoadAsync()
  {
    IsLoading = true;
    ErrorMessage = null;
    Workspace = new TransaccionCfdiLinkingWorkspaceDto();
    ClearSelection();
    UiMessages.Clear();

    try
    {
      var header = await TransaccionService.GetHeaderAsync(Id);
      if (header is null)
      {
        ErrorMessage = "Transacción no encontrada.";
        return;
      }

      Header = header;
      Filters.ResetFromHeader(header);
      await LoadWorkspaceAsync();
    }
    catch
    {
      ErrorMessage = "No se pudo cargar la información de la transacción.";
    }
    finally
    {
      IsLoading = false;
      await InvokeAsync(StateHasChanged);
    }
  }

  private async Task LoadWorkspaceAsync()
  {
    if (Header is null)
    {
      Workspace = new TransaccionCfdiLinkingWorkspaceDto();
      return;
    }

    var previousKind = SelectedKind;
    var previousRegularId = SelectedRegularCandidate?.ComprobanteId;
    var previousPago20Id = SelectedPago20Candidate?.DoctoRelacionadoId;

    IsRefreshingWorkspace = true;

    try
    {
      var request = new TransaccionCfdiSearchRequest
      {
        Rfc = Header.Rfc ?? string.Empty,
        Monto = Filters.Monto,
        Concepto = Normalize(Filters.Concepto),
        ComprobanteId = Filters.ComprobanteId,
        Tipo = Normalize(Filters.Tipo),
        Renglones = Filters.Renglones
      };

      Workspace = await TransaccionService.GetTransaccionCfdiLinkingWorkspaceAsync(Header.Id, request);
      RestoreSelection(previousKind, previousRegularId, previousPago20Id);
    }
    catch
    {
      UiMessages.ShowError("No se pudieron cargar los comprobantes y candidatos de la transacción.");
      ClearSelection();
    }
    finally
    {
      IsRefreshingWorkspace = false;
      await InvokeAsync(StateHasChanged);
    }
  }

  private void RestoreSelection(CandidateSelectionKind previousKind, long? previousRegularId, int? previousPago20Id)
  {
    ClearSelection();

    if (previousKind == CandidateSelectionKind.Regular && previousRegularId.HasValue)
    {
      var candidate = Workspace.RegularCandidates.FirstOrDefault(item => item.ComprobanteId == previousRegularId.Value);
      if (candidate is not null)
      {
        SelectRegularCandidate(candidate);
      }
    }
    else if (previousKind == CandidateSelectionKind.Pago20 && previousPago20Id.HasValue)
    {
      var candidate = Workspace.Pago20Candidates.FirstOrDefault(item => item.DoctoRelacionadoId == previousPago20Id.Value);
      if (candidate is not null)
      {
        SelectPago20Candidate(candidate);
      }
    }
  }

  private void ClearSelection()
  {
    SelectedKind = CandidateSelectionKind.None;
    SelectedRegularCandidate = null;
    SelectedPago20Candidate = null;
    LinkMonto = 0m;
  }

  private static string? Normalize(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  protected enum CandidateSelectionKind
  {
    None,
    Regular,
    Pago20
  }

  protected sealed class CandidateFilterState
  {
    public decimal? Monto { get; set; }
    public string? Concepto { get; set; }
    public long? ComprobanteId { get; set; }
    public string? Tipo { get; set; }
    public int Renglones { get; set; } = 50;

    public void ResetFromHeader(TransaccionHeaderDto header)
    {
      Monto = header is null ? null : Math.Abs(header.Monto);
      Concepto = null;
      ComprobanteId = null;
      Tipo = null;
      Renglones = 50;
    }
  }
}
