using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.JSInterop;
using OrionERP.Application.Features.OrdenesTrabajo;
using OrionERP.Infrastructure.Auth;
using OrionERP.Web.Services;

namespace OrionERP.Web.Features.OrdenesTrabajo;

public partial class OrdenTrabajoDetailPage : ComponentBase, IAsyncDisposable
{
  private const long MaxImageBytes = 12 * 1024 * 1024;
  private const long MaxEvidenceFileBytes = 25 * 1024 * 1024;
  private const int ImageMaxPixels = 1600;
  private const int ThumbnailMaxPixels = 320;

  [Parameter] public int Id { get; set; }
  [SupplyParameterFromQuery(Name = "from")] public string? ReturnState { get; set; }

  [Inject] private IOrdenTrabajoService OrdenTrabajoService { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;
  [Inject] private NavigationManager Navigation { get; set; } = default!;
  [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
  [Inject] private IJSRuntime JSRuntime { get; set; } = default!;

  protected CultureInfo CurrencyCulture { get; } = CultureInfo.GetCultureInfo("es-MX");
  protected OrdenTrabajoDetailDto? Order { get; set; }
  protected List<OrdenTrabajoLookupDto> Employees { get; set; } = [];
  protected OrdenTrabajoUpdateRequest EditRequest { get; set; } = new();
  protected List<OrdenTrabajoStepSaveRequest> StepEditor { get; set; } = [];
  protected HashSet<int> EditHelperIds { get; set; } = [];
  protected Dictionary<int, string?> StepNotes { get; set; } = [];
  protected Dictionary<int, string> PendingStepStatuses { get; set; } = [];
  protected HashSet<int> ExpandedNoteStepIds { get; set; } = [];
  protected HashSet<int> BusyStepIds { get; set; } = [];
  protected List<OrdenTrabajoTransactionSearchItemDto> TransactionMatches { get; set; } = [];
  protected string TransactionSearchText { get; set; } = string.Empty;
  protected string ReviewReason { get; set; } = string.Empty;
  protected string CancelReason { get; set; } = string.Empty;
  protected string ActionDialogKind { get; set; } = string.Empty;
  protected string ActionDialogReason { get; set; } = string.Empty;
  protected string CurrentUserName { get; set; } = "OrionERP";
  protected int? CurrentEmployeeId { get; set; }
  protected bool IsPrivilegedUser { get; set; }
  protected bool IsLoading { get; set; }
  protected bool IsMutating { get; set; }
  protected bool IsCameraStarting { get; set; }
  protected bool IsCameraCapturing { get; set; }
  protected bool CameraSupported { get; set; } = true;
  protected string? ErrorMessage { get; set; }
  protected string? CameraError { get; set; }
  protected string? CameraPreviewUrl { get; set; }
  protected CameraCaptureResult? PendingCameraCapture { get; set; }
  protected bool IsEvidencePreviewOpen { get; set; }
  protected bool IsEvidencePreviewLoading { get; set; }
  protected bool IsEditingSteps { get; set; }
  protected string? EvidencePreviewImageDataUrl { get; set; }
  protected string? EvidencePreviewError { get; set; }
  protected string EvidencePreviewTitle { get; set; } = "Foto capturada";
  protected string? EvidencePreviewSubtitle { get; set; }
  protected int? ActiveCameraStepId { get; set; }
  protected List<CameraDeviceOption> CameraDevices { get; set; } = [];
  protected string? SelectedCameraDeviceId { get; set; }
  protected ElementReference CameraVideoElement { get; set; }
  protected ElementReference ActionReasonElement { get; set; }
  protected ElementReference ActionDialogCancelElement { get; set; }
  protected int? LastSavedStepId { get; set; }

  protected bool CanReview => IsPrivilegedUser;
  protected bool IsInReview => Order?.Estado == OrdenTrabajoCodes.EstadoEnRevision;
  protected bool CanEdit => Order is not null && IsPrivilegedUser && IsEditableStatus(Order.Estado);
  protected bool CanEditSteps => CanEdit && Order?.HasBeenSubmittedForReview == false;
  protected bool CanExecute => Order is not null
    && IsExecutableStatus(Order.Estado)
    && IsCurrentUserAssigned();
  protected bool CanRemoveEvidence => CanExecute && Order?.HasBeenSubmittedForReview == false;
  protected bool IsActionDialogOpen => !string.IsNullOrWhiteSpace(ActionDialogKind);
  protected bool CanStart => CanExecute && Order?.Estado != OrdenTrabajoCodes.EstadoEnProceso;
  protected OrdenTrabajoReviewReadiness ReviewReadiness
    => OrdenTrabajoReviewReadinessCalculator.Calculate(Order?.Steps);
  protected int ProgressPercent => Order?.StepCount > 0
    ? (int)Math.Clamp(Math.Round(Order.CompletedStepCount * 100d / Order.StepCount), 0, 100)
    : 0;
  protected string ReturnHref
  {
    get
    {
      var relative = ReturnState?.Trim().TrimStart('/');
      return !string.IsNullOrWhiteSpace(relative)
        && relative.StartsWith("ordenes-trabajo", StringComparison.OrdinalIgnoreCase)
        && !relative.StartsWith($"ordenes-trabajo/{Id}", StringComparison.OrdinalIgnoreCase)
          ? $"/{relative}"
          : "/ordenes-trabajo";
    }
  }

  protected string? ExecutionBlockedReason
  {
    get
    {
      if (Order is null || CanExecute)
      {
        return null;
      }

      if (!IsExecutableStatus(Order.Estado))
      {
        return $"La orden está en estado {OrdenesTrabajoPage.GetStatusLabel(Order.Estado)} y ya no permite cambios.";
      }

      if (!CurrentEmployeeId.HasValue)
      {
        return "Tu usuario no está vinculado con un empleado. Pide a un supervisor que revise tu acceso y vuelve a iniciar sesión.";
      }

      return $"Sólo {Order.OwnerName} y sus ayudantes pueden trabajar en esta orden.";
    }
  }

  protected string? ReviewBlockedReason => Order is null || IsInReview
    ? null
    : "Solo puedes aprobar y cerrar una orden que este en revision.";

  private int? ActorEmployeeIdForExecution => CurrentEmployeeId;
  private IJSObjectReference? CameraModule { get; set; }
  private bool PendingCameraStart { get; set; }
  private bool PendingDraftRestore { get; set; }
  private bool PendingActionDialogFocus { get; set; }

  protected override async Task OnInitializedAsync()
  {
    await ResolveCurrentUserAsync();
    await LoadAsync();
  }

  protected async Task LoadAsync()
  {
    await CloseCameraAsync(showState: false);
    IsLoading = true;
    ErrorMessage = null;
    try
    {
      Order = await OrdenTrabajoService.GetWorkOrderDetailAsync(Id);
      Employees = (await OrdenTrabajoService.GetActiveEmployeeOptionsAsync(Order?.Rfc)).ToList();
      BuildEditorFromOrder();
      PendingDraftRestore = true;
    }
    catch (Exception ex)
    {
      ErrorMessage = ex.Message;
    }
    finally
    {
      IsLoading = false;
    }
  }

  protected async Task SaveAsync()
  {
    if (!CanEdit || Order is null)
    {
      return;
    }

    IsMutating = true;
    try
    {
      EditRequest.HelperEmployeeIds = EditHelperIds.ToList();
      EditRequest.UpdatedBy = CurrentUserName;
      var result = await OrdenTrabajoService.UpdateWorkOrderAsync(Order.Id, EditRequest);
      await HandleMutationResultAsync(result);
    }
    finally
    {
      IsMutating = false;
    }
  }

  protected void AddStep()
  {
    if (!CanEditSteps || !IsEditingSteps)
    {
      return;
    }

    StepEditor.Add(CreateEmptyStep(StepEditor.Count + 1));
  }

  protected void RemoveStep(OrdenTrabajoStepSaveRequest step)
  {
    if (!CanEditSteps || !IsEditingSteps)
    {
      return;
    }

    StepEditor.Remove(step);
    ResequenceStepEditor();
  }

  protected void StartStepEdit()
  {
    if (!CanEditSteps)
    {
      return;
    }

    BuildStepEditorFromOrder();
    IsEditingSteps = true;
  }

  protected void CancelStepEdit()
  {
    BuildStepEditorFromOrder();
    IsEditingSteps = false;
  }

  protected async Task SaveStepsAsync()
  {
    if (!CanEditSteps || !IsEditingSteps || Order is null)
    {
      return;
    }

    IsMutating = true;
    try
    {
      ResequenceStepEditor();
      var result = await OrdenTrabajoService.ReplaceWorkOrderStepsAsync(
        Order.Id,
        new OrdenTrabajoStepsSaveRequest
        {
          Steps = StepEditor,
          SavedBy = CurrentUserName
        });
      if (result.Success)
      {
        IsEditingSteps = false;
      }

      await HandleMutationResultAsync(result);
    }
    finally
    {
      IsMutating = false;
    }
  }

  protected async Task StartAsync()
  {
    if (!CanExecute || Order is null)
    {
      return;
    }

    await MutateAsync(() => OrdenTrabajoService.StartWorkOrderAsync(Order.Id, CurrentUserName, ActorEmployeeIdForExecution));
  }

  protected async Task SubmitAsync()
  {
    if (!CanExecute || Order is null)
    {
      return;
    }

    await MutateAsync(() => OrdenTrabajoService.SubmitForReviewAsync(Order.Id, CurrentUserName, ActorEmployeeIdForExecution));
  }

  protected async Task ApproveAsync()
  {
    if (!CanReview || Order is null)
    {
      return;
    }

    await MutateAsync(() => OrdenTrabajoService.ApproveAsync(Order.Id, CurrentUserName));
  }

  protected async Task RejectAsync()
  {
    if (!CanReview || Order is null)
    {
      return;
    }

    await MutateAsync(() => OrdenTrabajoService.RejectAsync(Order.Id, ReviewReason, CurrentUserName));
    ReviewReason = string.Empty;
  }

  protected async Task CancelAsync()
  {
    if (!CanEdit || Order is null)
    {
      return;
    }

    await MutateAsync(() => OrdenTrabajoService.CancelWorkOrderAsync(Order.Id, CancelReason, CurrentUserName));
    CancelReason = string.Empty;
  }

  protected async Task UpdateStepAsync(OrdenTrabajoStepDto step, string status)
  {
    if (!CanExecute || Order is null)
    {
      return;
    }

    BusyStepIds.Add(step.Id);
    LastSavedStepId = null;
    try
    {
      var notes = StepNotes.TryGetValue(step.Id, out var draftNotes) ? draftNotes : step.Notas;
      var result = await OrdenTrabajoService.UpdateStepAsync(
        Order.Id,
        step.Id,
        new OrdenTrabajoStepUpdateRequest
        {
          Estado = status,
          Notas = notes,
          UpdatedBy = CurrentUserName,
          ActorEmployeeId = ActorEmployeeIdForExecution
        });
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        await LoadAsync();
        return;
      }

      step.Estado = status;
      step.Notas = NullIfWhiteSpace(notes);
      step.CompletadoEn = status == OrdenTrabajoCodes.PasoPendiente ? null : DateTime.UtcNow;
      step.CompletadoPor = status == OrdenTrabajoCodes.PasoPendiente ? null : CurrentUserName;
      Order.Estado = Order.Estado is OrdenTrabajoCodes.EstadoBorrador or OrdenTrabajoCodes.EstadoAsignada or OrdenTrabajoCodes.EstadoRechazada
        ? OrdenTrabajoCodes.EstadoEnProceso
        : Order.Estado;
      RefreshOrderProgress();
      PendingStepStatuses.Remove(step.Id);
      LastSavedStepId = step.Id;
      await ClearStepDraftAsync(step.Id);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo actualizar el paso. {ex.Message}");
    }
    finally
    {
      BusyStepIds.Remove(step.Id);
    }
  }

  protected async Task RequestStepStatusAsync(OrdenTrabajoStepDto step, string status)
  {
    var notes = GetStepNotes(step);
    var requiresNote = (status == OrdenTrabajoCodes.PasoIncidencia && step.RequiereNotasEnIncidencia)
      || (status == OrdenTrabajoCodes.PasoNoAplica && step.RequiereNotasEnNoAplica);
    if (requiresNote && string.IsNullOrWhiteSpace(notes))
    {
      PendingStepStatuses[step.Id] = status;
      ExpandedNoteStepIds.Add(step.Id);
      return;
    }

    await UpdateStepAsync(step, status);
  }

  protected async Task ConfirmPendingStepAsync(OrdenTrabajoStepDto step)
  {
    if (!PendingStepStatuses.TryGetValue(step.Id, out var status))
    {
      return;
    }

    if (string.IsNullOrWhiteSpace(GetStepNotes(step)))
    {
      UiMessages.ShowWarning("Escribe una nota para continuar.");
      return;
    }

    await UpdateStepAsync(step, status);
  }

  protected async Task OnEvidenceSelectedAsync(OrdenTrabajoStepDto step, InputFileChangeEventArgs args)
  {
    if (!CanExecute || Order is null)
    {
      return;
    }

    var file = args.File;
    if (file is null)
    {
      return;
    }

    BusyStepIds.Add(step.Id);
    try
    {
      if (file.Size > MaxEvidenceFileBytes)
      {
        UiMessages.ShowWarning($"El archivo supera el limite de {FormatFileSize(MaxEvidenceFileBytes)}.");
        return;
      }

      var isImage = IsImageFile(file.Name, file.ContentType);
      var content = isImage
        ? await BuildImageBytesAsync(file, ImageMaxPixels)
        : await BuildFileBytesAsync(file, MaxEvidenceFileBytes);
      var thumbnailBytes = Array.Empty<byte>();
      string? thumbnailContentType = null;
      if (isImage)
      {
        var thumb = await BuildImageBytesAsync(file, ThumbnailMaxPixels);
        thumbnailBytes = thumb.Bytes;
        thumbnailContentType = thumb.ContentType;
      }

      var result = await OrdenTrabajoService.AddStepEvidenceAsync(
        Order.Id,
        step.Id,
        new OrdenTrabajoEvidenceCreateRequest
        {
          ImageBytes = content.Bytes,
          ThumbnailBytes = thumbnailBytes.Length == 0 ? null : thumbnailBytes,
          FileName = file.Name,
          ContentType = content.ContentType,
          ThumbnailContentType = thumbnailContentType,
          DeviceInfo = "Blazor InputFile upload",
          CaptureSource = OrdenTrabajoCodes.EvidenciaFile,
          CapturedBy = CurrentUserName,
          ActorEmployeeId = ActorEmployeeIdForExecution
        });
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      if (result.EntityId.HasValue)
      {
        AddLocalEvidence(
          step,
          result.EntityId.Value,
          file.Name,
          content.ContentType,
          thumbnailBytes,
          thumbnailContentType,
          file.Size,
          OrdenTrabajoCodes.EvidenciaFile);
      }
      else
      {
        await LoadAsync();
      }

      UiMessages.ShowSuccess("Evidencia guardada.");
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo guardar la evidencia. {ex.Message}");
    }
    finally
    {
      BusyStepIds.Remove(step.Id);
    }
  }

  protected async Task OpenCameraAsync(OrdenTrabajoStepDto step)
  {
    if (!CanExecute || Order is null)
    {
      return;
    }

    CameraError = null;
    ActiveCameraStepId = step.Id;
    IsCameraStarting = true;
    CameraSupported = true;

    try
    {
      var module = await EnsureCameraModuleAsync();
      CameraSupported = await module.InvokeAsync<bool>("isSupported");
      if (!CameraSupported)
      {
        CameraError = "Este teléfono no permite abrir la cámara aquí. Cierra esta pantalla y usa “Subir archivo”.";
        IsCameraStarting = false;
        return;
      }

      PendingCameraStart = true;
      await InvokeAsync(StateHasChanged);
    }
    catch (Exception)
    {
      CameraError = "No pudimos preparar la cámara. Revisa el permiso del navegador o cierra esta pantalla y usa “Subir archivo”.";
      IsCameraStarting = false;
    }
  }

  protected async Task CloseCameraAsync()
    => await CloseCameraAsync(showState: true);

  protected async Task ChangeCameraAsync(ChangeEventArgs args)
  {
    SelectedCameraDeviceId = args.Value?.ToString();
    if (ActiveCameraStepId.HasValue)
    {
      await StartCameraStreamAsync();
    }
  }

  protected async Task CaptureCameraAsync(OrdenTrabajoStepDto step)
  {
    if (!CanExecute || Order is null || ActiveCameraStepId != step.Id)
    {
      return;
    }

    IsCameraCapturing = true;
    CameraError = null;
    try
    {
      var module = await EnsureCameraModuleAsync();
      var capture = await module.InvokeAsync<CameraCaptureResult>(
        "capture",
        CameraVideoElement,
        ImageMaxPixels,
        ThumbnailMaxPixels);
      PendingCameraCapture = capture;
      CameraPreviewUrl = await module.InvokeAsync<string>("getPreviewUrl");
    }
    catch (Exception)
    {
      CameraError = "No se pudo capturar la foto. Mantén la cámara abierta y toca Capturar nuevamente.";
    }
    finally
    {
      IsCameraCapturing = false;
    }
  }

  protected async Task RetakeCameraAsync()
  {
    CameraPreviewUrl = null;
    PendingCameraCapture = null;
    try
    {
      var module = await EnsureCameraModuleAsync();
      await module.InvokeVoidAsync("clearLastCapture");
    }
    catch (Exception ex)
    {
      CameraError = $"No se pudo preparar otra foto. {ex.Message}";
    }
  }

  protected async Task SaveCameraAsync(OrdenTrabajoStepDto step)
  {
    if (!CanExecute || Order is null || ActiveCameraStepId != step.Id || PendingCameraCapture is null)
    {
      return;
    }

    IsCameraCapturing = true;
    CameraError = null;
    BusyStepIds.Add(step.Id);
    try
    {
      var module = await EnsureCameraModuleAsync();
      var imageBytes = await ReadCameraBlobAsync(module, "getLastImage", MaxImageBytes);
      var thumbnailBytes = await ReadCameraBlobAsync(module, "getLastThumbnail", MaxImageBytes);
      var fileName = $"camara-{Order.Folio}-{step.Id}-{DateTime.Now:yyyyMMddHHmmss}.jpg";
      var result = await OrdenTrabajoService.AddStepEvidenceAsync(
        Order.Id,
        step.Id,
        new OrdenTrabajoEvidenceCreateRequest
        {
          ImageBytes = imageBytes,
          ThumbnailBytes = thumbnailBytes,
          FileName = fileName,
          ContentType = PendingCameraCapture.ImageContentType,
          ThumbnailContentType = PendingCameraCapture.ThumbnailContentType,
          DeviceInfo = BuildCameraDeviceInfo(PendingCameraCapture),
          CaptureSource = OrdenTrabajoCodes.EvidenciaCamera,
          CapturedBy = CurrentUserName,
          ActorEmployeeId = ActorEmployeeIdForExecution
        });
      if (!result.Success)
      {
        CameraError = result.Message;
        return;
      }

      if (result.EntityId.HasValue)
      {
        AddLocalEvidence(
          step,
          result.EntityId.Value,
          fileName,
          PendingCameraCapture.ImageContentType,
          thumbnailBytes,
          PendingCameraCapture.ThumbnailContentType,
          imageBytes.LongLength,
          OrdenTrabajoCodes.EvidenciaCamera);
      }
      else
      {
        await LoadAsync();
      }

      await module.InvokeVoidAsync("clearLastCapture");
      await CloseCameraAsync(showState: false);
      UiMessages.ShowSuccess("Foto guardada.");
    }
    catch (Exception)
    {
      CameraError = "No se pudo guardar la foto. Conservamos la vista previa: revisa tu conexión y vuelve a tocar “Usar esta foto”.";
    }
    finally
    {
      BusyStepIds.Remove(step.Id);
      IsCameraCapturing = false;
    }
  }

  protected async Task RemoveEvidenceAsync(OrdenTrabajoStepDto step, int evidenceId)
  {
    if (!CanExecute || Order is null)
    {
      return;
    }

    BusyStepIds.Add(step.Id);
    try
    {
      var result = await OrdenTrabajoService.RemoveStepEvidenceAsync(Order.Id, step.Id, evidenceId, CurrentUserName, ActorEmployeeIdForExecution);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      step.Evidence = step.Evidence.Where(evidence => evidence.Id != evidenceId).ToList();
      step.ActiveEvidenceCount = step.Evidence.Count(evidence => !evidence.Eliminada);
      UiMessages.ShowSuccess("Evidencia eliminada.");
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo quitar la evidencia. {ex.Message}");
    }
    finally
    {
      BusyStepIds.Remove(step.Id);
    }
  }

  protected async Task OpenEvidenceAsync(OrdenTrabajoEvidenceDto evidence)
  {
    if (IsImageEvidence(evidence))
    {
      await OpenEvidencePreviewAsync(evidence);
      return;
    }

    await DownloadEvidenceAsync(evidence);
  }

  protected async Task OpenEvidencePreviewAsync(OrdenTrabajoEvidenceDto evidence)
  {
    IsEvidencePreviewOpen = true;
    IsEvidencePreviewLoading = true;
    EvidencePreviewImageDataUrl = null;
    EvidencePreviewError = null;
    EvidencePreviewTitle = $"Evidencia {evidence.Id}";
    EvidencePreviewSubtitle = $"{evidence.CapturadaPor} · {evidence.CapturadaEn:yyyy-MM-dd HH:mm}";

    try
    {
      var content = await OrdenTrabajoService.GetEvidenceContentAsync(evidence.Id);
      if (content?.Bytes is not { Length: > 0 })
      {
        EvidencePreviewError = "No se encontro la evidencia completa.";
        return;
      }

      EvidencePreviewTitle = string.IsNullOrWhiteSpace(content.FileName)
        ? EvidencePreviewTitle
        : content.FileName;
      EvidencePreviewImageDataUrl = BuildImageDataUrl(content.ContentType, content.Bytes);
    }
    catch (Exception ex)
    {
      EvidencePreviewError = $"No se pudo cargar la evidencia. {ex.Message}";
    }
    finally
    {
      IsEvidencePreviewLoading = false;
    }
  }

  protected async Task DownloadEvidenceAsync(OrdenTrabajoEvidenceDto evidence)
  {
    try
    {
      var content = await OrdenTrabajoService.GetEvidenceContentAsync(evidence.Id);
      if (content?.Bytes is not { Length: > 0 })
      {
        UiMessages.ShowWarning("No se encontro el archivo solicitado.");
        return;
      }

      var fileName = string.IsNullOrWhiteSpace(content.FileName)
        ? GetEvidenceDisplayName(evidence)
        : content.FileName;
      var contentType = string.IsNullOrWhiteSpace(content.ContentType)
        ? "application/octet-stream"
        : content.ContentType;
      var dataUrl = $"data:{contentType};base64,{Convert.ToBase64String(content.Bytes)}";
      await JSRuntime.InvokeVoidAsync("triggerFileDownload", fileName, dataUrl);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo descargar la evidencia. {ex.Message}");
    }
  }

  protected void CloseEvidencePreview()
  {
    IsEvidencePreviewOpen = false;
    IsEvidencePreviewLoading = false;
    EvidencePreviewImageDataUrl = null;
    EvidencePreviewError = null;
  }

  protected void HandleEvidencePreviewKeyDown(KeyboardEventArgs args)
  {
    if (args.Key == "Escape")
    {
      CloseEvidencePreview();
    }
  }

  protected async Task SearchTransactionsAsync()
  {
    if (Order is null)
    {
      return;
    }

    TransactionMatches = (await OrdenTrabajoService.SearchTransactionsAsync(Order.Id, TransactionSearchText)).ToList();
  }

  protected Task OnTransactionSearchKeyUpAsync(KeyboardEventArgs args)
    => args.Key == "Enter" ? SearchTransactionsAsync() : Task.CompletedTask;

  protected async Task LinkTransactionAsync(int transactionId)
  {
    if (Order is null)
    {
      return;
    }

    await MutateAsync(() => OrdenTrabajoService.LinkTransactionAsync(Order.Id, transactionId, CurrentUserName));
    TransactionMatches.Clear();
  }

  protected async Task UnlinkTransactionAsync(int transactionId)
  {
    if (Order is null)
    {
      return;
    }

    await MutateAsync(() => OrdenTrabajoService.UnlinkTransactionAsync(Order.Id, transactionId, CurrentUserName));
  }

  protected void ToggleEditHelper(int employeeId, ChangeEventArgs args)
  {
    if (args.Value is bool selected && selected)
    {
      EditHelperIds.Add(employeeId);
      return;
    }

    if (bool.TryParse(args.Value?.ToString(), out var parsed) && parsed)
    {
      EditHelperIds.Add(employeeId);
      return;
    }

    EditHelperIds.Remove(employeeId);
  }

  protected static string GetStepLabel(string status)
    => status switch
    {
      "HECHO" => "Hecho",
      "INCIDENCIA" => "Incidencia",
      "NO_APLICA" => "N/A",
      _ => "Pendiente"
    };

  protected static string GetDetailStatusClass(string status)
    => status switch
    {
      OrdenTrabajoCodes.EstadoEnProceso => "is-progress",
      OrdenTrabajoCodes.EstadoEnRevision => "is-review",
      OrdenTrabajoCodes.EstadoRechazada => "is-rejected",
      OrdenTrabajoCodes.EstadoCerrada => "is-closed",
      OrdenTrabajoCodes.EstadoCancelada => "is-cancelled",
      _ => "is-assigned"
    };

  protected static string GetDetailStatusIcon(string status)
    => status switch
    {
      OrdenTrabajoCodes.EstadoEnProceso => "oi-media-play",
      OrdenTrabajoCodes.EstadoEnRevision => "oi-eye",
      OrdenTrabajoCodes.EstadoRechazada => "oi-action-undo",
      OrdenTrabajoCodes.EstadoCerrada => "oi-check",
      OrdenTrabajoCodes.EstadoCancelada => "oi-ban",
      _ => "oi-task"
    };

  protected static string GetStepStatusClass(string status)
    => status switch
    {
      OrdenTrabajoCodes.PasoHecho => "is-done",
      OrdenTrabajoCodes.PasoIncidencia => "is-issue",
      OrdenTrabajoCodes.PasoNoAplica => "is-na",
      _ => "is-pending"
    };

  protected static string GetStepBadgeClass(string status)
    => status switch
    {
      "HECHO" => "badge text-bg-success",
      "INCIDENCIA" => "badge text-bg-warning",
      "NO_APLICA" => "badge text-bg-secondary",
      _ => "badge text-bg-light"
    };

  protected static string GetStepCardClass(OrdenTrabajoStepDto step)
    => $"orden-step orden-step-{step.Estado.ToLowerInvariant().Replace('_', '-')}";

  protected static string GetPhotoPolicyLabel(string policy)
    => policy switch
    {
      "REQUERIDA" => "requerida",
      "OPCIONAL" => "opcional",
      _ => "no permitida"
    };

  protected static string GetCaptureSourceLabel(string? source)
    => source switch
    {
      OrdenTrabajoCodes.EvidenciaCamera => "Camara",
      OrdenTrabajoCodes.EvidenciaFile => "Archivo",
      _ => "Origen desconocido"
    };

  protected static string GetCaptureSourceBadgeClass(string? source)
    => source switch
    {
      OrdenTrabajoCodes.EvidenciaCamera => "badge text-bg-primary mt-1",
      OrdenTrabajoCodes.EvidenciaFile => "badge text-bg-secondary mt-1",
      _ => "badge text-bg-light mt-1"
    };

  protected static string BuildThumbnailDataUrl(OrdenTrabajoEvidenceDto evidence)
  {
    var contentType = string.IsNullOrWhiteSpace(evidence.ThumbnailContentType)
      ? evidence.ContentType
      : evidence.ThumbnailContentType;
    var bytes = evidence.ThumbnailBytes ?? Array.Empty<byte>();
    return BuildImageDataUrl(contentType, bytes);
  }

  protected static string BuildImageDataUrl(string? contentType, byte[] bytes)
    => $"data:{(string.IsNullOrWhiteSpace(contentType) ? "image/jpeg" : contentType)};base64,{Convert.ToBase64String(bytes)}";

  protected static bool IsImageEvidence(OrdenTrabajoEvidenceDto evidence)
    => IsImageFile(evidence.FileName, evidence.ContentType)
      || IsImageFile(evidence.FileName, evidence.ThumbnailContentType);

  protected static string GetEvidenceDisplayName(OrdenTrabajoEvidenceDto evidence)
    => string.IsNullOrWhiteSpace(evidence.FileName)
      ? $"evidencia-{evidence.Id}"
      : evidence.FileName;

  protected static string GetEvidenceActionTitle(OrdenTrabajoEvidenceDto evidence)
    => IsImageEvidence(evidence) ? "Ver imagen completa" : "Descargar archivo";

  protected static string GetEvidenceIconClass(OrdenTrabajoEvidenceDto evidence)
    => GetFileExtension(evidence.FileName) switch
    {
      ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" => "oi oi-image",
      ".pdf" => "oi oi-document",
      ".doc" or ".docx" => "oi oi-document",
      ".xls" or ".xlsx" or ".csv" => "oi oi-spreadsheet",
      ".ppt" or ".pptx" => "oi oi-project",
      ".txt" => "oi oi-text",
      ".xml" or ".json" => "oi oi-code",
      ".zip" or ".rar" or ".7z" => "oi oi-box",
      _ => "oi oi-file"
    };

  protected static string GetEvidenceTypeLabel(OrdenTrabajoEvidenceDto evidence)
  {
    var extension = GetFileExtension(evidence.FileName);
    if (!string.IsNullOrWhiteSpace(extension))
    {
      return extension.TrimStart('.').ToUpperInvariant();
    }

    return IsImageEvidence(evidence) ? "IMG" : "FILE";
  }

  protected static string GetEvidencePlaceholderClass(OrdenTrabajoEvidenceDto evidence)
    => $"orden-evidence-placeholder {GetEvidenceColorClass(evidence)}";

  protected static string FormatFileSize(long bytes)
  {
    if (bytes <= 0)
    {
      return "0 KB";
    }

    string[] units = ["B", "KB", "MB", "GB"];
    var size = (double)bytes;
    var unitIndex = 0;
    while (size >= 1024 && unitIndex < units.Length - 1)
    {
      size /= 1024;
      unitIndex++;
    }

    return unitIndex == 0
      ? $"{size:0} {units[unitIndex]}"
      : $"{size:0.#} {units[unitIndex]}";
  }

  protected string? GetStepNotes(OrdenTrabajoStepDto step)
    => StepNotes.TryGetValue(step.Id, out var notes) ? notes : step.Notas;

  protected void SetStepNotes(int stepId, string? notes)
    => StepNotes[stepId] = notes;

  protected void ToggleStepNotes(int stepId)
  {
    if (!ExpandedNoteStepIds.Add(stepId))
    {
      ExpandedNoteStepIds.Remove(stepId);
    }
  }

  protected bool IsStepNotesVisible(OrdenTrabajoStepDto step)
    => ExpandedNoteStepIds.Contains(step.Id)
      || PendingStepStatuses.ContainsKey(step.Id)
      || !string.IsNullOrWhiteSpace(GetStepNotes(step));

  protected bool IsStepBusy(int stepId)
    => BusyStepIds.Contains(stepId);

  protected static bool IsExecutableStatusForDisplay(string status)
    => IsExecutableStatus(status);

  protected static bool ShouldShowStepDescription(OrdenTrabajoStepDto step)
    => !string.IsNullOrWhiteSpace(step.Descripcion)
      && !string.Equals(NormalizeComparisonText(step.Titulo), NormalizeComparisonText(step.Descripcion), StringComparison.OrdinalIgnoreCase);

  protected bool HasMissingRequiredNote(OrdenTrabajoStepDto step)
    => string.IsNullOrWhiteSpace(GetStepNotes(step))
      && ((step.Estado == OrdenTrabajoCodes.PasoIncidencia && step.RequiereNotasEnIncidencia)
        || (step.Estado == OrdenTrabajoCodes.PasoNoAplica && step.RequiereNotasEnNoAplica));

  protected string GetReadinessMessage()
  {
    var parts = new List<string>();
    if (ReviewReadiness.PendingStepCount > 0)
    {
      parts.Add($"{ReviewReadiness.PendingStepCount} {Pluralize(ReviewReadiness.PendingStepCount, "paso pendiente", "pasos pendientes")}");
    }
    if (ReviewReadiness.MissingRequiredPhotoCount > 0)
    {
      parts.Add($"{ReviewReadiness.MissingRequiredPhotoCount} {Pluralize(ReviewReadiness.MissingRequiredPhotoCount, "foto pendiente", "fotos pendientes")}");
    }
    if (ReviewReadiness.MissingRequiredNoteCount > 0)
    {
      parts.Add($"{ReviewReadiness.MissingRequiredNoteCount} {Pluralize(ReviewReadiness.MissingRequiredNoteCount, "nota pendiente", "notas pendientes")}");
    }

    return parts.Count == 0 ? "Lista para enviar" : string.Join(" · ", parts);
  }

  protected void OpenActionDialog(string kind)
  {
    ActionDialogKind = kind;
    ActionDialogReason = kind switch
    {
      "reject" => ReviewReason,
      "cancel" => CancelReason,
      _ => string.Empty
    };
    PendingActionDialogFocus = true;
  }

  protected void CloseActionDialog()
  {
    if (IsMutating)
    {
      return;
    }

    ActionDialogKind = string.Empty;
    ActionDialogReason = string.Empty;
  }

  protected string ActionDialogTitle => ActionDialogKind switch
  {
    "reject" => "Devolver para corrección",
    "cancel" => "Cancelar orden",
    "delete" => "Eliminar orden",
    _ => "Confirmar acción"
  };

  protected string ActionDialogMessage => ActionDialogKind switch
  {
    "reject" => "Explica con palabras claras qué debe corregir el equipo.",
    "cancel" => "La orden dejará de aparecer como trabajo activo.",
    "delete" => $"Se eliminará {Order?.Folio}. Esta acción no se puede deshacer.",
    _ => string.Empty
  };

  protected string ActionDialogConfirmLabel => ActionDialogKind switch
  {
    "reject" => "Devolver orden",
    "cancel" => "Cancelar orden",
    "delete" => "Eliminar definitivamente",
    _ => "Confirmar"
  };

  protected bool ActionDialogRequiresReason => ActionDialogKind is "reject" or "cancel";

  protected void HandleActionDialogKeyDown(KeyboardEventArgs args)
  {
    if (args.Key == "Escape")
    {
      CloseActionDialog();
    }
  }

  protected async Task ConfirmActionDialogAsync()
  {
    if (Order is null || IsMutating)
    {
      return;
    }

    if (ActionDialogRequiresReason && string.IsNullOrWhiteSpace(ActionDialogReason))
    {
      UiMessages.ShowWarning("Escribe un motivo para continuar.");
      return;
    }

    IsMutating = true;
    try
    {
      var kind = ActionDialogKind;
      var result = kind switch
      {
        "reject" => await OrdenTrabajoService.RejectAsync(Order.Id, ActionDialogReason, CurrentUserName),
        "cancel" => await OrdenTrabajoService.CancelWorkOrderAsync(Order.Id, ActionDialogReason, CurrentUserName),
        "delete" => await OrdenTrabajoService.DeleteWorkOrderAsync(Order.Id, CurrentUserName),
        _ => OrdenTrabajoCommandResult.Fail("La acción seleccionada no es válida.")
      };

      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      ActionDialogKind = string.Empty;
      ActionDialogReason = string.Empty;
      if (kind == "delete")
      {
        Navigation.NavigateTo(ReturnHref);
        return;
      }

      await LoadAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError(ex.Message);
    }
    finally
    {
      IsMutating = false;
    }
  }

  protected override async Task OnAfterRenderAsync(bool firstRender)
  {
    if (PendingDraftRestore)
    {
      PendingDraftRestore = false;
      await RestoreStepDraftsAsync();
      await InvokeAsync(StateHasChanged);
    }

    if (PendingActionDialogFocus)
    {
      PendingActionDialogFocus = false;
      var target = ActionDialogRequiresReason ? ActionReasonElement : ActionDialogCancelElement;
      try
      {
        await JSRuntime.InvokeVoidAsync("focusAndSelectTextInput", target, false);
      }
      catch (JSException)
      {
      }
      catch (InvalidOperationException)
      {
      }
    }

    if (!PendingCameraStart)
    {
      return;
    }

    PendingCameraStart = false;
    await StartCameraStreamAsync();
  }

  private void BuildEditorFromOrder()
  {
    if (Order is null)
    {
      return;
    }

    EditRequest = new OrdenTrabajoUpdateRequest
    {
      Titulo = Order.Titulo,
      Descripcion = Order.Descripcion,
      OwnerEmployeeId = Order.OwnerEmployeeId,
      HelperEmployeeIds = Order.Helpers.Select(helper => helper.EmployeeId).ToList(),
      FechaProgramada = Order.FechaProgramada,
      HoraInicioProgramada = Order.HoraInicioProgramada,
      HoraFinProgramada = Order.HoraFinProgramada,
      FechaVencimiento = Order.FechaVencimiento,
      Prioridad = Order.Prioridad,
      Ubicacion = Order.Ubicacion,
      EstimatedCost = Order.EstimatedCost
    };
    EditHelperIds = Order.Helpers.Select(helper => helper.EmployeeId).ToHashSet();
    StepNotes = Order.Steps.ToDictionary(step => step.Id, step => step.Notas);
    BuildStepEditorFromOrder();
    IsEditingSteps = false;
  }

  private void BuildStepEditorFromOrder()
  {
    if (Order is null)
    {
      StepEditor = [];
      return;
    }

    StepEditor = Order.Steps.Count == 0
      ? [CreateEmptyStep(1)]
      : Order.Steps
        .OrderBy(step => step.Secuencia)
        .Select(step => new OrdenTrabajoStepSaveRequest
        {
          Secuencia = step.Secuencia,
          Titulo = step.Titulo,
          Descripcion = step.Descripcion,
          PoliticaFoto = step.PoliticaFoto,
          RequiereNotasEnIncidencia = step.RequiereNotasEnIncidencia,
          RequiereNotasEnNoAplica = step.RequiereNotasEnNoAplica,
          ProcedimientoId = step.ProcedimientoId
        })
        .ToList();
  }

  private void ResequenceStepEditor()
  {
    for (var index = 0; index < StepEditor.Count; index++)
    {
      StepEditor[index].Secuencia = index + 1;
    }
  }

  private static OrdenTrabajoStepSaveRequest CreateEmptyStep(int sequence)
    => new()
    {
      Secuencia = sequence,
      PoliticaFoto = OrdenTrabajoCodes.FotoOpcional,
      RequiereNotasEnIncidencia = true,
      RequiereNotasEnNoAplica = true
    };

  private bool IsCurrentUserAssigned()
  {
    if (Order is null || !CurrentEmployeeId.HasValue)
    {
      return false;
    }

    return OrdenTrabajoPermissions.CanExecute(
      CurrentEmployeeId,
      Order.OwnerEmployeeId,
      Order.Helpers.Select(helper => helper.EmployeeId));
  }

  private void RefreshOrderProgress()
  {
    if (Order is null)
    {
      return;
    }

    Order.CompletedStepCount = Order.Steps.Count(step => step.Estado != OrdenTrabajoCodes.PasoPendiente);
    Order.IssueStepCount = Order.Steps.Count(step => step.Estado == OrdenTrabajoCodes.PasoIncidencia);
  }

  private void AddLocalEvidence(
    OrdenTrabajoStepDto step,
    int evidenceId,
    string fileName,
    string contentType,
    byte[]? thumbnailBytes,
    string? thumbnailContentType,
    long sizeBytes,
    string captureSource)
  {
    var evidence = new OrdenTrabajoEvidenceDto
    {
      Id = evidenceId,
      PasoId = step.Id,
      FileName = fileName,
      ContentType = contentType,
      ThumbnailBytes = thumbnailBytes,
      ThumbnailContentType = thumbnailContentType,
      SizeBytes = sizeBytes,
      CaptureSource = captureSource,
      CapturadaEn = DateTime.UtcNow,
      CapturadaPor = CurrentUserName
    };
    step.Evidence = step.Evidence.Append(evidence).ToList();
    step.ActiveEvidenceCount = step.Evidence.Count(item => !item.Eliminada);
  }

  private async Task RestoreStepDraftsAsync()
  {
    if (Order is null)
    {
      return;
    }

    foreach (var step in Order.Steps)
    {
      try
      {
        var draft = await JSRuntime.InvokeAsync<string?>("sessionStorage.getItem", GetStepDraftKey(step.Id));
        if (draft is null || string.Equals(draft, step.Notas, StringComparison.Ordinal))
        {
          continue;
        }

        StepNotes[step.Id] = draft;
        ExpandedNoteStepIds.Add(step.Id);
      }
      catch (JSException)
      {
        return;
      }
      catch (InvalidOperationException)
      {
        return;
      }
    }
  }

  private async Task ClearStepDraftAsync(int stepId)
  {
    try
    {
      await JSRuntime.InvokeVoidAsync("sessionStorage.removeItem", GetStepDraftKey(stepId));
    }
    catch (JSException)
    {
    }
    catch (InvalidOperationException)
    {
    }
  }

  private string GetStepDraftKey(int stepId)
    => $"orionerp:ordenes-trabajo:{Id}:paso:{stepId}:nota";

  private static string NormalizeComparisonText(string? value)
    => string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

  private static string Pluralize(int count, string singular, string plural)
    => count == 1 ? singular : plural;

  private static bool IsEditableStatus(string status)
    => status is "BORRADOR" or "ASIGNADA" or "EN_PROCESO" or "RECHAZADA";

  private static bool IsExecutableStatus(string status)
    => status is "BORRADOR" or "ASIGNADA" or "EN_PROCESO" or "RECHAZADA";

  private async Task MutateAsync(Func<Task<OrdenTrabajoCommandResult>> operation)
  {
    IsMutating = true;
    try
    {
      var result = await operation();
      await HandleMutationResultAsync(result);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError(ex.Message);
    }
    finally
    {
      IsMutating = false;
    }
  }

  private async Task HandleMutationResultAsync(OrdenTrabajoCommandResult result)
  {
    if (!result.Success)
    {
      UiMessages.ShowError(result.Message);
      return;
    }

    UiMessages.ShowSuccess(result.Message);
    await LoadAsync();
  }

  private async Task<IJSObjectReference> EnsureCameraModuleAsync()
  {
    CameraModule ??= await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/orden-trabajo-camera.js");
    return CameraModule;
  }

  private static async Task<byte[]> ReadCameraBlobAsync(IJSObjectReference module, string functionName, long maxAllowedBytes)
  {
    await using var streamReference = await module.InvokeAsync<IJSStreamReference>(functionName);
    await using var stream = await streamReference.OpenReadStreamAsync(maxAllowedBytes);
    using var ms = new MemoryStream();
    await stream.CopyToAsync(ms);
    return ms.ToArray();
  }

  private async Task StartCameraStreamAsync()
  {
    IsCameraStarting = true;
    CameraError = null;
    try
    {
      var module = await EnsureCameraModuleAsync();
      var streamInfo = await module.InvokeAsync<CameraStreamInfo>("start", CameraVideoElement, NullIfWhiteSpace(SelectedCameraDeviceId));
      CameraDevices = (await module.InvokeAsync<CameraDeviceOption[]>("listCameras")).ToList();
      if (!string.IsNullOrWhiteSpace(streamInfo.DeviceId))
      {
        SelectedCameraDeviceId = streamInfo.DeviceId;
      }
      else if (string.IsNullOrWhiteSpace(SelectedCameraDeviceId) && CameraDevices.Count > 0)
      {
        SelectedCameraDeviceId = CameraDevices[0].DeviceId;
      }
    }
    catch (Exception)
    {
      CameraError = "No pudimos abrir la cámara. Permite su uso en el navegador o cierra esta pantalla y usa “Subir archivo”.";
    }
    finally
    {
      IsCameraStarting = false;
      await InvokeAsync(StateHasChanged);
    }
  }

  private async Task CloseCameraAsync(bool showState)
  {
    PendingCameraStart = false;
    ActiveCameraStepId = null;
    CameraError = null;
    IsCameraStarting = false;
    IsCameraCapturing = false;
    CameraPreviewUrl = null;
    PendingCameraCapture = null;

    if (CameraModule is not null)
    {
      try
      {
        await CameraModule.InvokeVoidAsync("stop");
        await CameraModule.InvokeVoidAsync("clearLastCapture");
      }
      catch (JSDisconnectedException)
      {
      }
      catch (InvalidOperationException)
      {
      }
    }

    if (showState)
    {
      await InvokeAsync(StateHasChanged);
    }
  }

  private static string BuildCameraDeviceInfo(CameraCaptureResult capture)
  {
    var parts = new List<string> { "getUserMedia camera capture" };
    if (!string.IsNullOrWhiteSpace(capture.DeviceLabel))
    {
      parts.Add($"device={capture.DeviceLabel}");
    }

    if (!string.IsNullOrWhiteSpace(capture.FacingMode))
    {
      parts.Add($"facingMode={capture.FacingMode}");
    }

    parts.Add($"image={capture.ImageWidth}x{capture.ImageHeight}");
    parts.Add($"thumbnail={capture.ThumbnailWidth}x{capture.ThumbnailHeight}");
    return string.Join("; ", parts);
  }

  private async Task<(byte[] Bytes, string ContentType)> BuildImageBytesAsync(IBrowserFile file, int maxPixels)
  {
    try
    {
      var converted = await file.RequestImageFileAsync("image/jpeg", maxPixels, maxPixels);
      await using var convertedStream = converted.OpenReadStream(MaxImageBytes);
      using var convertedMs = new MemoryStream();
      await convertedStream.CopyToAsync(convertedMs);
      return (convertedMs.ToArray(), converted.ContentType);
    }
    catch
    {
      await using var stream = file.OpenReadStream(MaxImageBytes);
      using var ms = new MemoryStream();
      await stream.CopyToAsync(ms);
      return (ms.ToArray(), string.IsNullOrWhiteSpace(file.ContentType) ? "image/jpeg" : file.ContentType);
    }
  }

  private static async Task<(byte[] Bytes, string ContentType)> BuildFileBytesAsync(IBrowserFile file, long maxAllowedBytes)
  {
    await using var stream = file.OpenReadStream(maxAllowedBytes);
    using var ms = new MemoryStream();
    await stream.CopyToAsync(ms);
    return (ms.ToArray(), ResolveBrowserFileContentType(file));
  }

  private static string ResolveBrowserFileContentType(IBrowserFile file)
  {
    var contentType = string.IsNullOrWhiteSpace(file.ContentType)
      ? null
      : file.ContentType;
    if (!string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
      && contentType is not null)
    {
      return contentType;
    }

    var extensionContentType = ResolveContentTypeFromExtension(GetFileExtension(file.Name));
    return string.Equals(extensionContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
      ? contentType ?? extensionContentType
      : extensionContentType;
  }

  private static bool IsImageFile(string? fileName, string? contentType)
    => (!string.IsNullOrWhiteSpace(contentType) && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
      || GetFileExtension(fileName) is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp";

  private static string GetEvidenceColorClass(OrdenTrabajoEvidenceDto evidence)
    => GetFileExtension(evidence.FileName) switch
    {
      ".pdf" => "orden-evidence-placeholder-pdf",
      ".doc" or ".docx" => "orden-evidence-placeholder-word",
      ".xls" or ".xlsx" or ".csv" => "orden-evidence-placeholder-sheet",
      ".ppt" or ".pptx" => "orden-evidence-placeholder-slide",
      ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" => "orden-evidence-placeholder-image",
      ".xml" or ".json" => "orden-evidence-placeholder-code",
      ".zip" or ".rar" or ".7z" => "orden-evidence-placeholder-archive",
      _ => "orden-evidence-placeholder-file"
    };

  private static string GetFileExtension(string? fileName)
    => string.IsNullOrWhiteSpace(fileName)
      ? string.Empty
      : Path.GetExtension(fileName).ToLowerInvariant();

  private static string ResolveContentTypeFromExtension(string extension)
    => extension switch
    {
      ".pdf" => "application/pdf",
      ".doc" => "application/msword",
      ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
      ".xls" => "application/vnd.ms-excel",
      ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
      ".ppt" => "application/vnd.ms-powerpoint",
      ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
      ".csv" => "text/csv",
      ".txt" => "text/plain",
      ".xml" => "application/xml",
      ".json" => "application/json",
      ".jpg" or ".jpeg" => "image/jpeg",
      ".png" => "image/png",
      ".gif" => "image/gif",
      ".webp" => "image/webp",
      ".bmp" => "image/bmp",
      ".zip" => "application/zip",
      _ => "application/octet-stream"
    };

  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private async Task ResolveCurrentUserAsync()
  {
    var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
    var user = authState.User;
    CurrentUserName = user.Identity?.Name?.Trim() switch
    {
      { Length: > 0 } name => name,
      _ => "OrionERP"
    };
    IsPrivilegedUser = OrdenTrabajoPermissions.CanAccessManagement(user.IsInRole);

    CurrentEmployeeId = int.TryParse(user.FindFirst("employee_id")?.Value, out var employeeId) ? employeeId : null;
  }

  public async ValueTask DisposeAsync()
  {
    await CloseCameraAsync(showState: false);
    if (CameraModule is not null)
    {
      try
      {
        await CameraModule.DisposeAsync();
      }
      catch (JSDisconnectedException)
      {
      }
    }
  }

  protected sealed class CameraDeviceOption
  {
    public string DeviceId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
  }

  protected sealed class CameraCaptureResult
  {
    public string ImageContentType { get; set; } = "image/jpeg";
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public string ThumbnailContentType { get; set; } = "image/jpeg";
    public int ThumbnailWidth { get; set; }
    public int ThumbnailHeight { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceLabel { get; set; } = string.Empty;
    public string FacingMode { get; set; } = string.Empty;
  }

  private sealed class CameraStreamInfo
  {
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceLabel { get; set; } = string.Empty;
    public string FacingMode { get; set; } = string.Empty;
  }
}
