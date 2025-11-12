using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
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

  protected TransaccionHeaderDto? Header { get; private set; }
  protected List<TransaccionCfdiCandidateDto> CandidateRows { get; } = new();
  protected List<TransaccionCfdiCandidateDto> LinkedRows { get; } = new();
  protected CandidateFilterState Filters { get; } = new();
  protected TransaccionCfdiCandidateDto? SelectedCandidate { get; private set; }

  protected bool IsLoading { get; private set; } = true;
  protected bool IsSearchingCandidates { get; private set; }
  protected bool IsLoadingLinked { get; private set; }
  protected bool IsLinking { get; private set; }
  protected string? ErrorMessage { get; private set; }

  protected override async Task OnParametersSetAsync()
  {
    await LoadAsync();
  }

  protected bool IsCandidateSelected(TransaccionCfdiCandidateDto candidate)
    => SelectedCandidate?.ComprobanteId == candidate?.ComprobanteId;

  protected string FormatCurrency(decimal value)
    => value.ToString("C2", CurrencyCulture);

  protected void SelectCandidate(TransaccionCfdiCandidateDto candidate)
  {
    SelectedCandidate = candidate;
  }

  protected async Task SearchCandidatesAsync()
  {
    if (IsSearchingCandidates || Header?.Rfc is null)
    {
      return;
    }

    UiMessages.Clear();
    await LoadCandidatesAsync();
  }

  protected async Task LinkSelectedAsync()
  {
    if (Header is null || SelectedCandidate is null || IsLinking)
    {
      return;
    }

    var confirmationMessage = $"¿Estás seguro de que deseas ligar la transacción {Header.Id} con el comprobante {SelectedCandidate.ComprobanteId}?";

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
      var monto = Filters.Monto ?? SelectedCandidate.Total;
      var linkRequest = new TransaccionCfdiLinkRequest
      {
        TransaccionId = Header.Id,
        ComprobanteId = SelectedCandidate.ComprobanteId,
        Monto = monto,
        UseDoctoRelacionadoTable = string.Equals(SelectedCandidate.Tipo, "COMP", StringComparison.OrdinalIgnoreCase)
      };

      var result = await TransaccionService.LinkCfdiAsync(linkRequest);

      if (result.Success)
      {
        UiMessages.ShowSuccess(result.Message);
        SelectedCandidate = null;
        await LoadLinkedAsync();
        await LoadCandidatesAsync();
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

  private async Task LoadAsync()
  {
    IsLoading = true;
    ErrorMessage = null;
    CandidateRows.Clear();
    LinkedRows.Clear();
    SelectedCandidate = null;
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

      await LoadLinkedAsync();
      await LoadCandidatesAsync();
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

  private async Task LoadCandidatesAsync()
  {
    if (Header?.Rfc is null)
    {
      CandidateRows.Clear();
      SelectedCandidate = null;
      await InvokeAsync(StateHasChanged);
      return;
    }

    var previousSelectedId = SelectedCandidate?.ComprobanteId;
    IsSearchingCandidates = true;
    CandidateRows.Clear();

    try
    {
      var request = new TransaccionCfdiSearchRequest
      {
        Rfc = Header.Rfc,
        Monto = Filters.Monto,
        Concepto = Normalize(Filters.Concepto),
        ComprobanteId = Filters.ComprobanteId,
        Tipo = Normalize(Filters.Tipo),
        Renglones = Filters.Renglones
      };

      var rows = await TransaccionService.GetCfdiCandidatesAsync(request);
      CandidateRows.AddRange(rows);

      SelectedCandidate = CandidateRows.FirstOrDefault(candidate => candidate.ComprobanteId == previousSelectedId);
    }
    catch
    {
      UiMessages.ShowError("No se pudieron cargar los CFDIs candidatos. Intenta nuevamente.");
      SelectedCandidate = null;
    }
    finally
    {
      IsSearchingCandidates = false;
      await InvokeAsync(StateHasChanged);
    }
  }

  private async Task LoadLinkedAsync()
  {
    if (Header is null)
    {
      LinkedRows.Clear();
      return;
    }

    IsLoadingLinked = true;
    LinkedRows.Clear();

    try
    {
      var ids = await TransaccionService.GetLinkedCfdiIdsAsync(Header.Id);
      if (ids.Count == 0)
      {
        return;
      }

      var csv = string.Join(",", ids);
      var request = new TransaccionCfdiSearchRequest
      {
        Rfc = Header.Rfc ?? string.Empty,
        ComprobantesCsv = csv,
        Renglones = Filters.Renglones
      };

      var rows = await TransaccionService.GetCfdiCandidatesAsync(request);
      LinkedRows.AddRange(rows);
    }
    catch
    {
      UiMessages.ShowError("No se pudieron cargar los comprobantes ligados.");
    }
    finally
    {
      IsLoadingLinked = false;
      await InvokeAsync(StateHasChanged);
    }
  }

  private static string? Normalize(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  protected sealed class CandidateFilterState
  {
    public decimal? Monto { get; set; }
    public string? Concepto { get; set; }
    public long? ComprobanteId { get; set; }
    public string? Tipo { get; set; }
    public int Renglones { get; set; } = 25;

    public void ResetFromHeader(TransaccionHeaderDto header)
    {
      Monto = header?.Monto;
      Concepto = null;
      ComprobanteId = null;
      Tipo = null;
    }
  }
}
