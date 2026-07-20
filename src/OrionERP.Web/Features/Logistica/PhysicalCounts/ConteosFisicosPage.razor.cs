using System.Globalization;
using System.IO;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using OrionERP.Application.Features.Logistica.Locations;
using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Application.Features.Logistica.PhysicalCounts;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Web.Services;
using OrionERP.Web.State;

namespace OrionERP.Web.Features.Logistica.PhysicalCounts;

public partial class ConteosFisicosPage : ComponentBase, IDisposable
{
  private static readonly CultureInfo QuantityInputCulture = CultureInfo.GetCultureInfo("es-MX");
  private static readonly NumberStyles QuantityInputNumberStyles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands;

  private string _countedQuantityInput = string.Empty;
  private ElementReference MobileCountedQuantityInputRef;
  private ElementReference CountedQuantityInputRef;
  private bool _focusCountedQuantityInputPending;
  private bool _showPostedSessions;
  private bool _isSessionListVisibleOnMobile = true;
  private int? _appliedQuerySessionId;

  [Parameter]
  [SupplyParameterFromQuery(Name = "sessionId")]
  public int? QuerySessionId { get; set; }

  [Inject] private IPhysicalCountService PhysicalCountService { get; set; } = default!;
  [Inject] private IMaterialService MaterialService { get; set; } = default!;
  [Inject] private ILocationService LocationService { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;
  [Inject] private IJSRuntime Js { get; set; } = default!;
  [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
  [Inject] private IUserRfcState RfcState { get; set; } = default!;

  protected List<LookupOptionDto> LocationOptions { get; set; } = [];
  protected List<PhysicalCountSessionSummaryDto> Sessions { get; set; } = [];
  protected PhysicalCountSessionCreateRequest SessionCreateRequest { get; set; } = new();
  protected PhysicalCountSessionDetailDto? SelectedSession { get; set; }
  protected PhysicalCountLineDto? SelectedLine { get; set; }
  protected PhysicalCountLineCaptureRequest LineCapture { get; set; } = new();
  protected Dictionary<int, string> MaterialThumbnailDataUrls { get; set; } = [];
  protected string CurrentUserName { get; set; } = "OrionERP";
  protected bool IsLoadingSessions { get; set; }
  protected bool IsCreatingSession { get; set; }
  protected bool IsSavingLine { get; set; }
  protected bool IsMutatingSession { get; set; }
  protected bool IsCancelingSession { get; set; }
  protected bool IsRequestingRecount { get; set; }
  protected bool IsAdministrator { get; set; }
  protected bool ShowCancelModal { get; set; }
  protected bool ShowRecountModal { get; set; }
  protected string CancelReason { get; set; } = string.Empty;
  protected List<RecountLineEditor> RecountPlanLines { get; set; } = [];
  protected bool ShowMaterialImageModal { get; set; }
  protected bool IsLoadingMaterialImage { get; set; }
  protected byte[]? PendingLineAttachmentBytes { get; set; }
  protected string? PendingLineAttachmentName { get; set; }
  protected string? PendingLineAttachmentContentType { get; set; }
  protected string? MaterialImageModalTitle { get; set; }
  protected string? MaterialImageModalDataUrl { get; set; }

  protected bool ShowPostedSessions
  {
    get => _showPostedSessions;
    set
    {
      if (_showPostedSessions == value)
      {
        return;
      }

      _showPostedSessions = value;
      EnsureSelectedSessionVisible();
    }
  }

  protected IReadOnlyList<PhysicalCountSessionSummaryDto> VisibleSessions => Sessions
    .Where(session => ShouldDisplaySession(session.Status))
    .ToList();

  protected bool CanSubmit => SelectedSession is not null && (IsDraftStatus(SelectedSession.Status) || IsRecountStatus(SelectedSession.Status));
  protected bool CanCancel => IsAdministrator && SelectedSession is not null && IsCancelableStatus(SelectedSession.Status);
  protected bool CanRequestRecount => IsAdministrator && SelectedSession is not null && (IsSubmittedStatus(SelectedSession.Status) || IsApprovedStatus(SelectedSession.Status));
  protected bool CanApprove => IsAdministrator && SelectedSession is not null && IsSubmittedStatus(SelectedSession.Status);
  protected bool CanPost => IsAdministrator && SelectedSession is not null && IsApprovedStatus(SelectedSession.Status);
  protected bool CanCaptureLine => SelectedSession is not null
    && SelectedLine is not null
    && (IsDraftStatus(SelectedSession.Status) || IsRecountStatus(SelectedSession.Status));
  protected string SelectedSessionStatusBadgeClass => GetSessionStatusBadgeClass(SelectedSession?.Status);
  protected string SelectedSessionStatusLabel => GetSessionStatusLabel(SelectedSession?.Status);
  protected string CountedQuantityInput
  {
    get => _countedQuantityInput;
    set
    {
      _countedQuantityInput = value;

      if (TryParseCountedQuantity(value, out var parsedQuantity))
      {
        LineCapture.CountedQuantity = parsedQuantity;
      }
    }
  }

  protected string CurrentVarianceText => SelectedLine is null
    ? "0.00"
    : TryParseCountedQuantity(CountedQuantityInput, out var countedQuantity)
      ? (countedQuantity - SelectedLine.ExpectedQuantity).ToString("N2")
      : "Pendiente";

  protected override async Task OnInitializedAsync()
  {
    RfcState.Changed += HandleRfcChanged;
    CurrentUserName = await ResolveCurrentUserAsync();
    LocationOptions = (await LocationService.GetLocationLookupAsync(inventoryOnly: true)).ToList();
    await CargarSesionesAsync();
    await ApplyQuerySessionSelectionAsync();
  }

  private void HandleRfcChanged() => _ = InvokeAsync(async () =>
  {
    ClearSelectedSession();
    LocationOptions = (await LocationService.GetLocationLookupAsync(inventoryOnly: true)).ToList();
    await CargarSesionesAsync();
    StateHasChanged();
  });

  public void Dispose() => RfcState.Changed -= HandleRfcChanged;

  protected override async Task OnParametersSetAsync()
  {
    if (Sessions.Count > 0)
    {
      await ApplyQuerySessionSelectionAsync();
    }
  }

  protected async Task CargarSesionesAsync()
  {
    IsLoadingSessions = true;
    try
    {
      Sessions = (await PhysicalCountService.GetSessionsAsync()).ToList();
      SyncSelectedSessionAfterRefresh();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudieron cargar las sesiones de conteo. {ex.Message}");
    }
    finally
    {
      IsLoadingSessions = false;
      StateHasChanged();
    }
  }

  protected async Task CrearSesionAsync()
  {
    if (SessionCreateRequest.LocationId <= 0)
    {
      UiMessages.ShowWarning("Selecciona una ubicación para crear la sesión.");
      return;
    }

    IsCreatingSession = true;
    try
    {
      SessionCreateRequest.CreatedBy = CurrentUserName;
      var result = await PhysicalCountService.CreateSessionAsync(SessionCreateRequest);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      SessionCreateRequest = new();
      await CargarSesionesAsync();
      if (result.EntityId.HasValue)
      {
        await ReselectSessionIfVisibleAsync(result.EntityId.Value);
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo crear la sesión. {ex.Message}");
    }
    finally
    {
      IsCreatingSession = false;
    }
  }

  protected async Task SeleccionarSesionAsync(int sessionId)
  {
    try
    {
      var session = await PhysicalCountService.GetSessionAsync(sessionId);
      if (session is null || !ShouldDisplaySession(session.Status))
      {
        ClearSelectedSession();
        return;
      }

      SelectedSession = session;
      _isSessionListVisibleOnMobile = false;
      MaterialThumbnailDataUrls = [];
      CloseMaterialImageModal();
      await CargarMiniaturasMaterialesAsync();
      SelectedLine = null;
      _countedQuantityInput = string.Empty;
      if (SelectedSession?.Lines.Count > 0)
      {
        SeleccionarLinea(SelectedSession.Lines[0]);
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cargar la sesión seleccionada. {ex.Message}");
    }
  }

  protected void SeleccionarLinea(PhysicalCountLineDto line)
  {
    SelectedLine = line;
    LineCapture = new PhysicalCountLineCaptureRequest
    {
      SessionId = SelectedSession?.Id ?? 0,
      LineId = line.Id,
      CountedQuantity = line.CountedQuantity ?? 0m,
      Notes = line.Notes,
      IsMissing = line.IsMissing,
      IsDamaged = line.IsDamaged,
      CapturedBy = CurrentUserName,
      Lots = line.Lots.Select(lot => new PhysicalCountLotCaptureRequest
      {
        MaterialLotId = lot.MaterialLotId,
        CountedQuantity = lot.CountedQuantity
      }).ToList()
    };

    PendingLineAttachmentBytes = null;
    PendingLineAttachmentName = null;
    PendingLineAttachmentContentType = null;
    UpdateCountedQuantityInputFromCapture();
    QueueCountedQuantityFocus();
  }

  protected bool IsLineCapturedByCurrentUser(PhysicalCountLineDto line)
  {
    if (string.IsNullOrWhiteSpace(CurrentUserName) || !line.CapturedAt.HasValue || string.IsNullOrWhiteSpace(line.CapturedBy))
    {
      return false;
    }

    return string.Equals(line.CapturedBy.Trim(), CurrentUserName.Trim(), StringComparison.OrdinalIgnoreCase);
  }

  protected void UpdateTotalFromLots()
  {
    if (LineCapture.Lots.Count == 0) return;
    LineCapture.CountedQuantity = LineCapture.Lots.Sum(lot => lot.CountedQuantity ?? 0m);
    UpdateCountedQuantityInputFromCapture();
  }

  protected bool IsSelectedLine(PhysicalCountLineDto line)
    => SelectedLine?.Id == line.Id;

  protected static string GetCountUnitLabel(string? unitName)
    => string.IsNullOrWhiteSpace(unitName) ? "sin unidad configurada" : unitName.Trim();

  protected static string GetCountUnitBadgeClass(string? unitName)
    => string.IsNullOrWhiteSpace(unitName)
      ? "conteos-unit-badge conteos-unit-badge-missing"
      : "conteos-unit-badge";

  protected static string GetQuantityFieldLabel(string label, string? unitName)
    => string.IsNullOrWhiteSpace(unitName)
      ? label
      : $"{label} ({unitName.Trim()})";

  protected static string FormatLineCapturedSummary(PhysicalCountLineDto line)
  {
    if (!line.CapturedAt.HasValue)
    {
      return "Sin conteo capturado";
    }

    var capturedAt = line.CapturedAt.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
    return string.IsNullOrWhiteSpace(line.CapturedBy)
      ? $"Conteo {capturedAt}"
      : $"Conteo {capturedAt} · {line.CapturedBy.Trim()}";
  }

  protected string GetLineRowClass(PhysicalCountLineDto line)
  {
    var classes = new List<string>();
    var isSelected = IsSelectedLine(line);
    var capturedByCurrentUser = IsLineCapturedByCurrentUser(line);

    if (isSelected && capturedByCurrentUser)
    {
      classes.Add("table-primary");
      classes.Add("conteos-line-row-captured");
    }
    else if (isSelected)
    {
      classes.Add("table-primary");
    }
    else if (capturedByCurrentUser)
    {
      classes.Add("conteos-line-row-captured");
    }

    if (HasRecountIssue(line))
    {
      classes.Add("conteos-line-row-recount");
    }

    return string.Join(" ", classes);
  }

  protected string GetSessionRowClass(PhysicalCountSessionSummaryDto session)
  {
    var isSelected = SelectedSession?.Id == session.Id;
    var selectionClass = isSelected ? "table-primary" : string.Empty;
    return $"{selectionClass} conteos-session-row {GetSessionStatusClass(session.Status)}".Trim();
  }

  protected string GetSessionStatusBadgeClass(string? status)
    => $"conteos-session-status-badge {GetSessionStatusClass(status)}";

  protected static string GetSessionStatusLabel(string? status)
    => NormalizeSessionStatus(status) switch
    {
      "draft" => "Borrador",
      "submitted" => "Enviada",
      "approved" => "Aprobada",
      "recount" => "Reconteo",
      "posted" => "Contabilizada",
      "canceled" => "Cancelada",
      _ => string.IsNullOrWhiteSpace(status) ? "Sin estatus" : status.Trim()
    };

  protected async Task OnLineAttachmentSelectedAsync(InputFileChangeEventArgs args)
  {
    var file = args.File;
    if (file is null)
    {
      return;
    }

    await using var stream = file.OpenReadStream(long.MaxValue);
    using var ms = new MemoryStream();
    await stream.CopyToAsync(ms);
    PendingLineAttachmentBytes = ms.ToArray();
    PendingLineAttachmentName = file.Name;
    PendingLineAttachmentContentType = file.ContentType;
  }

  protected Task GuardarLineaMovilAsync()
    => GuardarLineaAsync(advanceToNextUncountedLine: true);

  protected async Task GuardarLineaAsync()
    => await GuardarLineaAsync(advanceToNextUncountedLine: false);

  protected async Task GuardarLineaAsync(bool advanceToNextUncountedLine)
  {
    if (!CanCaptureLine || SelectedLine is null || SelectedSession is null)
    {
      return;
    }

    if (!TryParseCountedQuantity(CountedQuantityInput, out var countedQuantity))
    {
      UiMessages.ShowWarning("Captura una cantidad válida en el campo contado.");
      QueueCountedQuantityFocus();
      return;
    }

    IsSavingLine = true;
    try
    {
      LineCapture.CountedQuantity = countedQuantity;
      LineCapture.CapturedBy = CurrentUserName;
      LineCapture.AttachmentBytes = PendingLineAttachmentBytes;
      LineCapture.AttachmentFileName = PendingLineAttachmentName;
      LineCapture.AttachmentExtension = Path.GetExtension(PendingLineAttachmentName ?? string.Empty).TrimStart('.');
      LineCapture.AttachmentContentType = PendingLineAttachmentContentType;
      LineCapture.AttachmentDescription = PendingLineAttachmentName;

      var result = await PhysicalCountService.CaptureLineAsync(LineCapture);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      await SeleccionarSesionAsync(SelectedSession.Id);
      if (SelectedSession is not null)
      {
        var preferredLine = advanceToNextUncountedLine
          ? FindNextUncountedLine(SelectedSession.Lines, LineCapture.LineId)
          : null;
        preferredLine ??= SelectedSession.Lines.FirstOrDefault(line => line.Id == LineCapture.LineId);
        preferredLine ??= SelectedSession.Lines.FirstOrDefault();
        if (preferredLine is not null)
        {
          SeleccionarLinea(preferredLine);
        }
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo guardar la línea. {ex.Message}");
    }
    finally
    {
      IsSavingLine = false;
    }
  }

  protected void OnCountedQuantityInput(ChangeEventArgs args)
  {
    CountedQuantityInput = args.Value?.ToString() ?? string.Empty;
  }

  protected void OnCountedQuantityBlur()
  {
    if (TryParseCountedQuantity(CountedQuantityInput, out var parsedQuantity))
    {
      LineCapture.CountedQuantity = parsedQuantity;
      _countedQuantityInput = FormatCountedQuantity(parsedQuantity);
      return;
    }

    UpdateCountedQuantityInputFromCapture();
  }

  protected async Task SelectCountedQuantityAsync()
  {
    _ = await TryFocusCountedQuantityInputAsync(scrollIntoViewOnMobile: false);
  }

  protected async Task HandleCountedQuantityKeyDownAsync(KeyboardEventArgs args)
  {
    if (!string.Equals(args.Key, "Enter", StringComparison.Ordinal) || !CanCaptureLine || IsSavingLine)
    {
      return;
    }

    await GuardarLineaAsync();
  }

  protected async Task HandleMobileCountedQuantityKeyDownAsync(KeyboardEventArgs args)
  {
    if (!string.Equals(args.Key, "Enter", StringComparison.Ordinal) || !CanCaptureLine || IsSavingLine)
    {
      return;
    }

    await GuardarLineaMovilAsync();
  }

  protected override async Task OnAfterRenderAsync(bool firstRender)
  {
    if (!_focusCountedQuantityInputPending || !CanCaptureLine)
    {
      return;
    }

    _focusCountedQuantityInputPending = false;
    if (!await TryFocusCountedQuantityInputAsync(scrollIntoViewOnMobile: true))
    {
      _focusCountedQuantityInputPending = true;
    }
  }

  protected async Task EnviarSesionAsync() => await EjecutarSesionAsync(
    () => PhysicalCountService.SubmitSessionAsync(SelectedSession!.Id, CurrentUserName));

  protected async Task CancelarSesionAsync()
  {
    if (!CanCancel || SelectedSession is null || IsMutatingSession)
    {
      return;
    }

    CancelReason = string.Empty;
    ShowCancelModal = true;
  }

  protected void CerrarCancelacion()
  {
    if (IsCancelingSession)
    {
      return;
    }

    ShowCancelModal = false;
    CancelReason = string.Empty;
  }

  protected async Task ConfirmarCancelacionAsync()
  {
    if (!CanCancel || SelectedSession is null || IsMutatingSession)
    {
      return;
    }

    if (string.IsNullOrWhiteSpace(CancelReason))
    {
      UiMessages.ShowWarning("Captura una razón para cancelar el conteo.");
      return;
    }

    var sessionId = SelectedSession.Id;
    IsCancelingSession = true;
    IsMutatingSession = true;

    try
    {
      var result = await PhysicalCountService.CancelSessionAsync(new PhysicalCountCancelRequest
      {
        SessionId = sessionId,
        CanceledBy = CurrentUserName,
        Reason = CancelReason
      });
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      ShowCancelModal = false;
      CancelReason = string.Empty;
      ClearSelectedSession();
      await CargarSesionesAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cancelar la sesión. {ex.Message}");
    }
    finally
    {
      IsCancelingSession = false;
      IsMutatingSession = false;
    }
  }

  protected void AbrirReconteo()
  {
    if (!CanRequestRecount || SelectedSession is null)
    {
      return;
    }

    RecountPlanLines = SelectedSession.Lines
      .Select(line => new RecountLineEditor
      {
        LineId = line.Id,
        MaterialCode = line.MaterialCode,
        MaterialDescription = string.IsNullOrWhiteSpace(line.MaterialDescription) ? line.MaterialCode : line.MaterialDescription,
        ExpectedQuantity = line.ExpectedQuantity,
        CountedQuantity = line.CountedQuantity,
        VarianceQuantity = line.VarianceQuantity,
        IssueCode = PhysicalCountRecountIssueCodes.QuantityMismatch
      })
      .ToList();
    ShowRecountModal = true;
  }

  protected void CerrarReconteo()
  {
    if (IsRequestingRecount)
    {
      return;
    }

    ShowRecountModal = false;
    RecountPlanLines = [];
  }

  protected async Task EnviarAReconteoAsync()
  {
    if (!CanRequestRecount || SelectedSession is null || IsMutatingSession)
    {
      return;
    }

    var selectedLines = RecountPlanLines.Where(line => line.IsSelected).ToList();
    if (selectedLines.Count == 0)
    {
      UiMessages.ShowWarning("Selecciona al menos una línea para enviar a reconteo.");
      return;
    }

    if (selectedLines.Any(line => string.IsNullOrWhiteSpace(line.IssueCode) || !PhysicalCountRecountIssueCodes.All.Contains(line.IssueCode) || string.IsNullOrWhiteSpace(line.Reason)))
    {
      UiMessages.ShowWarning("Cada línea seleccionada necesita incidencia y razón.");
      return;
    }

    var sessionId = SelectedSession.Id;
    IsRequestingRecount = true;
    IsMutatingSession = true;

    try
    {
      var result = await PhysicalCountService.RequestRecountAsync(new PhysicalCountRecountRequest
      {
        SessionId = sessionId,
        RequestedBy = CurrentUserName,
        Lines = selectedLines
          .Select(line => new PhysicalCountRecountLineRequest
          {
            LineId = line.LineId,
            IssueCode = line.IssueCode,
            Reason = line.Reason
          })
          .ToList()
      });
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      ShowRecountModal = false;
      RecountPlanLines = [];
      await CargarSesionesAsync();
      await ReselectSessionIfVisibleAsync(sessionId);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo enviar la sesión a reconteo. {ex.Message}");
    }
    finally
    {
      IsRequestingRecount = false;
      IsMutatingSession = false;
    }
  }

  protected async Task MostrarSesionesAsync()
  {
    _isSessionListVisibleOnMobile = true;
    StateHasChanged();
    await ScrollToTopAsync();
  }

  protected async Task AprobarSesionAsync() => await EjecutarSesionAsync(
    () => PhysicalCountService.ApproveSessionAsync(SelectedSession!.Id, CurrentUserName));

  protected async Task ContabilizarSesionAsync() => await EjecutarSesionAsync(
    () => PhysicalCountService.PostSessionAsync(SelectedSession!.Id, CurrentUserName));

  protected async Task DescargarEvidenciaAsync(int attachmentId)
  {
    try
    {
      var content = await PhysicalCountService.GetAttachmentContentAsync(attachmentId);
      if (content is null)
      {
        UiMessages.ShowWarning("No se encontró la evidencia solicitada.");
        return;
      }

      var dataUrl = FormattableString.Invariant($"data:{content.ContentType};base64,{Convert.ToBase64String(content.Bytes)}");
      await Js.InvokeVoidAsync("triggerFileDownload", content.FileName, dataUrl);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo descargar la evidencia. {ex.Message}");
    }
  }

  protected async Task AbrirImagenMaterialAsync(PhysicalCountLineDto line)
  {
    if (SelectedLine?.Id != line.Id)
    {
      SeleccionarLinea(line);
    }

    ShowMaterialImageModal = true;
    IsLoadingMaterialImage = true;
    MaterialImageModalTitle = string.IsNullOrWhiteSpace(line.MaterialDescription)
      ? line.MaterialCode
      : string.IsNullOrWhiteSpace(line.MaterialCode)
        ? line.MaterialDescription
        : $"{line.MaterialDescription} · {line.MaterialCode}";
    MaterialImageModalDataUrl = TryGetMaterialThumbnailDataUrl(line.MaterialId, out var thumbnailDataUrl)
      ? thumbnailDataUrl
      : null;

    try
    {
      var image = await MaterialService.GetMaterialImageAsync(LogisticsRfc.Require(RfcState.CurrentRfc), line.MaterialId);
      if (image is null)
      {
        if (MaterialImageModalDataUrl is null)
        {
          UiMessages.ShowWarning("El material seleccionado no tiene imagen disponible.");
        }

        return;
      }

      MaterialImageModalDataUrl = BuildDataUrl(image.ContentType, image.Bytes);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cargar la imagen del material. {ex.Message}");
    }
    finally
    {
      IsLoadingMaterialImage = false;
    }
  }

  protected void CloseMaterialImageModal()
  {
    ShowMaterialImageModal = false;
    IsLoadingMaterialImage = false;
    MaterialImageModalTitle = null;
    MaterialImageModalDataUrl = null;
  }

  private async Task EjecutarSesionAsync(Func<Task<LogisticsCommandResult>> operation)
  {
    if (SelectedSession is null)
    {
      return;
    }

    IsMutatingSession = true;
    try
    {
      var sessionId = SelectedSession.Id;
      var result = await operation();
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      await CargarSesionesAsync();
      await ReselectSessionIfVisibleAsync(sessionId);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo actualizar la sesión. {ex.Message}");
    }
    finally
    {
      IsMutatingSession = false;
    }
  }

  private async Task<bool> ConfirmAsync(string message)
  {
    try
    {
      return await Js.InvokeAsync<bool>("confirm", message);
    }
    catch
    {
      return true;
    }
  }

  private async Task<string> ResolveCurrentUserAsync()
  {
    var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
    var user = authState.User;
    IsAdministrator = user.IsInRole("Administrador");
    return user.Identity?.Name?.Trim() switch
    {
      { Length: > 0 } name => name,
      _ => "Administrador"
    };
  }

  private async Task CargarMiniaturasMaterialesAsync()
  {
    if (SelectedSession?.Lines.Count is not > 0)
    {
      MaterialThumbnailDataUrls = [];
      return;
    }

    try
    {
      var thumbnails = await MaterialService.GetMaterialThumbnailsAsync(LogisticsRfc.Require(RfcState.CurrentRfc), SelectedSession.Lines.Select(line => line.MaterialId));
      MaterialThumbnailDataUrls = thumbnails
        .Where(thumbnail => thumbnail.Bytes.Length > 0)
        .ToDictionary(
          thumbnail => thumbnail.Id,
          thumbnail => BuildDataUrl(thumbnail.ContentType, thumbnail.Bytes));
    }
    catch (Exception ex)
    {
      MaterialThumbnailDataUrls = [];
      UiMessages.ShowWarning($"No se pudieron cargar las miniaturas de los materiales. {ex.Message}");
    }
  }

  private bool TryGetMaterialThumbnailDataUrl(int materialId, out string dataUrl)
    => MaterialThumbnailDataUrls.TryGetValue(materialId, out dataUrl!);

  protected string? GetMaterialThumbnailDataUrl(int materialId)
    => TryGetMaterialThumbnailDataUrl(materialId, out var dataUrl) ? dataUrl : null;

  protected string GetSessionsSectionClass()
    => _isSessionListVisibleOnMobile || SelectedSession is null
      ? "card shadow-sm"
      : "card shadow-sm conteos-mobile-list-hidden";

  private void QueueCountedQuantityFocus()
  {
    _focusCountedQuantityInputPending = CanCaptureLine;
  }

  private async Task ReselectSessionIfVisibleAsync(int sessionId)
  {
    var session = Sessions.FirstOrDefault(item => item.Id == sessionId);
    if (session is null || !ShouldDisplaySession(session.Status))
    {
      ClearSelectedSession();
      return;
    }

    await SeleccionarSesionAsync(sessionId);
  }

  private async Task ApplyQuerySessionSelectionAsync()
  {
    if (!QuerySessionId.HasValue || _appliedQuerySessionId == QuerySessionId.Value)
    {
      return;
    }

    _appliedQuerySessionId = QuerySessionId.Value;
    await ReselectSessionIfVisibleAsync(QuerySessionId.Value);
  }

  private static PhysicalCountLineDto? FindNextUncountedLine(IReadOnlyList<PhysicalCountLineDto> lines, int currentLineId)
  {
    if (lines.Count == 0)
    {
      return null;
    }

    var currentIndex = -1;
    for (var index = 0; index < lines.Count; index++)
    {
      if (lines[index].Id == currentLineId)
      {
        currentIndex = index;
        break;
      }
    }

    if (currentIndex < 0)
    {
      return lines.FirstOrDefault(line => line.CountedQuantity is null);
    }

    for (var offset = 1; offset < lines.Count; offset++)
    {
      var candidate = lines[(currentIndex + offset) % lines.Count];
      if (candidate.CountedQuantity is null)
      {
        return candidate;
      }
    }

    return null;
  }

  private static string GetSessionStatusClass(string? status)
    => NormalizeSessionStatus(status) switch
    {
      "draft" => "conteos-session-status-draft",
      "submitted" => "conteos-session-status-submitted",
      "approved" => "conteos-session-status-approved",
      "recount" => "conteos-session-status-recount",
      "posted" => "conteos-session-status-posted",
      "canceled" => "conteos-session-status-canceled",
      _ => "conteos-session-status-unknown"
    };

  private void UpdateCountedQuantityInputFromCapture()
  {
    _countedQuantityInput = SelectedLine?.CountedQuantity is null
      ? string.Empty
      : FormatCountedQuantity(LineCapture.CountedQuantity);
  }

  private void SyncSelectedSessionAfterRefresh()
  {
    if (SelectedSession is null)
    {
      return;
    }

    var session = Sessions.FirstOrDefault(item => item.Id == SelectedSession.Id);
    if (session is null || !ShouldDisplaySession(session.Status))
    {
      ClearSelectedSession();
    }
  }

  private void EnsureSelectedSessionVisible()
  {
    if (SelectedSession is null || ShouldDisplaySession(SelectedSession.Status))
    {
      return;
    }

    ClearSelectedSession();
  }

  private static string NormalizeSessionStatus(string? status)
    => string.IsNullOrWhiteSpace(status) ? string.Empty : status.Trim().ToLowerInvariant();

  private static bool IsDraftStatus(string? status)
    => NormalizeSessionStatus(status) == "draft";

  private static bool IsSubmittedStatus(string? status)
    => NormalizeSessionStatus(status) == "submitted";

  private static bool IsApprovedStatus(string? status)
    => NormalizeSessionStatus(status) == "approved";

  private static bool IsRecountStatus(string? status)
    => NormalizeSessionStatus(status) == "recount";

  private static bool IsPostedStatus(string? status)
    => NormalizeSessionStatus(status) == "posted";

  private static bool IsCanceledStatus(string? status)
    => NormalizeSessionStatus(status) == "canceled";

  private static bool IsCancelableStatus(string? status)
    => IsDraftStatus(status)
      || IsSubmittedStatus(status)
      || IsApprovedStatus(status)
      || IsRecountStatus(status);

  private bool ShouldDisplaySession(string? status)
    => ShowPostedSessions || (!IsPostedStatus(status) && !IsCanceledStatus(status));

  protected static bool HasRecountIssue(PhysicalCountLineDto line)
    => !string.IsNullOrWhiteSpace(line.RecountIssueCode) || !string.IsNullOrWhiteSpace(line.RecountReason);

  protected static string FormatOptionalQuantity(decimal? quantity)
    => quantity.HasValue ? quantity.Value.ToString("N2") : "Pendiente";

  protected static string GetRecountIssueLabel(string? issueCode)
    => issueCode switch
    {
      PhysicalCountRecountIssueCodes.QuantityMismatch => "Cantidad",
      PhysicalCountRecountIssueCodes.UnitIssue => "Unidad",
      PhysicalCountRecountIssueCodes.WrongMaterial => "Material",
      PhysicalCountRecountIssueCodes.EvidenceMissing => "Evidencia",
      PhysicalCountRecountIssueCodes.ConditionIssue => "Condición",
      PhysicalCountRecountIssueCodes.Other => "Otro",
      _ => string.IsNullOrWhiteSpace(issueCode) ? "Incidencia" : issueCode
    };

  private void ClearSelectedSession()
  {
    _isSessionListVisibleOnMobile = true;
    SelectedSession = null;
    SelectedLine = null;
    LineCapture = new();
    MaterialThumbnailDataUrls = [];
    PendingLineAttachmentBytes = null;
    PendingLineAttachmentName = null;
    PendingLineAttachmentContentType = null;
    _countedQuantityInput = string.Empty;
    _focusCountedQuantityInputPending = false;
    CloseMaterialImageModal();
  }

  private async Task ScrollToTopAsync()
  {
    try
    {
      await Js.InvokeVoidAsync("scrollTo", 0, 0);
    }
    catch (InvalidOperationException)
    {
    }
    catch (JSDisconnectedException)
    {
    }
  }

  private async Task<bool> TryFocusCountedQuantityInputAsync(bool scrollIntoViewOnMobile)
  {
    try
    {
      return await Js.InvokeAsync<bool>(
        "focusAndSelectVisibleTextInput",
        MobileCountedQuantityInputRef,
        CountedQuantityInputRef,
        scrollIntoViewOnMobile);
    }
    catch (InvalidOperationException)
    {
      return false;
    }
    catch (JSDisconnectedException)
    {
      return false;
    }
  }

  private static string FormatCountedQuantity(decimal quantity)
    => quantity.ToString("0.####", QuantityInputCulture);

  private static bool TryParseCountedQuantity(string? value, out decimal result)
  {
    var trimmedValue = value?.Trim();
    if (string.IsNullOrWhiteSpace(trimmedValue))
    {
      result = default;
      return false;
    }

    if (decimal.TryParse(trimmedValue, QuantityInputNumberStyles, QuantityInputCulture, out result) ||
        decimal.TryParse(trimmedValue, QuantityInputNumberStyles, CultureInfo.InvariantCulture, out result) ||
        decimal.TryParse(trimmedValue, QuantityInputNumberStyles, CultureInfo.CurrentCulture, out result))
    {
      return true;
    }

    var normalizedValue = trimmedValue.Replace(',', '.');
    return decimal.TryParse(normalizedValue, QuantityInputNumberStyles, CultureInfo.InvariantCulture, out result);
  }

  private static string BuildDataUrl(string? contentType, byte[] bytes)
  {
    var safeContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
    return FormattableString.Invariant($"data:{safeContentType};base64,{Convert.ToBase64String(bytes)}");
  }

  protected sealed class RecountLineEditor
  {
    public bool IsSelected { get; set; }
    public int LineId { get; set; }
    public string MaterialCode { get; set; } = string.Empty;
    public string MaterialDescription { get; set; } = string.Empty;
    public decimal ExpectedQuantity { get; set; }
    public decimal? CountedQuantity { get; set; }
    public decimal? VarianceQuantity { get; set; }
    public string IssueCode { get; set; } = PhysicalCountRecountIssueCodes.QuantityMismatch;
    public string Reason { get; set; } = string.Empty;
  }
}
