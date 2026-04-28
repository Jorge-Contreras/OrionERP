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
  private const int ImageMaxPixels = 1600;
  private const int ThumbnailMaxPixels = 320;

  [Parameter] public int Id { get; set; }

  [Inject] private IOrdenTrabajoService OrdenTrabajoService { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;
  [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
  [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
  [Inject] private IJSRuntime JSRuntime { get; set; } = default!;

  protected CultureInfo CurrencyCulture { get; } = CultureInfo.GetCultureInfo("es-MX");
  protected OrdenTrabajoDetailDto? Order { get; set; }
  protected List<OrdenTrabajoLookupDto> Employees { get; set; } = [];
  protected OrdenTrabajoUpdateRequest EditRequest { get; set; } = new();
  protected List<OrdenTrabajoStepSaveRequest> StepEditor { get; set; } = [];
  protected HashSet<int> EditHelperIds { get; set; } = [];
  protected Dictionary<int, string?> StepNotes { get; set; } = [];
  protected List<OrdenTrabajoTransactionSearchItemDto> TransactionMatches { get; set; } = [];
  protected string TransactionSearchText { get; set; } = string.Empty;
  protected string ReviewReason { get; set; } = string.Empty;
  protected string CancelReason { get; set; } = string.Empty;
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
  protected bool IsEvidencePreviewOpen { get; set; }
  protected bool IsEvidencePreviewLoading { get; set; }
  protected string? EvidencePreviewImageDataUrl { get; set; }
  protected string? EvidencePreviewError { get; set; }
  protected string EvidencePreviewTitle { get; set; } = "Foto capturada";
  protected string? EvidencePreviewSubtitle { get; set; }
  protected int? ActiveCameraStepId { get; set; }
  protected List<CameraDeviceOption> CameraDevices { get; set; } = [];
  protected string? SelectedCameraDeviceId { get; set; }
  protected ElementReference CameraVideoElement { get; set; }

  protected bool CanReview => IsPrivilegedUser;
  protected bool IsInReview => Order?.Estado == OrdenTrabajoCodes.EstadoEnRevision;
  protected bool CanEdit => Order is not null && IsPrivilegedUser && IsEditableStatus(Order.Estado);
  protected bool CanEditSteps => CanEdit && Order?.HasBeenSubmittedForReview == false;
  protected bool CanExecute => Order is not null
    && IsExecutableStatus(Order.Estado)
    && IsCurrentUserAssigned();
  protected bool CanRemoveEvidence => CanExecute && Order?.HasBeenSubmittedForReview == false;

  private int? ActorEmployeeIdForExecution => CurrentEmployeeId;
  private IJSObjectReference? CameraModule { get; set; }
  private bool PendingCameraStart { get; set; }

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
    StepEditor.Add(CreateEmptyStep(StepEditor.Count + 1));
  }

  protected void RemoveStep(OrdenTrabajoStepSaveRequest step)
  {
    StepEditor.Remove(step);
    ResequenceStepEditor();
  }

  protected async Task SaveStepsAsync()
  {
    if (!CanEditSteps || Order is null)
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

    await MutateAsync(() => OrdenTrabajoService.UpdateStepAsync(
      Order.Id,
      step.Id,
      new OrdenTrabajoStepUpdateRequest
      {
        Estado = status,
        Notas = StepNotes.TryGetValue(step.Id, out var notes) ? notes : step.Notas,
        UpdatedBy = CurrentUserName,
        ActorEmployeeId = ActorEmployeeIdForExecution
      }));
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

    IsMutating = true;
    try
    {
      var image = await BuildImageBytesAsync(file, ImageMaxPixels);
      var thumb = await BuildImageBytesAsync(file, ThumbnailMaxPixels);
      var result = await OrdenTrabajoService.AddStepEvidenceAsync(
        Order.Id,
        step.Id,
        new OrdenTrabajoEvidenceCreateRequest
        {
          ImageBytes = image.Bytes,
          ThumbnailBytes = thumb.Bytes,
          FileName = file.Name,
          ContentType = image.ContentType,
          ThumbnailContentType = thumb.ContentType,
          DeviceInfo = "Blazor InputFile upload",
          CaptureSource = OrdenTrabajoCodes.EvidenciaFile,
          CapturedBy = CurrentUserName,
          ActorEmployeeId = ActorEmployeeIdForExecution
        });
      await HandleMutationResultAsync(result);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo guardar la evidencia. {ex.Message}");
    }
    finally
    {
      IsMutating = false;
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
        CameraError = "Este navegador no permite acceso directo a la camara. Usa la opcion de archivo.";
        IsCameraStarting = false;
        return;
      }

      PendingCameraStart = true;
      await InvokeAsync(StateHasChanged);
    }
    catch (Exception ex)
    {
      CameraError = $"No se pudo preparar la camara. {ex.Message}";
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
      var imageBytes = await ReadCameraBlobAsync(module, "getLastImage", MaxImageBytes);
      var thumbnailBytes = await ReadCameraBlobAsync(module, "getLastThumbnail", MaxImageBytes);

      var result = await OrdenTrabajoService.AddStepEvidenceAsync(
        Order.Id,
        step.Id,
        new OrdenTrabajoEvidenceCreateRequest
        {
          ImageBytes = imageBytes,
          ThumbnailBytes = thumbnailBytes,
          FileName = $"camara-{Order.Folio}-{step.Id}-{DateTime.Now:yyyyMMddHHmmss}.jpg",
          ContentType = capture.ImageContentType,
          ThumbnailContentType = capture.ThumbnailContentType,
          DeviceInfo = BuildCameraDeviceInfo(capture),
          CaptureSource = OrdenTrabajoCodes.EvidenciaCamera,
          CapturedBy = CurrentUserName,
          ActorEmployeeId = ActorEmployeeIdForExecution
        });

      if (result.Success)
      {
        await module.InvokeVoidAsync("clearLastCapture");
        await CloseCameraAsync(showState: false);
      }

      await HandleMutationResultAsync(result);
    }
    catch (Exception ex)
    {
      CameraError = $"No se pudo capturar la foto. {ex.Message}";
    }
    finally
    {
      IsCameraCapturing = false;
    }
  }

  protected async Task RemoveEvidenceAsync(OrdenTrabajoStepDto step, int evidenceId)
  {
    if (!CanExecute || Order is null)
    {
      return;
    }

    await MutateAsync(() => OrdenTrabajoService.RemoveStepEvidenceAsync(Order.Id, step.Id, evidenceId, CurrentUserName, ActorEmployeeIdForExecution));
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
        EvidencePreviewError = "No se encontro la foto completa.";
        return;
      }

      EvidencePreviewTitle = string.IsNullOrWhiteSpace(content.FileName)
        ? EvidencePreviewTitle
        : content.FileName;
      EvidencePreviewImageDataUrl = BuildImageDataUrl(content.ContentType, content.Bytes);
    }
    catch (Exception ex)
    {
      EvidencePreviewError = $"No se pudo cargar la foto. {ex.Message}";
    }
    finally
    {
      IsEvidencePreviewLoading = false;
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

  protected string? GetStepNotes(OrdenTrabajoStepDto step)
    => StepNotes.TryGetValue(step.Id, out var notes) ? notes : step.Notas;

  protected void SetStepNotes(int stepId, string? notes)
    => StepNotes[stepId] = notes;

  protected override async Task OnAfterRenderAsync(bool firstRender)
  {
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
      PoliticaFoto = OrdenTrabajoCodes.FotoNoPermitida,
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
    catch (Exception ex)
    {
      CameraError = $"No se pudo abrir la camara. {ex.Message}";
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

    if (CameraModule is not null)
    {
      try
      {
        await CameraModule.InvokeVoidAsync("stop");
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
    IsPrivilegedUser = user.IsInRole("Administrador")
      || user.IsInRole("OrdenTrabajoAdmin")
      || user.IsInRole("OrdenTrabajoSupervisor");

    var appUser = await UserManager.GetUserAsync(user);
    CurrentEmployeeId = appUser?.EmployeeId;
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
