using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Purchasing;
using OrionERP.Web.Services;

namespace OrionERP.Web.Features.Logistica.Purchasing;

/// <summary>
/// Paso de contabilidad de una compra: muestra lo recibido contra lo registrado y deja crear la
/// póliza o ligar una existente. Vive fuera del formulario de la compra para que Enter y los
/// botones no envíen ese formulario. Las confirmaciones son parte de la página, no ventanas del
/// navegador: dicen qué va a pasar con los montos a la vista y no se aceptan por reflejo.
/// </summary>
public partial class PurchaseOrderAccountingPanel : ComponentBase
{
  private int _loadedOrderId;
  private int _loadedRefreshKey = -1;

  [Parameter, EditorRequired] public int PurchaseOrderId { get; set; }

  /// <summary>Cambia cuando la compra recibe algo nuevo; obliga a recalcular lo pendiente.</summary>
  [Parameter] public int RefreshKey { get; set; }

  [Parameter] public EventCallback<PurchaseAccountingSummaryDto?> OnSummaryChanged { get; set; }

  [Inject] private IPurchaseAccountingService AccountingService { get; set; } = default!;
  [Inject] private ICurrentUserAccessor CurrentUser { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;
  [Inject] private IOperationErrorPresenter Errors { get; set; } = default!;

  protected PurchaseAccountingWorkspaceDto? Workspace { get; private set; }
  protected PurchaseAccountingSummaryDto? Summary => Workspace?.Summary;
  protected bool IsLoading { get; private set; }
  protected string? LoadError { get; private set; }

  protected DateTime PolicyDate { get; set; } = DateTime.Today;
  protected string? PaymentMethod { get; set; }
  protected bool IsGenerating { get; private set; }
  protected bool IsConfirmingGenerate { get; private set; }
  protected int? ConfirmingRemoveLinkId { get; private set; }

  protected bool ShowLinkSearch { get; private set; }
  protected string CandidateSearch { get; set; } = string.Empty;
  protected List<PurchaseAccountingCandidateDto> Candidates { get; } = [];
  protected bool IsSearching { get; private set; }
  protected bool HasSearched { get; private set; }
  protected PurchaseAccountingCandidateDto? SelectedCandidate { get; private set; }
  protected decimal LinkAmount { get; set; }
  protected bool IsLinking { get; private set; }
  protected int? RemovingLinkId { get; private set; }

  protected bool IsBusy => IsGenerating || IsLinking || RemovingLinkId.HasValue;

  protected bool CanGenerate => Summary is { PorContabilizar: > 0m, CuentasListas: true }
    && !string.IsNullOrWhiteSpace(PaymentMethod)
    && PolicyDate != default
    && !IsBusy;

  protected decimal MaxLinkAmount => SelectedCandidate is null || Summary is null
    ? 0m
    : Math.Max(Math.Min(SelectedCandidate.Disponible, Summary.PorContabilizar), 0m);

  protected bool CanLink => SelectedCandidate is not null && LinkAmount > 0m && LinkAmount <= MaxLinkAmount && !IsBusy;

  protected string PaymentMethodLabel
    => Workspace?.FormasPago.FirstOrDefault(forma => forma.Clave == PaymentMethod) is { } forma
      ? $"{forma.Descripcion} ({forma.Clave})"
      : PaymentMethod ?? string.Empty;

  protected string PolicyCountLabel => (Workspace?.Polizas.Count ?? 0) switch
  {
    0 => "Sin pólizas todavía",
    1 => "En 1 póliza",
    var count => $"En {count} pólizas"
  };

  protected override async Task OnParametersSetAsync()
  {
    if (PurchaseOrderId == _loadedOrderId && RefreshKey == _loadedRefreshKey) return;

    if (PurchaseOrderId != _loadedOrderId) ResetLinkSearch(collapse: true);
    _loadedOrderId = PurchaseOrderId;
    _loadedRefreshKey = RefreshKey;
    await LoadAsync();
  }

  protected Task RetryAsync() => LoadAsync();

  private async Task LoadAsync()
  {
    IsLoading = true;
    LoadError = null;
    IsConfirmingGenerate = false;
    ConfirmingRemoveLinkId = null;
    try
    {
      Workspace = await AccountingService.GetPurchaseOrderWorkspaceAsync(PurchaseOrderId);
      if (Workspace is not null)
      {
        PolicyDate = (Workspace.Summary.UltimaRecepcion ?? DateTime.Today).Date;
        PaymentMethod = Workspace.FormaPagoSugerida;
      }
    }
    catch (Exception ex)
    {
      Workspace = null;
      LoadError = Errors.ToUserMessage(ex, "consultar la contabilidad de la compra", new { PurchaseOrderId });
    }
    finally
    {
      IsLoading = false;
    }

    await OnSummaryChanged.InvokeAsync(Summary);
  }

  /// <summary>Crear una póliza no se deshace: primero se muestra qué se va a registrar.</summary>
  protected void AskGenerate()
  {
    if (CanGenerate) IsConfirmingGenerate = true;
  }

  protected void CancelGenerate() => IsConfirmingGenerate = false;

  protected async Task GenerateAsync()
  {
    if (!CanGenerate) return;

    var fecha = PolicyDate.Date;
    IsConfirmingGenerate = false;
    IsGenerating = true;
    try
    {
      var result = await AccountingService.GeneratePolicyAsync(new PurchaseAccountingGenerateRequest
      {
        PurchaseOrderId = PurchaseOrderId,
        Fecha = fecha,
        FormaPago = PaymentMethod
      }, await CurrentUser.GetUserNameAsync());

      if (result.Success)
      {
        UiMessages.ShowSuccess(result.Message);
        ResetLinkSearch(collapse: true);
      }
      else
      {
        UiMessages.ShowError(result.Message);
      }

      await LoadAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError(Errors.ToUserMessage(ex, "crear la póliza de la compra", new { PurchaseOrderId }));
    }
    finally
    {
      IsGenerating = false;
    }
  }

  protected async Task ToggleLinkSearchAsync()
  {
    ShowLinkSearch = !ShowLinkSearch;
    if (ShowLinkSearch && !HasSearched) await SearchAsync();
  }

  protected async Task SearchAsync()
  {
    IsSearching = true;
    SelectedCandidate = null;
    LinkAmount = 0m;
    try
    {
      var rows = await AccountingService.SearchPolicyCandidatesAsync(PurchaseOrderId, CandidateSearch);
      Candidates.Clear();
      Candidates.AddRange(rows);
      HasSearched = true;
    }
    catch (Exception ex)
    {
      UiMessages.ShowError(Errors.ToUserMessage(ex, "buscar pólizas para la compra", new { PurchaseOrderId, CandidateSearch }));
    }
    finally
    {
      IsSearching = false;
    }
  }

  protected async Task HandleSearchKeyDownAsync(KeyboardEventArgs args)
  {
    if (args.Key == "Enter") await SearchAsync();
  }

  protected void SelectCandidate(PurchaseAccountingCandidateDto candidate)
  {
    SelectedCandidate = candidate;
    LinkAmount = MaxLinkAmount;
  }

  protected void CancelSelection()
  {
    SelectedCandidate = null;
    LinkAmount = 0m;
  }

  protected async Task LinkAsync()
  {
    // El recuadro de la póliza elegida, con su monto, ya es la confirmación; ligar se deshace con "Quitar".
    if (!CanLink || SelectedCandidate is not { } candidate) return;

    IsLinking = true;
    try
    {
      var result = await AccountingService.LinkAsync(new PurchaseAccountingLinkRequest
      {
        PurchaseOrderId = PurchaseOrderId,
        TransaccionId = candidate.TransaccionId,
        Monto = LinkAmount
      }, await CurrentUser.GetUserNameAsync());

      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      ResetLinkSearch(collapse: true);
      await LoadAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError(Errors.ToUserMessage(ex, "ligar la póliza a la compra", new { PurchaseOrderId, candidate.TransaccionId }));
    }
    finally
    {
      IsLinking = false;
    }
  }

  /// <summary>
  /// Sólo para pólizas ligadas a mano. Quitar una póliza generada dejaría la compra pendiente con
  /// el gasto ya registrado; eso lo corrige Contabilidad desde la póliza.
  /// </summary>
  protected void AskRemove(PurchaseAccountingLinkedPolizaDto poliza)
  {
    if (!poliza.FueGenerada && !IsBusy) ConfirmingRemoveLinkId = poliza.LinkId;
  }

  protected void CancelRemove() => ConfirmingRemoveLinkId = null;

  protected async Task RemoveAsync(PurchaseAccountingLinkedPolizaDto poliza)
  {
    if (poliza.FueGenerada || IsBusy || ConfirmingRemoveLinkId != poliza.LinkId) return;

    RemovingLinkId = poliza.LinkId;
    try
    {
      var result = await AccountingService.UnlinkAsync(poliza.LinkId);
      if (result.Success) UiMessages.ShowSuccess(result.Message);
      else UiMessages.ShowWarning(result.Message);

      await LoadAsync();
      if (ShowLinkSearch) await SearchAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError(Errors.ToUserMessage(ex, "quitar la póliza de la compra", new { PurchaseOrderId, poliza.LinkId }));
    }
    finally
    {
      RemovingLinkId = null;
    }
  }

  private void ResetLinkSearch(bool collapse)
  {
    if (collapse) ShowLinkSearch = false;
    Candidates.Clear();
    HasSearched = false;
    SelectedCandidate = null;
    LinkAmount = 0m;
    CandidateSearch = string.Empty;
  }

  protected static string Money(decimal amount) => PurchaseAccountingDisplay.Money(amount);
}
