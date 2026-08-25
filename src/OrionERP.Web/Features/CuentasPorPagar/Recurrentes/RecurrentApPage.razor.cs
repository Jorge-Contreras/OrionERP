using OrionERP.Application.Common;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using OrionERP.Application.Features.CuentasPorPagar.Recurrentes;
using OrionERP.Web.Services;
using OrionERP.Web.State;
using System.Globalization;

namespace OrionERP.Web.Features.CuentasPorPagar.Recurrentes;

public partial class RecurrentApPage : ComponentBase
{
  private const long AttachmentMaxFileSize = RecurrentApAttachmentCreateRequest.MaxFileSizeBytes;
  private int? _handledRequestedOccurrenceId;

  [Inject] private IRecurrentApService ApService { get; set; } = default!;
  [Inject] private ICurrentCompanyContext RfcState { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;
  [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
  [Inject] private IJSRuntime Js { get; set; } = default!;

  [Parameter]
  [SupplyParameterFromQuery(Name = "occurrenceId")]
  public int? RequestedOccurrenceId { get; set; }

  protected RecurrentApWorkspaceDto Workspace { get; set; } = new();
  protected RecurrentApFilter Filter { get; set; } = new();
  protected RecurrentApUpsertRequest Editor { get; set; } = CreateEditor();
  protected RecurrentApOccurrenceStatusRequest StatusEditor { get; set; } = new();
  protected RecurrentApOccurrenceListItemDto? SelectedOccurrence { get; set; }
  protected RecurrentApOccurrenceDetailDto? OccurrenceDetail { get; set; }
  protected List<RecurrentApAttachmentDto> Attachments { get; set; } = [];
  protected List<RecurrentApTransactionLinkDto> LinkedTransactions { get; set; } = [];
  protected List<RecurrentApTransactionCandidateDto> TransactionCandidates { get; set; } = [];
  protected string TransactionSearchText { get; set; } = string.Empty;
  protected string CurrentUserName { get; set; } = "OrionERP";
  protected bool IsLoading { get; set; }
  protected bool IsSavingPayable { get; set; }
  protected bool IsSavingOccurrence { get; set; }
  protected bool IsLinkingTransaction { get; set; }
  protected int? UnlinkingPaymentId { get; set; }
  protected bool IsUploadingAttachment { get; set; }
  protected bool IsReseedingPayable { get; set; }
  protected bool IsCancellingOccurrence { get; set; }
  protected bool IsReadOnly { get; set; }
  protected bool IsEditorVisible { get; set; } = true;
  protected bool AreOccurrencesVisible { get; set; } = true;
  protected bool IsReadOnlyOrSaving => IsReadOnly || IsSavingPayable;
  protected bool IsReadOnlyOrMutating => IsReadOnly || IsSavingOccurrence || IsLinkingTransaction || UnlinkingPaymentId.HasValue || IsUploadingAttachment || IsReseedingPayable || IsCancellingOccurrence;
  protected bool CanUseProviderCredentials => !IsReadOnly;
  protected string CurrentRfc => RfcState.RequireRfc();
  protected string EditorTitle => Editor.Id.HasValue ? "Editar recurrente" : "Nuevo recurrente";

  protected override async Task OnInitializedAsync()
  {
    CurrentUserName = await ResolveCurrentUserAsync();
    await ResolvePermissionsAsync();
    ResetFilters();
    await LoadWorkspaceAsync();
  }

  protected override async Task OnParametersSetAsync()
  {
    if (!RequestedOccurrenceId.HasValue
      || RequestedOccurrenceId.Value <= 0
      || RequestedOccurrenceId == _handledRequestedOccurrenceId)
    {
      return;
    }

    await SelectRequestedOccurrenceAsync(RequestedOccurrenceId.Value);
  }

  protected async Task RefreshAsync()
  {
    await LoadWorkspaceAsync();
  }

  protected async Task ClearFiltersAsync()
  {
    ResetFilters();
    await LoadWorkspaceAsync();
  }

  protected void NewPayable()
  {
    Editor = CreateEditor(CurrentRfc);
    IsEditorVisible = true;
  }

  protected async Task EditPayable(int payableId)
  {
    var payable = await ApService.GetPayableAsync(payableId, CurrentRfc, includePassword: CanUseProviderCredentials);
    if (payable is null)
    {
      UiMessages.ShowWarning("La cuenta recurrente ya no existe.");
      return;
    }

    Editor = new RecurrentApUpsertRequest
    {
      Id = payable.Id,
      Rfc = payable.Rfc,
      Name = payable.Name,
      BusinessPartnerId = payable.BusinessPartnerId,
      PayeeNameSnapshot = payable.PayeeNameSnapshot,
      PayeeRfcSnapshot = payable.PayeeRfcSnapshot,
      Category = payable.Category,
      Description = payable.Description,
      Website = payable.Website,
      UserName = payable.UserName,
      Password = payable.Password,
      FrequencyUnit = payable.FrequencyUnit,
      IntervalCount = payable.IntervalCount,
      StartDate = payable.StartDate,
      EndDate = payable.EndDate,
      DueDayOfMonth = payable.DueDayOfMonth,
      DueMonth = payable.DueMonth,
      ExpectedAmount = payable.ExpectedAmount,
      Currency = payable.Currency,
      IsActive = payable.IsActive
    };
    IsEditorVisible = true;
  }

  protected async Task SavePayableAsync()
  {
    if (IsReadOnly)
    {
      return;
    }

    IsSavingPayable = true;
    try
    {
      Editor.Rfc = CurrentRfc;
      var id = await ApService.SavePayableAsync(Editor, CurrentUserName);
      UiMessages.ShowSuccess("Cuenta recurrente guardada.");
      await LoadWorkspaceAsync();
      await EditPayable(id);
      IsEditorVisible = false;
    }
    catch (Exception ex)
    {
      UiMessages.ShowError(ex.Message);
    }
    finally
    {
      IsSavingPayable = false;
    }
  }

  protected async Task SelectOccurrenceAsync(RecurrentApOccurrenceListItemDto occurrence)
  {
    AreOccurrencesVisible = false;
    SelectedOccurrence = occurrence;
    StatusEditor = new RecurrentApOccurrenceStatusRequest
    {
      OccurrenceId = occurrence.Id,
      Rfc = occurrence.Rfc,
      Status = occurrence.Status,
      ExpectedAmount = occurrence.ExpectedAmount,
      ActualAmount = occurrence.ActualPaidAmount,
      PaymentDate = occurrence.PaymentDate,
      Notes = occurrence.Notes
    };
    await LoadSelectedOccurrenceRelatedDataAsync(occurrence.Id, occurrence.Rfc);
    TransactionCandidates = [];
    TransactionSearchText = string.Empty;
  }

  protected void ShowOccurrences()
  {
    AreOccurrencesVisible = true;
  }

  protected void ShowEditor()
  {
    IsEditorVisible = true;
  }

  protected void ClearSelectedOccurrence()
  {
    SelectedOccurrence = null;
    OccurrenceDetail = null;
    Attachments = [];
    LinkedTransactions = [];
    TransactionCandidates = [];
  }

  protected async Task SaveOccurrenceStatusAsync()
  {
    if (SelectedOccurrence is null || IsReadOnly)
    {
      return;
    }

    IsSavingOccurrence = true;
    try
    {
      StatusEditor.Rfc = SelectedOccurrence.Rfc;
      StatusEditor.OccurrenceId = SelectedOccurrence.Id;
      await ApService.SetOccurrenceStatusAsync(StatusEditor, CurrentUserName);
      UiMessages.ShowSuccess("Estatus AP actualizado.");
      await LoadWorkspaceAsync();
      ClearSelectedOccurrence();
      AreOccurrencesVisible = true;
    }
    catch (Exception ex)
    {
      UiMessages.ShowError(ex.Message);
    }
    finally
    {
      IsSavingOccurrence = false;
    }
  }

  protected async Task ReseedPayableAsync()
  {
    if (!Editor.Id.HasValue || IsReadOnly)
    {
      return;
    }

    var confirmed = await ConfirmAsync("Se eliminarán y recrearán solo los vencimientos futuros pendientes sin pólizas, archivos, notas ni cambios manuales. ¿Deseas continuar?");
    if (!confirmed)
    {
      return;
    }

    IsReseedingPayable = true;
    try
    {
      var result = await ApService.ReseedPayableOccurrencesAsync(Editor.Id.Value, CurrentRfc, CurrentUserName);
      UiMessages.ShowSuccess($"Resiembra completada. Creados: {result.CreatedCount}. Eliminados: {result.DeletedCount}. Conservados: {result.PreservedCount}.");
      await LoadWorkspaceAsync();
      if (SelectedOccurrence is not null)
      {
        var refreshed = Workspace.Occurrences.FirstOrDefault(item => item.Id == SelectedOccurrence.Id);
        if (refreshed is not null)
        {
          await SelectOccurrenceAsync(refreshed);
        }
        else
        {
          ClearSelectedOccurrence();
          AreOccurrencesVisible = true;
        }
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError(ex.Message);
    }
    finally
    {
      IsReseedingPayable = false;
    }
  }

  protected async Task CancelSelectedOccurrenceAsync()
  {
    if (SelectedOccurrence is null || IsReadOnly)
    {
      return;
    }

    var confirmed = await ConfirmAsync("¿Deseas cancelar este vencimiento? No se puede cancelar si tiene pólizas ligadas.");
    if (!confirmed)
    {
      return;
    }

    IsCancellingOccurrence = true;
    try
    {
      await ApService.CancelOccurrenceAsync(SelectedOccurrence.Id, SelectedOccurrence.Rfc, CurrentUserName);
      UiMessages.ShowSuccess("Vencimiento cancelado.");
      await LoadWorkspaceAsync();
      ClearSelectedOccurrence();
      AreOccurrencesVisible = true;
    }
    catch (Exception ex)
    {
      UiMessages.ShowError(ex.Message);
    }
    finally
    {
      IsCancellingOccurrence = false;
    }
  }

  protected async Task CopyProviderValueAsync(string label, string? value)
  {
    if (!CanUseProviderCredentials)
    {
      UiMessages.ShowWarning("No tienes permiso para copiar credenciales del proveedor.");
      return;
    }

    if (string.IsNullOrWhiteSpace(value))
    {
      UiMessages.ShowWarning($"{label} no está configurado.");
      return;
    }

    await Js.InvokeVoidAsync("navigator.clipboard.writeText", value);
    UiMessages.ShowSuccess($"{label} copiado al portapapeles.");
  }

  protected async Task SearchTransactionsAsync()
  {
    TransactionCandidates = (await ApService.SearchTransactionsAsync(CurrentRfc, TransactionSearchText)).ToList();
  }

  protected async Task LinkTransactionAsync(RecurrentApTransactionCandidateDto transaction)
  {
    if (SelectedOccurrence is null || IsReadOnly)
    {
      return;
    }

    IsLinkingTransaction = true;
    try
    {
      await ApService.LinkTransactionAsync(new RecurrentApTransactionLinkRequest
      {
        OccurrenceId = SelectedOccurrence.Id,
        Rfc = SelectedOccurrence.Rfc,
        TransaccionId = transaction.Id,
        Amount = Math.Abs(transaction.Monto),
        PaymentDate = transaction.Fecha
      }, CurrentUserName);

      UiMessages.ShowSuccess("Póliza ligada al vencimiento AP.");
      await LoadWorkspaceAsync();
      var refreshed = Workspace.Occurrences.FirstOrDefault(item => item.Id == SelectedOccurrence.Id);
      if (refreshed is not null)
      {
        await SelectOccurrenceAsync(refreshed);
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError(ex.Message);
    }
    finally
    {
      IsLinkingTransaction = false;
    }
  }

  protected async Task UnlinkTransactionAsync(RecurrentApTransactionLinkDto link)
  {
    if (SelectedOccurrence is null || IsReadOnly)
    {
      return;
    }

    UnlinkingPaymentId = link.PaymentId;
    try
    {
      await ApService.UnlinkTransactionAsync(link.PaymentId, SelectedOccurrence.Rfc, CurrentUserName);
      UiMessages.ShowSuccess("Póliza desligada del vencimiento AP.");
      await LoadWorkspaceAsync();
      var refreshed = Workspace.Occurrences.FirstOrDefault(item => item.Id == SelectedOccurrence.Id);
      if (refreshed is not null)
      {
        await SelectOccurrenceAsync(refreshed);
      }
      else
      {
        ClearSelectedOccurrence();
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError(ex.Message);
    }
    finally
    {
      UnlinkingPaymentId = null;
    }
  }

  protected async Task UploadAttachmentAsync(InputFileChangeEventArgs args)
  {
    if (SelectedOccurrence is null || IsReadOnly)
    {
      return;
    }

    var file = args.File;
    if (file is null)
    {
      return;
    }

    IsUploadingAttachment = true;
    try
    {
      await using var stream = file.OpenReadStream(AttachmentMaxFileSize);
      using var memory = new MemoryStream();
      await stream.CopyToAsync(memory);

      await ApService.AddAttachmentAsync(new RecurrentApAttachmentCreateRequest
      {
        OccurrenceId = SelectedOccurrence.Id,
        Rfc = SelectedOccurrence.Rfc,
        FileName = file.Name,
        ContentType = file.ContentType,
        Content = memory.ToArray(),
        UploadedBy = CurrentUserName
      });

      Attachments = (await ApService.GetAttachmentsAsync(SelectedOccurrence.Id, SelectedOccurrence.Rfc)).ToList();
      UiMessages.ShowSuccess("Archivo AP cargado.");
      await LoadWorkspaceAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError(ex.Message);
    }
    finally
    {
      IsUploadingAttachment = false;
    }
  }

  protected async Task DownloadAttachmentAsync(RecurrentApAttachmentDto attachment)
  {
    if (SelectedOccurrence is null)
    {
      return;
    }

    var content = await ApService.GetAttachmentContentAsync(attachment.Id, SelectedOccurrence.Rfc);
    if (content is null)
    {
      UiMessages.ShowWarning("El archivo ya no está disponible.");
      return;
    }

    var base64 = Convert.ToBase64String(content.Content);
    await Js.InvokeVoidAsync("eval", $"(() => {{ const a = document.createElement('a'); a.href = 'data:{content.ContentType};base64,{base64}'; a.download = {System.Text.Json.JsonSerializer.Serialize(content.FileName)}; a.click(); }})()");
  }

  protected async Task DeleteAttachmentAsync(RecurrentApAttachmentDto attachment)
  {
    if (SelectedOccurrence is null || IsReadOnly)
    {
      return;
    }

    await ApService.DeleteAttachmentAsync(attachment.Id, SelectedOccurrence.Rfc, CurrentUserName);
    Attachments = (await ApService.GetAttachmentsAsync(SelectedOccurrence.Id, SelectedOccurrence.Rfc)).ToList();
    UiMessages.ShowSuccess("Archivo AP eliminado.");
    await LoadWorkspaceAsync();
  }

  private async Task LoadSelectedOccurrenceRelatedDataAsync(int occurrenceId, string rfc)
  {
    OccurrenceDetail = await ApService.GetOccurrenceDetailAsync(occurrenceId, rfc, includePassword: CanUseProviderCredentials);
    Attachments = (await ApService.GetAttachmentsAsync(occurrenceId, rfc)).ToList();
    LinkedTransactions = (await ApService.GetOccurrenceTransactionLinksAsync(occurrenceId, rfc)).ToList();
  }

  private async Task SelectRequestedOccurrenceAsync(int occurrenceId)
  {
    var requestedOccurrence = Workspace.Occurrences.FirstOrDefault(item => item.Id == occurrenceId);
    if (requestedOccurrence is null)
    {
      var previousFilter = Filter;
      Filter = new RecurrentApFilter
      {
        Rfc = CurrentRfc,
        OccurrenceId = occurrenceId,
        DueSoonDays = Filter.DueSoonDays,
        Take = 1
      };

      await LoadWorkspaceAsync();
      requestedOccurrence = Workspace.Occurrences.FirstOrDefault(item => item.Id == occurrenceId);
      if (requestedOccurrence is null)
      {
        Filter = previousFilter;
        await LoadWorkspaceAsync();
      }
    }

    _handledRequestedOccurrenceId = occurrenceId;

    if (requestedOccurrence is null)
    {
      UiMessages.ShowWarning("El vencimiento AP seleccionado ya no existe o pertenece a otro RFC.");
      return;
    }

    await SelectOccurrenceAsync(requestedOccurrence);
  }

  private async Task LoadWorkspaceAsync()
  {
    IsLoading = true;
    try
    {
      Filter.Rfc = CurrentRfc;
      Workspace = await ApService.GetWorkspaceAsync(Filter);
      if (string.IsNullOrWhiteSpace(Editor.Rfc))
      {
        Editor = CreateEditor(CurrentRfc);
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError(ex.Message);
    }
    finally
    {
      IsLoading = false;
    }
  }

  private void ResetFilters()
  {
    Filter = new RecurrentApFilter
    {
      Rfc = CurrentRfc,
      FromDate = DateTime.Today.AddMonths(-1),
      ToDate = DateTime.Today.AddMonths(3),
      DueSoonDays = 7
    };
  }

  private async Task<string> ResolveCurrentUserAsync()
  {
    var auth = await AuthenticationStateProvider.GetAuthenticationStateAsync();
    return auth.User.Identity?.Name?.Trim() switch
    {
      { Length: > 0 } name => name,
      _ => "OrionERP"
    };
  }

  private async Task ResolvePermissionsAsync()
  {
    var auth = await AuthenticationStateProvider.GetAuthenticationStateAsync();
    var user = auth.User;
    IsReadOnly = user.IsInRole("APReadOnly")
      && !user.IsInRole("Administrador")
      && !user.IsInRole("APAdmin")
      && !user.IsInRole("APOperator");
  }

  private static RecurrentApUpsertRequest CreateEditor(string? rfc = null)
    => new()
    {
      Rfc = rfc ?? string.Empty,
      Name = string.Empty,
      FrequencyUnit = RecurrentApFrequencyUnits.Months,
      IntervalCount = 1,
      StartDate = DateTime.Today,
      DueDayOfMonth = DateTime.Today.Day,
      ExpectedAmount = null,
      Currency = "MXN",
      IsActive = true
    };

  private async Task<bool> ConfirmAsync(string message)
    => await Js.InvokeAsync<bool>("confirm", message);

  protected static string FormatCurrency(decimal value)
    => value.ToString("C2", CultureInfo.GetCultureInfo("es-MX"));

  protected static string FormatOptionalCurrency(decimal? value)
    => value.HasValue ? FormatCurrency(value.Value) : "-";

  protected static string GetStatusLabel(string? status)
    => status switch
    {
      RecurrentApStatuses.Pending => "Pendiente",
      RecurrentApStatuses.PartiallyPaid => "Parcial",
      RecurrentApStatuses.Paid => "Pagado",
      RecurrentApStatuses.Skipped => "Omitido",
      RecurrentApStatuses.Cancelled => "Cancelado",
      _ => status ?? "-"
    };

  protected static string GetFrequencyLabel(string? unit)
    => unit switch
    {
      RecurrentApFrequencyUnits.Days => "Días",
      RecurrentApFrequencyUnits.Weeks => "Semanas",
      RecurrentApFrequencyUnits.Months => "Meses",
      RecurrentApFrequencyUnits.Years => "Años",
      _ => unit ?? "-"
    };

  protected static string GetStatusBadgeClass(string? status)
    => status switch
    {
      RecurrentApStatuses.Paid => "badge text-bg-success",
      RecurrentApStatuses.PartiallyPaid => "badge text-bg-warning",
      RecurrentApStatuses.Skipped => "badge text-bg-secondary",
      RecurrentApStatuses.Cancelled => "badge text-bg-dark",
      _ => "badge text-bg-light border"
    };

  protected static string GetOccurrenceRowClass(RecurrentApOccurrenceListItemDto occurrence)
  {
    if (occurrence.IsOverdue)
    {
      return "table-danger";
    }

    if (occurrence.IsDueSoon)
    {
      return "table-warning";
    }

    return string.Empty;
  }

}
