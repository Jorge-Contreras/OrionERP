using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using OrionERP.Application.Features.Contabilidad.Bancos;
using OrionERP.Web.Services;

namespace OrionERP.Web.Features.Contabilidad.Bancos;

public partial class BancoMovimientoLinkingPage : ComponentBase
{
  private static readonly CultureInfo CurrencyCulture = new("es-MX");
  private int? _fixingTransaccionId;

  [Parameter]
  public long MovimientoId { get; set; }

  [SupplyParameterFromQuery(Name = "transaccionId")]
  public int? FocusTransaccionId { get; set; }

  [Inject]
  public IBancosService BancosService { get; set; } = default!;

  [Inject]
  public IUiMessageService UiMessages { get; set; } = default!;

  [Inject]
  public NavigationManager NavManager { get; set; } = default!;

  [Inject]
  public IOperationErrorPresenter Errors { get; set; } = default!;

  protected BankMovementLinkingWorkspaceDto Workspace { get; private set; } = new();
  protected BankMovementLinkingSummaryDto? Summary => Workspace.Summary;
  protected List<LinkEditor> Editors { get; } = [];
  protected List<BankMovementTransactionCandidateDto> Candidates => Workspace.Candidates;
  protected string? Search { get; set; }
  protected bool IncludeOtherCandidates { get; set; }
  protected bool IsLoading { get; private set; }
  protected bool IsSearching { get; private set; }
  protected bool IsSaving { get; private set; }
  protected string? ErrorMessage { get; private set; }

  protected decimal AssignedDebe => Editors.Sum(item => item.Debe);
  protected decimal AssignedHaber => Editors.Sum(item => item.Haber);
  protected decimal RemainingDebe => Summary is null ? 0m : Summary.ExpectedDebe - AssignedDebe;
  protected decimal RemainingHaber => Summary is null ? 0m : Summary.ExpectedHaber - AssignedHaber;

  protected bool CanSave
    => Summary?.MappingValid == true &&
       Editors.Count > 0 &&
       !IsSaving &&
       Math.Abs(RemainingDebe) <= 0.01m &&
       Math.Abs(RemainingHaber) <= 0.01m &&
       Editors.All(IsEditorSideValid);

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
      await LoadWorkspaceAsync(preserveEditors: true);
    }
    finally
    {
      IsSearching = false;
    }
  }

  protected async Task ClearSearchAsync()
  {
    Search = null;
    IncludeOtherCandidates = false;
    await SearchAsync();
  }

  protected void AddCandidate(BankMovementTransactionCandidateDto candidate)
  {
    if (Summary is null || candidate is null || IsCandidateAdded(candidate))
    {
      return;
    }

    var remaining = Summary.IsCargo ? RemainingDebe : RemainingHaber;
    var available = Summary.IsCargo ? candidate.AvailableDebe : candidate.AvailableHaber;
    var suggested = available > 0m ? decimal.Min(remaining, available) : remaining;
    suggested = decimal.Round(decimal.Max(0m, suggested), 2);

    Editors.Add(LinkEditor.FromCandidate(candidate, Summary.IsCargo, suggested));
  }

  protected void RemoveEditor(LinkEditor editor)
  {
    Editors.Remove(editor);
  }

  protected bool IsCandidateAdded(BankMovementTransactionCandidateDto candidate)
    => Editors.Any(item => item.TransaccionId == candidate.TransaccionId);

  protected async Task SaveAsync()
  {
    if (!CanSave || Summary is null)
    {
      UiMessages.ShowWarning("La asignación debe cuadrar exactamente antes de guardar.");
      return;
    }

    IsSaving = true;

    try
    {
      var request = new BankMovementLinkSaveRequest
      {
        MovimientoId = Summary.MovimientoId,
        Actor = "OrionERP"
      };

      request.Links.AddRange(Editors.Select(editor => new BankMovementLinkSaveItem
      {
        TransaccionId = editor.TransaccionId,
        Debe = editor.Debe,
        Haber = editor.Haber
      }));

      var result = await BancosService.SaveMovementLinksAsync(request);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      await LoadWorkspaceAsync(preserveEditors: false);
    }
    finally
    {
      IsSaving = false;
    }
  }

  protected bool CanFixBankLine(LinkEditor editor)
    => Summary?.MappingValid == true && IsEditorSideValid(editor) && !IsFixing(editor);

  protected bool IsFixing(LinkEditor editor)
    => _fixingTransaccionId == editor.TransaccionId;

  protected async Task FixBankLineAsync(LinkEditor editor)
  {
    if (Summary is null || !CanFixBankLine(editor))
    {
      return;
    }

    _fixingTransaccionId = editor.TransaccionId;

    try
    {
      var result = await BancosService.FixMovementTransactionBankLineAsync(new BankMovementAccountingFixRequest
      {
        MovimientoId = Summary.MovimientoId,
        TransaccionId = editor.TransaccionId,
        Debe = editor.Debe,
        Haber = editor.Haber,
        Actor = "OrionERP"
      });

      if (!result.Success)
      {
        UiMessages.ShowWarning(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      await LoadWorkspaceAsync(preserveEditors: true);
    }
    finally
    {
      _fixingTransaccionId = null;
    }
  }

  protected void GoBack()
  {
    NavManager.NavigateTo("/contabilidad/bancos");
  }

  protected string FormatCurrency(decimal value)
    => value.ToString("C2", CurrencyCulture);

  protected static string FormatBankAccount(BankMovementLinkingSummaryDto summary)
    => summary.MappingValid
      ? $"{summary.BankAccountNivel1}.{summary.BankAccountNivel2}.{summary.BankAccountNivel3}"
      : "Sin configurar";

  protected static string FormatStatus(string? status)
    => status switch
    {
      "FUERTE" => "Fuerte",
      "POSIBLE" => "Posible",
      "OTRA" => "Otra",
      "OK" => "OK",
      "REVISAR" => "Revisar",
      _ => string.IsNullOrWhiteSpace(status) ? "Sin dato" : status
    };

  protected static string GetCandidateBadgeClass(BankMovementTransactionCandidateDto candidate)
    => candidate.MatchStatus switch
    {
      "FUERTE" => "text-bg-success",
      "POSIBLE" => "text-bg-primary",
      "REVISAR" => "text-bg-warning",
      "OTRA" => "text-bg-secondary",
      _ => "text-bg-secondary"
    };

  protected string GetEditorStatus(LinkEditor editor)
  {
    if (!IsEditorSideValid(editor))
    {
      return "Importe inválido";
    }

    var available = Summary?.IsCargo == true ? editor.AvailableDebe : editor.AvailableHaber;
    var requested = Summary?.IsCargo == true ? editor.Debe : editor.Haber;

    return requested <= available + 0.01m ? "Listo" : "Requiere RC";
  }

  protected string GetEditorBadgeClass(LinkEditor editor)
    => string.Equals(GetEditorStatus(editor), "Listo", StringComparison.Ordinal)
      ? "text-bg-success"
      : "text-bg-warning";

  private bool IsEditorSideValid(LinkEditor editor)
  {
    if (Summary is null)
    {
      return false;
    }

    return Summary.IsCargo
      ? editor.Debe > 0m && editor.Haber == 0m
      : editor.Haber > 0m && editor.Debe == 0m;
  }

  private async Task LoadAsync()
  {
    IsLoading = true;
    ErrorMessage = null;

    try
    {
      await LoadWorkspaceAsync(preserveEditors: false);
    }
    catch (Exception ex)
    {
      ErrorMessage = Errors.ToUserMessage(ex, "cargar el espacio de ligado de movimientos bancarios", new { FocusTransaccionId });
    }
    finally
    {
      IsLoading = false;
    }
  }

  private async Task LoadWorkspaceAsync(bool preserveEditors)
  {
    var previousEditors = preserveEditors
      ? Editors.ToDictionary(item => item.TransaccionId, item => item)
      : new Dictionary<int, LinkEditor>();

    Workspace = await BancosService.GetMovementLinkingWorkspaceAsync(
      MovimientoId,
      Search,
      IncludeOtherCandidates,
      FocusTransaccionId);

    Editors.Clear();
    if (preserveEditors && previousEditors.Count > 0)
    {
      foreach (var link in Workspace.Links)
      {
        if (previousEditors.TryGetValue(link.TransaccionId, out var existing))
        {
          existing.RefreshFromLink(link);
          Editors.Add(existing);
        }
        else
        {
          Editors.Add(LinkEditor.FromLink(link));
        }
      }

      foreach (var existing in previousEditors.Values.Where(item => Workspace.Links.All(link => link.TransaccionId != item.TransaccionId)))
      {
        var candidate = Workspace.Candidates.FirstOrDefault(item => item.TransaccionId == existing.TransaccionId);
        if (candidate is not null)
        {
          existing.RefreshFromCandidate(candidate);
        }

        Editors.Add(existing);
      }
    }
    else
    {
      Editors.AddRange(Workspace.Links.Select(LinkEditor.FromLink));
    }

    if (FocusTransaccionId.HasValue)
    {
      var candidate = Workspace.Candidates.FirstOrDefault(item => item.TransaccionId == FocusTransaccionId.Value);
      if (candidate is not null)
      {
        AddCandidate(candidate);
      }
    }
  }

  protected sealed class LinkEditor
  {
    public int TransaccionId { get; set; }
    public DateTime Fecha { get; set; }
    public string Concepto { get; set; } = string.Empty;
    public decimal Debe { get; set; }
    public decimal Haber { get; set; }
    public decimal BankRegistroDebe { get; set; }
    public decimal BankRegistroHaber { get; set; }
    public decimal AvailableDebe { get; set; }
    public decimal AvailableHaber { get; set; }

    public static LinkEditor FromLink(BankMovementTransactionLinkDto link)
      => new()
      {
        TransaccionId = link.TransaccionId,
        Fecha = link.Fecha,
        Concepto = link.Concepto,
        Debe = link.Debe,
        Haber = link.Haber,
        BankRegistroDebe = link.BankRegistroDebe,
        BankRegistroHaber = link.BankRegistroHaber,
        AvailableDebe = link.AvailableDebe,
        AvailableHaber = link.AvailableHaber
      };

    public static LinkEditor FromCandidate(BankMovementTransactionCandidateDto candidate, bool isCargo, decimal suggested)
      => new()
      {
        TransaccionId = candidate.TransaccionId,
        Fecha = candidate.Fecha,
        Concepto = candidate.Concepto,
        Debe = isCargo ? suggested : 0m,
        Haber = isCargo ? 0m : suggested,
        BankRegistroDebe = candidate.BankRegistroDebe,
        BankRegistroHaber = candidate.BankRegistroHaber,
        AvailableDebe = candidate.AvailableDebe,
        AvailableHaber = candidate.AvailableHaber
      };

    public void RefreshFromLink(BankMovementTransactionLinkDto link)
    {
      Fecha = link.Fecha;
      Concepto = link.Concepto;
      BankRegistroDebe = link.BankRegistroDebe;
      BankRegistroHaber = link.BankRegistroHaber;
      AvailableDebe = link.AvailableDebe;
      AvailableHaber = link.AvailableHaber;
    }

    public void RefreshFromCandidate(BankMovementTransactionCandidateDto candidate)
    {
      Fecha = candidate.Fecha;
      Concepto = candidate.Concepto;
      BankRegistroDebe = candidate.BankRegistroDebe;
      BankRegistroHaber = candidate.BankRegistroHaber;
      AvailableDebe = candidate.AvailableDebe;
      AvailableHaber = candidate.AvailableHaber;
    }
  }
}
