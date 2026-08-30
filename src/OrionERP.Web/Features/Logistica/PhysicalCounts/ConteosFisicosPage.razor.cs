using System.Globalization;
using System.IO;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Locations;
using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Application.Features.Logistica.PhysicalCounts;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Web.Services;
using OrionERP.Web.State;

namespace OrionERP.Web.Features.Logistica.PhysicalCounts;

public partial class ConteosFisicosPage : ComponentBase, IAsyncDisposable
{
  private static readonly CultureInfo QuantityInputCulture = CultureInfo.GetCultureInfo("es-MX");
  private static readonly NumberStyles QuantityInputNumberStyles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands;

  private string _countedQuantityInput = string.Empty;
  private ElementReference CountedQuantityInputRef;
  private ElementReference ScannerVideoElement;
  private bool _focusCountedQuantityInputPending;
  private bool _startScannerPending;
  private int? _appliedQuerySessionId;
  private IJSObjectReference? _scannerModule;
  private DotNetObjectReference<ConteosFisicosPage>? _scannerCallbackReference;

  [Parameter]
  [SupplyParameterFromQuery(Name = "sessionId")]
  public int? QuerySessionId { get; set; }

  [Inject] private IPhysicalCountService PhysicalCountService { get; set; } = default!;
  [Inject] private IMaterialService MaterialService { get; set; } = default!;
  [Inject] private ILocationService LocationService { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;
  [Inject] private IJSRuntime Js { get; set; } = default!;
  [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
  [Inject] private ICurrentCompanyContext RfcState { get; set; } = default!;

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
  protected bool CanManageCounts { get; set; }
  protected bool ShowCreateModal { get; set; }
  protected bool ShowCancelModal { get; set; }
  protected bool ShowRecountPanel { get; set; }
  protected bool ShowCompletionReview { get; set; }
  protected bool ShowFullAuditLog { get; set; }
  protected bool ShowMobileMaterialList { get; set; }
  protected bool ShowBarcodeScanner { get; set; }
  protected bool IsStartingScanner { get; set; }
  protected string? ScannerMessage { get; set; }
  protected string CancelReason { get; set; } = string.Empty;
  protected string MaterialSearch { get; set; } = string.Empty;
  protected LineFilterMode CaptureFilter { get; set; } = LineFilterMode.Pending;
  protected LineFilterMode ReviewFilter { get; set; } = LineFilterMode.Variance;
  protected List<RecountLineEditor> RecountPlanLines { get; set; } = [];
  protected bool ShowMaterialImageModal { get; set; }
  protected bool IsLoadingMaterialImage { get; set; }
  protected byte[]? PendingLineAttachmentBytes { get; set; }
  protected string? PendingLineAttachmentName { get; set; }
  protected string? PendingLineAttachmentContentType { get; set; }
  protected string? MaterialImageModalTitle { get; set; }
  protected string? MaterialImageModalDataUrl { get; set; }

  protected IReadOnlyList<FilterOption> CaptureFilters { get; } =
  [
    new(LineFilterMode.All, "Todos"),
    new(LineFilterMode.Pending, "Pendientes"),
    new(LineFilterMode.Counted, "Contados"),
    new(LineFilterMode.Variance, "Diferencias")
  ];

  protected IReadOnlyList<FilterOption> ReviewFilters { get; } =
  [
    new(LineFilterMode.All, "Todos"),
    new(LineFilterMode.Variance, "Diferencias"),
    new(LineFilterMode.Flagged, "Incidencias")
  ];

  protected IReadOnlyList<PhysicalCountSessionSummaryDto> CountableSessions => Sessions
    .Where(session => IsDraftStatus(session.Status) || IsRecountStatus(session.Status))
    .OrderByDescending(session => IsRecountStatus(session.Status))
    .ThenByDescending(session => session.CreatedAt)
    .ToList();

  protected IReadOnlyList<PhysicalCountSessionSummaryDto> ReviewSessions => Sessions
    .Where(session => IsSubmittedStatus(session.Status) || IsApprovedStatus(session.Status))
    .OrderByDescending(session => session.CreatedAt)
    .ToList();

  protected IReadOnlyList<PhysicalCountSessionSummaryDto> HistoricalSessions => Sessions
    .Where(session => IsPostedStatus(session.Status) || IsCanceledStatus(session.Status))
    .OrderByDescending(session => session.CreatedAt)
    .ToList();

  protected bool CanSubmit => SelectedSession is not null && (IsDraftStatus(SelectedSession.Status) || IsRecountStatus(SelectedSession.Status));
  protected bool CanCancel => CanManageCounts && SelectedSession is not null && IsCancelableStatus(SelectedSession.Status);
  protected bool CanRequestRecount => CanManageCounts && SelectedSession is not null && (IsSubmittedStatus(SelectedSession.Status) || IsApprovedStatus(SelectedSession.Status));
  protected bool CanApprove => CanManageCounts && SelectedSession is not null && IsSubmittedStatus(SelectedSession.Status);
  protected bool CanPost => CanManageCounts && SelectedSession is not null && IsApprovedStatus(SelectedSession.Status);
  protected bool CanCaptureLine => CanSubmit && SelectedLine is not null;
  protected bool CameraScanningAllowed => true;
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

  protected IReadOnlyList<PhysicalCountLineDto> CaptureSessionLines
  {
    get
    {
      if (SelectedSession is null)
      {
        return Array.Empty<PhysicalCountLineDto>();
      }

      return IsRecountStatus(SelectedSession.Status)
        ? SelectedSession.Lines.Where(HasRecountIssue).ToList()
        : SelectedSession.Lines;
    }
  }

  protected IReadOnlyList<PhysicalCountLineDto> FilteredSessionLines => CaptureSessionLines
    .Where(MatchesMaterialSearch)
    .Where(line => MatchesFilter(line, CaptureFilter))
    .OrderBy(line => line.CountedQuantity.HasValue)
    .ThenByDescending(HasRecountIssue)
    .ThenBy(GetMaterialTitle, StringComparer.CurrentCultureIgnoreCase)
    .ToList();

  protected IReadOnlyList<PhysicalCountLineDto> FilteredReviewLines => (SelectedSession?.Lines ?? Array.Empty<PhysicalCountLineDto>())
    .Where(MatchesMaterialSearch)
    .Where(line => MatchesFilter(line, ReviewFilter))
    .OrderByDescending(line => Math.Abs(line.VarianceQuantity ?? 0m))
    .ThenByDescending(line => line.IsMissing || line.IsDamaged)
    .ThenBy(GetMaterialTitle, StringComparer.CurrentCultureIgnoreCase)
    .ToList();

  protected int CaptureTotalLineCount => CaptureSessionLines.Count;
  protected int CountedLineCount => CaptureSessionLines.Count(line => line.CountedQuantity.HasValue);
  protected int PendingLineCount => CaptureSessionLines.Count(line => !line.CountedQuantity.HasValue);
  protected int VarianceLineCount => SelectedSession?.Lines.Count(line => line.VarianceQuantity is not null and not 0m) ?? 0;
  protected int MatchingLineCount => SelectedSession?.Lines.Count(line => line.CountedQuantity.HasValue && line.VarianceQuantity == 0m) ?? 0;
  protected int FlaggedLineCount => SelectedSession?.Lines.Count(line => line.IsMissing || line.IsDamaged || !string.IsNullOrWhiteSpace(line.Notes)) ?? 0;
  protected int SelectedRecountLineCount => RecountPlanLines.Count(line => line.IsSelected);
  protected int SessionProgressPercentage => GetProgressPercentage(CountedLineCount, CaptureTotalLineCount);
  protected IReadOnlyList<PhysicalCountAuditEventDto> AuditEvents => SelectedSession is null
    ? Array.Empty<PhysicalCountAuditEventDto>()
    : SelectedSession.AuditEvents
      .OrderByDescending(auditEvent => auditEvent.OccurredAt)
      .ToList();
  protected IReadOnlyList<PhysicalCountAuditEventDto> VisibleAuditEvents => ShowFullAuditLog
    ? AuditEvents
    : AuditEvents.Take(12).ToList();
  protected PhysicalCountAuditEventDto? LastAuditEvent => AuditEvents.FirstOrDefault();
  protected PhysicalCountAuditEventDto? FirstCaptureEvent => AuditEvents
    .Where(auditEvent => string.Equals(auditEvent.EventType, PhysicalCountAuditEventTypes.LineCounted, StringComparison.Ordinal))
    .OrderBy(auditEvent => auditEvent.OccurredAt)
    .FirstOrDefault();
  protected int AuditCounterCount => AuditEvents
    .Where(auditEvent => string.Equals(auditEvent.EventType, PhysicalCountAuditEventTypes.LineCounted, StringComparison.Ordinal))
    .Select(auditEvent => auditEvent.PerformedBy?.Trim())
    .Where(actor => !string.IsNullOrWhiteSpace(actor))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .Count();
  protected int AuditCaptureCount => AuditEvents.Count(auditEvent =>
    string.Equals(auditEvent.EventType, PhysicalCountAuditEventTypes.LineCounted, StringComparison.Ordinal));
  protected int CurrentLinePosition => SelectedLine is null ? 0 : Math.Max(1, CaptureSessionLines.ToList().FindIndex(line => line.Id == SelectedLine.Id) + 1);
  protected bool CanSelectPreviousLine => SelectedLine is not null && CaptureSessionLines.ToList().FindIndex(line => line.Id == SelectedLine.Id) > 0;
  protected bool ShouldOpenLineDetails => SelectedLine is not null
    && (SelectedLine.IsMissing
      || SelectedLine.IsDamaged
      || !string.IsNullOrWhiteSpace(SelectedLine.Notes)
      || SelectedLine.Attachments.Count > 0);

  protected string CurrentVarianceText => SelectedLine is null
    ? "0.00"
    : TryParseCountedQuantity(CountedQuantityInput, out var countedQuantity)
      ? (countedQuantity - SelectedLine.ExpectedQuantity).ToString("N2")
      : "Pendiente";

  protected override async Task OnInitializedAsync()
  {
    CurrentUserName = await ResolveCurrentUserAsync();
    LocationOptions = (await LocationService.GetLocationLookupAsync(inventoryOnly: true)).ToList();
    await CargarSesionesAsync();
    await ApplyQuerySessionSelectionAsync();
  }

  protected override async Task OnParametersSetAsync()
  {
    if (Sessions.Count > 0)
    {
      await ApplyQuerySessionSelectionAsync();
    }
  }

  protected override async Task OnAfterRenderAsync(bool firstRender)
  {
    if (_startScannerPending && ShowBarcodeScanner)
    {
      _startScannerPending = false;
      await StartScannerAsync();
    }

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
      UiMessages.ShowError($"No se pudieron cargar los conteos. {ex.Message}");
    }
    finally
    {
      IsLoadingSessions = false;
      StateHasChanged();
    }
  }

  protected void AbrirCreacion()
  {
    if (!CanManageCounts)
    {
      return;
    }

    SessionCreateRequest = new();
    ShowCreateModal = true;
  }

  protected void CerrarCreacion()
  {
    if (IsCreatingSession)
    {
      return;
    }

    ShowCreateModal = false;
    SessionCreateRequest = new();
  }

  protected async Task CrearSesionAsync()
  {
    if (!CanManageCounts)
    {
      return;
    }

    if (SessionCreateRequest.LocationId <= 0)
    {
      UiMessages.ShowWarning("Selecciona la ubicación que se va a contar.");
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

      ShowCreateModal = false;
      SessionCreateRequest = new();
      UiMessages.ShowSuccess("Conteo creado. Ya puedes comenzar a capturar materiales.");
      await CargarSesionesAsync();
      if (result.EntityId.HasValue)
      {
        await SeleccionarSesionAsync(result.EntityId.Value);
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo crear el conteo. {ex.Message}");
    }
    finally
    {
      IsCreatingSession = false;
    }
  }

  protected async Task SeleccionarSesionAsync(int sessionId, bool includeHistorical = false)
  {
    try
    {
      var session = await PhysicalCountService.GetSessionAsync(sessionId);
      if (session is null || ((IsPostedStatus(session.Status) || IsCanceledStatus(session.Status)) && !CanManageCounts))
      {
        ClearSelectedSession();
        return;
      }

      await StopScannerAsync();
      SelectedSession = session;
      SelectedLine = null;
      MaterialSearch = string.Empty;
      CaptureFilter = LineFilterMode.Pending;
      ReviewFilter = session.Lines.Any(line => line.VarianceQuantity is not null and not 0m)
        ? LineFilterMode.Variance
        : LineFilterMode.All;
      ShowCompletionReview = false;
      ShowFullAuditLog = false;
      ShowMobileMaterialList = false;
      ShowRecountPanel = false;
      RecountPlanLines = [];
      MaterialThumbnailDataUrls = [];
      CloseMaterialImageModal();
      await CargarMiniaturasMaterialesAsync();

      if (CanSubmit)
      {
        var preferredLine = CaptureSessionLines.FirstOrDefault(line => !line.CountedQuantity.HasValue);
        if (preferredLine is null)
        {
          ShowCompletionReview = true;
        }
        else
        {
          SeleccionarLinea(preferredLine);
        }
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo abrir el conteo seleccionado. {ex.Message}");
    }
  }

  protected async Task ActualizarSesionActualAsync()
  {
    if (SelectedSession is null)
    {
      return;
    }

    var sessionId = SelectedSession.Id;
    var selectedLine = SelectedLine;
    var pendingCapture = LineCapture;
    var pendingInput = CountedQuantityInput;
    var pendingAttachmentBytes = PendingLineAttachmentBytes;
    var pendingAttachmentName = PendingLineAttachmentName;
    var pendingAttachmentType = PendingLineAttachmentContentType;

    IsLoadingSessions = true;
    try
    {
      var session = await PhysicalCountService.GetSessionAsync(sessionId);
      if (session is null)
      {
        ClearSelectedSession();
        return;
      }

      SelectedSession = session;
      Sessions = (await PhysicalCountService.GetSessionsAsync()).ToList();
      if (selectedLine is null)
      {
        return;
      }

      var freshLine = session.Lines.FirstOrDefault(line => line.Id == selectedLine.Id);
      if (freshLine is null || freshLine.CapturedAt != selectedLine.CapturedAt)
      {
        UiMessages.ShowWarning("Otro empleado actualizó el material que tenías abierto. Tu captura local no se sobrescribió.");
        var next = CaptureSessionLines.FirstOrDefault(line => !line.CountedQuantity.HasValue);
        if (next is null)
        {
          SelectedLine = null;
          ShowCompletionReview = true;
        }
        else
        {
          SeleccionarLinea(next);
        }
        return;
      }

      SelectedLine = freshLine;
      LineCapture = pendingCapture;
      LineCapture.SessionId = session.Id;
      LineCapture.LineId = freshLine.Id;
      CountedQuantityInput = pendingInput;
      PendingLineAttachmentBytes = pendingAttachmentBytes;
      PendingLineAttachmentName = pendingAttachmentName;
      PendingLineAttachmentContentType = pendingAttachmentType;
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo actualizar el conteo. {ex.Message}");
    }
    finally
    {
      IsLoadingSessions = false;
      StateHasChanged();
    }
  }

  protected async Task VolverASesionesAsync()
  {
    await StopScannerAsync();
    ClearSelectedSession();
    await ScrollToTopAsync();
  }

  protected void SeleccionarLinea(PhysicalCountLineDto line)
  {
    SelectedLine = line;
    ShowCompletionReview = false;
    LineCapture = new PhysicalCountLineCaptureRequest
    {
      SessionId = SelectedSession?.Id ?? 0,
      LineId = line.Id,
      ExpectedCapturedAt = line.CapturedAt,
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

  protected void EditarLinea(PhysicalCountLineDto line)
  {
    CaptureFilter = LineFilterMode.All;
    SeleccionarLinea(line);
  }

  protected void SeleccionarLineaDesdeLista(PhysicalCountLineDto line)
  {
    ShowMobileMaterialList = false;
    SeleccionarLinea(line);
  }

  protected void SeleccionarLineaAnterior()
  {
    if (SelectedLine is null)
    {
      return;
    }

    var lines = CaptureSessionLines.ToList();
    var currentIndex = lines.FindIndex(line => line.Id == SelectedLine.Id);
    if (currentIndex > 0)
    {
      SeleccionarLinea(lines[currentIndex - 1]);
    }
  }

  protected void OmitirLineaActual()
  {
    if (SelectedLine is null)
    {
      return;
    }

    var nextLine = FindNextUncountedLine(CaptureSessionLines, SelectedLine.Id);
    if (nextLine is null)
    {
      SelectedLine = null;
      ShowCompletionReview = true;
      return;
    }

    SeleccionarLinea(nextLine);
  }

  protected void ContinuarPendientes()
  {
    var pending = CaptureSessionLines.FirstOrDefault(line => !line.CountedQuantity.HasValue);
    if (pending is not null)
    {
      CaptureFilter = LineFilterMode.Pending;
      SeleccionarLinea(pending);
    }
  }

  protected void MostrarRevisionFinal()
  {
    SelectedLine = null;
    ShowCompletionReview = true;
  }

  protected void MostrarRevisionDesdeLista()
  {
    ShowMobileMaterialList = false;
    MostrarRevisionFinal();
  }

  protected void AbrirListaMovil() => ShowMobileMaterialList = true;
  protected void CerrarListaMovil() => ShowMobileMaterialList = false;
  protected void SetCaptureFilter(LineFilterMode filter) => CaptureFilter = filter;
  protected void SetReviewFilter(LineFilterMode filter) => ReviewFilter = filter;

  protected void UpdateTotalFromLots()
  {
    if (LineCapture.Lots.Count == 0)
    {
      return;
    }

    LineCapture.CountedQuantity = LineCapture.Lots.Sum(lot => lot.CountedQuantity ?? 0m);
    UpdateCountedQuantityInputFromCapture();
  }

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

  protected async Task GuardarLineaAsync(bool advanceToNextUncountedLine)
  {
    if (!CanCaptureLine || SelectedLine is null || SelectedSession is null)
    {
      return;
    }

    if (!TryParseCountedQuantity(CountedQuantityInput, out var countedQuantity) || countedQuantity < 0m)
    {
      UiMessages.ShowWarning("Captura una cantidad válida, igual o mayor que cero.");
      QueueCountedQuantityFocus();
      return;
    }

    var sessionId = SelectedSession.Id;
    var savedLineId = SelectedLine.Id;
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
        if (result.Message.Contains("Otro empleado", StringComparison.OrdinalIgnoreCase))
        {
          UiMessages.ShowWarning(result.Message);
          await ReloadSessionAfterSaveAsync(sessionId, savedLineId, selectNextPending: true);
        }
        else
        {
          UiMessages.ShowError(result.Message);
        }
        return;
      }

      await ReloadSessionAfterSaveAsync(sessionId, savedLineId, advanceToNextUncountedLine);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo guardar el material. {ex.Message}");
    }
    finally
    {
      IsSavingLine = false;
    }
  }

  protected void OnCountedQuantityInput(ChangeEventArgs args)
    => CountedQuantityInput = args.Value?.ToString() ?? string.Empty;

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
    => _ = await TryFocusCountedQuantityInputAsync(scrollIntoViewOnMobile: false);

  protected async Task HandleMobileCountedQuantityKeyDownAsync(KeyboardEventArgs args)
  {
    if (string.Equals(args.Key, "Enter", StringComparison.Ordinal) && CanCaptureLine && !IsSavingLine)
    {
      await GuardarLineaMovilAsync();
    }
  }

  protected async Task EnviarSesionAsync()
  {
    if (!CanSubmit || SelectedSession is null)
    {
      return;
    }

    if (PendingLineCount > 0)
    {
      UiMessages.ShowWarning($"Aún faltan {PendingLineCount} materiales por contar.");
      return;
    }

    if (!await ConfirmAsync("¿Enviar este conteo a revisión? Las cantidades quedarán bloqueadas hasta que Logística solicite un reconteo."))
    {
      return;
    }

    await EjecutarSesionAsync(() => PhysicalCountService.SubmitSessionAsync(SelectedSession.Id, CurrentUserName));
  }

  protected async Task AprobarSesionAsync()
  {
    if (!CanApprove || SelectedSession is null)
    {
      return;
    }

    var message = VarianceLineCount == 0
      ? "¿Aprobar este conteo sin diferencias?"
      : $"¿Aprobar este conteo con {VarianceLineCount} diferencia(s)? Todavía no se modificará el inventario.";
    if (!await ConfirmAsync(message))
    {
      return;
    }

    await EjecutarSesionAsync(() => PhysicalCountService.ApproveSessionAsync(SelectedSession.Id, CurrentUserName));
  }

  protected async Task ContabilizarSesionAsync()
  {
    if (!CanPost || SelectedSession is null)
    {
      return;
    }

    if (!await ConfirmAsync($"¿Aplicar este conteo al inventario? Se actualizarán las existencias de {SelectedSession.Lines.Count} materiales y esta acción no podrá deshacerse desde esta pantalla."))
    {
      return;
    }

    await EjecutarSesionAsync(() => PhysicalCountService.PostSessionAsync(SelectedSession.Id, CurrentUserName));
  }

  protected Task CancelarSesionAsync()
  {
    if (!CanCancel || SelectedSession is null || IsMutatingSession)
    {
      return Task.CompletedTask;
    }

    CancelReason = string.Empty;
    ShowCancelModal = true;
    return Task.CompletedTask;
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
      UiMessages.ShowWarning("Explica por qué se cancela el conteo.");
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

      UiMessages.ShowSuccess("Conteo cancelado.");
      ShowCancelModal = false;
      CancelReason = string.Empty;
      await CargarSesionesAsync();
      await SeleccionarSesionAsync(sessionId, includeHistorical: true);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cancelar el conteo. {ex.Message}");
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
        IsSelected = line.VarianceQuantity is not null and not 0m,
        LineId = line.Id,
        MaterialCode = line.MaterialCode,
        MaterialDescription = GetMaterialTitle(line),
        ExpectedQuantity = line.ExpectedQuantity,
        CountedQuantity = line.CountedQuantity,
        VarianceQuantity = line.VarianceQuantity,
        IssueCode = PhysicalCountRecountIssueCodes.QuantityMismatch
      })
      .ToList();
    ReviewFilter = VarianceLineCount > 0 ? LineFilterMode.Variance : LineFilterMode.All;
    ShowRecountPanel = true;
  }

  protected void CerrarReconteo()
  {
    if (IsRequestingRecount)
    {
      return;
    }

    ShowRecountPanel = false;
    RecountPlanLines = [];
  }

  protected RecountLineEditor? GetRecountEditor(int lineId)
    => RecountPlanLines.FirstOrDefault(line => line.LineId == lineId);

  protected async Task EnviarAReconteoAsync()
  {
    if (!CanRequestRecount || SelectedSession is null || IsMutatingSession)
    {
      return;
    }

    var selectedLines = RecountPlanLines.Where(line => line.IsSelected).ToList();
    if (selectedLines.Count == 0)
    {
      UiMessages.ShowWarning("Selecciona al menos un material para recontar.");
      return;
    }

    if (selectedLines.Any(line => string.IsNullOrWhiteSpace(line.IssueCode)
      || !PhysicalCountRecountIssueCodes.All.Contains(line.IssueCode)
      || string.IsNullOrWhiteSpace(line.Reason)))
    {
      UiMessages.ShowWarning("Cada material seleccionado necesita un problema y una explicación.");
      return;
    }

    if (!await ConfirmAsync($"¿Enviar {selectedLines.Count} material(es) a reconteo? Sus cantidades se borrarán para que el equipo las capture otra vez."))
    {
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
        Lines = selectedLines.Select(line => new PhysicalCountRecountLineRequest
        {
          LineId = line.LineId,
          IssueCode = line.IssueCode,
          Reason = line.Reason
        }).ToList()
      });
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess("Reconteo solicitado. Los materiales ya están disponibles para el equipo.");
      ShowRecountPanel = false;
      RecountPlanLines = [];
      await CargarSesionesAsync();
      await SeleccionarSesionAsync(sessionId);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo solicitar el reconteo. {ex.Message}");
    }
    finally
    {
      IsRequestingRecount = false;
      IsMutatingSession = false;
    }
  }

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
    if (SelectedLine?.Id != line.Id && CanSubmit)
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
      var image = await MaterialService.GetMaterialImageAsync(RfcState.RequireRfc(), line.MaterialId);
      if (image is null)
      {
        if (MaterialImageModalDataUrl is null)
        {
          UiMessages.ShowWarning("El material no tiene imagen disponible.");
        }
        return;
      }

      MaterialImageModalDataUrl = BuildDataUrl(image.ContentType, image.Bytes);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cargar la imagen. {ex.Message}");
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

  protected Task AbrirEscanerDesdeListaAsync()
  {
    ShowMobileMaterialList = false;
    return AbrirEscanerAsync();
  }

  protected Task AbrirEscanerAsync()
  {
    if (!CameraScanningAllowed)
    {
      UiMessages.ShowWarning("La cámara está deshabilitada en el entorno de capacitación. Usa la búsqueda manual.");
      return Task.CompletedTask;
    }

    ScannerMessage = null;
    ShowBarcodeScanner = true;
    IsStartingScanner = true;
    _startScannerPending = true;
    return Task.CompletedTask;
  }

  protected async Task CerrarEscanerAsync()
  {
    await StopScannerAsync();
    ShowBarcodeScanner = false;
    IsStartingScanner = false;
    ScannerMessage = null;
    _startScannerPending = false;
  }

  [JSInvokable]
  public async Task<bool> OnBarcodeDetected(string? rawValue)
  {
    var barcode = rawValue?.Trim();
    if (string.IsNullOrWhiteSpace(barcode) || SelectedSession is null)
    {
      return false;
    }

    var matches = CaptureSessionLines
      .Where(line => string.Equals(line.Barcode?.Trim(), barcode, StringComparison.OrdinalIgnoreCase))
      .ToList();

    if (matches.Count == 1)
    {
      await InvokeAsync(async () =>
      {
        await CerrarEscanerAsync();
        CaptureFilter = LineFilterMode.All;
        SeleccionarLinea(matches[0]);
        StateHasChanged();
      });
      return true;
    }

    await InvokeAsync(() =>
    {
      ScannerMessage = matches.Count > 1
        ? $"El código {barcode} pertenece a más de un material. Usa la búsqueda manual."
        : $"No encontramos el código {barcode} en este conteo. Intenta otra vez.";
      StateHasChanged();
    });
    return false;
  }

  protected static string GetMaterialTitle(PhysicalCountLineDto? line)
    => line is null
      ? string.Empty
      : string.IsNullOrWhiteSpace(line.MaterialDescription)
        ? line.MaterialCode
        : line.MaterialDescription;

  protected string GetMaterialListItemClass(PhysicalCountLineDto line)
  {
    var classes = new List<string>();
    if (SelectedLine?.Id == line.Id) classes.Add("is-selected");
    if (line.CountedQuantity.HasValue) classes.Add("is-counted");
    if (line.VarianceQuantity is not null and not 0m) classes.Add("has-variance");
    if (HasRecountIssue(line)) classes.Add("has-recount");
    return string.Join(" ", classes);
  }

  protected static string GetLineStateIcon(PhysicalCountLineDto line)
    => !line.CountedQuantity.HasValue ? (HasRecountIssue(line) ? "↻" : "•") : "✓";

  protected static string GetMaterialListSubtitle(PhysicalCountLineDto line)
  {
    var unit = GetCountUnitLabel(line.BaseUnitName);
    if (!line.CountedQuantity.HasValue)
    {
      return HasRecountIssue(line) ? $"Recontar · {unit}" : $"Pendiente · {unit}";
    }

    return line.VarianceQuantity is not null and not 0m
      ? $"Diferencia {FormatSignedQuantity(line.VarianceQuantity)} · {unit}"
      : $"Contado · {unit}";
  }

  protected IReadOnlyList<PhysicalCountLineDto> GetOperatorReviewLines()
    => CaptureSessionLines
      .OrderBy(line => line.CountedQuantity.HasValue)
      .ThenByDescending(line => Math.Abs(line.VarianceQuantity ?? 0m))
      .ThenBy(GetMaterialTitle, StringComparer.CurrentCultureIgnoreCase)
      .ToList();

  protected string GetCurrentVarianceClass()
  {
    if (!TryParseCountedQuantity(CountedQuantityInput, out var counted) || SelectedLine is null)
    {
      return "is-pending";
    }

    return counted == SelectedLine.ExpectedQuantity ? "is-matching" : "is-different";
  }

  protected static int GetProgressPercentage(int counted, int total)
    => total <= 0 ? 0 : (int)Math.Round(counted * 100m / total, MidpointRounding.AwayFromZero);

  protected static int GetSessionProgressTotal(PhysicalCountSessionSummaryDto session)
    => IsRecountStatus(session.Status) && session.RecountLineCount > 0
      ? session.RecountLineCount
      : session.LineCount;

  protected static string GetCountUnitLabel(string? unitName)
    => string.IsNullOrWhiteSpace(unitName) ? "unidad sin configurar" : unitName.Trim();

  protected static string FormatOptionalQuantity(decimal? quantity)
    => quantity.HasValue ? quantity.Value.ToString("N2") : "Pendiente";

  protected static string FormatSignedQuantity(decimal? quantity)
    => quantity.HasValue ? $"{quantity.Value:+0.##;-0.##;0}" : "Pendiente";

  protected static bool HasRecountIssue(PhysicalCountLineDto line)
    => !string.IsNullOrWhiteSpace(line.RecountIssueCode) || !string.IsNullOrWhiteSpace(line.RecountReason);

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

  protected static string GetSessionStatusLabel(string? status)
    => NormalizeSessionStatus(status) switch
    {
      "draft" => "En conteo",
      "submitted" => "En revisión",
      "approved" => "Aprobado",
      "recount" => "Reconteo solicitado",
      "posted" => "Aplicado al inventario",
      "canceled" => "Cancelado",
      _ => string.IsNullOrWhiteSpace(status) ? "Sin estado" : status.Trim()
    };

  protected string GetSessionStatusBadgeClass(string? status)
    => $"conteos-status-badge {GetSessionStatusClass(status)}";

  protected static string GetSessionStatusClass(string? status)
    => NormalizeSessionStatus(status) switch
    {
      "draft" => "status-counting",
      "submitted" => "status-review",
      "approved" => "status-approved",
      "recount" => "status-recount",
      "posted" => "status-posted",
      "canceled" => "status-canceled",
      _ => "status-unknown"
    };

  protected string GetLifecycleStepClass(int step)
  {
    if (SelectedSession is null)
    {
      return string.Empty;
    }

    if (IsCanceledStatus(SelectedSession.Status))
    {
      return "is-canceled";
    }

    var currentStep = GetLifecycleStep(SelectedSession.Status);
    return step < currentStep ? "is-complete" : step == currentStep ? "is-current" : string.Empty;
  }

  protected static string GetLifecycleStepLabel(int step)
    => step switch
    {
      1 => "Contar",
      2 => "Revisar",
      3 => "Aprobar",
      4 => "Aplicar",
      _ => string.Empty
    };

  protected string GetCurrentStatusGuidance()
    => NormalizeSessionStatus(SelectedSession?.Status) switch
    {
      "draft" => "Paso actual: captura cada material. Al terminar, envía el conteo a revisión.",
      "recount" => "Paso actual: vuelve a contar únicamente los materiales solicitados y envíalos de nuevo.",
      "submitted" => CanManageCounts ? "Paso actual: revisa las diferencias, solicita un reconteo o aprueba el resultado." : "El equipo de Logística está revisando este conteo.",
      "approved" => CanManageCounts ? "Paso actual: el conteo está aprobado y listo para aplicarse al inventario." : "El conteo fue aprobado y está pendiente de aplicarse al inventario.",
      "posted" => "Proceso terminado: las cantidades ya fueron aplicadas al inventario.",
      "canceled" => "Este conteo fue cancelado y permanece disponible únicamente para consulta.",
      _ => "Consulta el estado del conteo."
    };

  protected static string GetAuditEventTitle(PhysicalCountAuditEventDto auditEvent)
    => auditEvent.EventType switch
    {
      PhysicalCountAuditEventTypes.SessionStarted => "Sesión de conteo creada",
      PhysicalCountAuditEventTypes.LineCounted => $"Material contado: {GetAuditMaterialTitle(auditEvent)}",
      PhysicalCountAuditEventTypes.EvidenceAdded => $"Evidencia agregada: {GetAuditMaterialTitle(auditEvent)}",
      PhysicalCountAuditEventTypes.Submitted => "Conteo enviado a revisión",
      PhysicalCountAuditEventTypes.RecountRequested => "Reconteo solicitado",
      PhysicalCountAuditEventTypes.RecountCompleted => "Reconteo terminado y enviado",
      PhysicalCountAuditEventTypes.Approved => "Conteo aprobado",
      PhysicalCountAuditEventTypes.Posted => "Conteo aplicado al inventario",
      PhysicalCountAuditEventTypes.Canceled => "Conteo cancelado",
      _ => "Actividad del conteo"
    };

  protected static string GetAuditEventDescription(PhysicalCountAuditEventDto auditEvent)
  {
    if (string.Equals(auditEvent.EventType, PhysicalCountAuditEventTypes.LineCounted, StringComparison.Ordinal))
    {
      var description = auditEvent.CountedQuantity.HasValue
        ? $"Contado {auditEvent.CountedQuantity.Value:N2}"
        : "Cantidad capturada";
      if (auditEvent.ExpectedQuantity.HasValue)
      {
        description += $" · esperado {auditEvent.ExpectedQuantity.Value:N2}";
      }

      if (!string.IsNullOrWhiteSpace(auditEvent.Details))
      {
        description += $" · Nota: {auditEvent.Details.Trim()}";
      }

      return description;
    }

    if (!string.IsNullOrWhiteSpace(auditEvent.Details))
    {
      return auditEvent.Details.Trim();
    }

    return auditEvent.EventType switch
    {
      PhysicalCountAuditEventTypes.SessionStarted => "Se creó la sesión y quedó lista para comenzar a contar.",
      PhysicalCountAuditEventTypes.EvidenceAdded => "Se adjuntó evidencia al material.",
      PhysicalCountAuditEventTypes.Submitted => "Las cantidades quedaron bloqueadas para revisión de Logística.",
      PhysicalCountAuditEventTypes.RecountRequested => "Logística pidió verificar nuevamente uno o más materiales.",
      PhysicalCountAuditEventTypes.RecountCompleted => "El equipo terminó los materiales solicitados para reconteo.",
      PhysicalCountAuditEventTypes.Approved => "Logística aprobó las cantidades capturadas.",
      PhysicalCountAuditEventTypes.Posted => "Las cantidades aprobadas actualizaron las existencias.",
      PhysicalCountAuditEventTypes.Canceled => "La sesión quedó disponible únicamente para consulta.",
      _ => "Se registró una actividad en esta sesión."
    };
  }

  protected static string GetAuditEventClass(string? eventType)
    => eventType switch
    {
      PhysicalCountAuditEventTypes.LineCounted => "is-capture",
      PhysicalCountAuditEventTypes.EvidenceAdded => "is-evidence",
      PhysicalCountAuditEventTypes.RecountRequested or PhysicalCountAuditEventTypes.RecountCompleted => "is-recount",
      PhysicalCountAuditEventTypes.Approved or PhysicalCountAuditEventTypes.Posted => "is-success",
      PhysicalCountAuditEventTypes.Canceled => "is-canceled",
      _ => "is-status"
    };

  protected static string GetAuditActor(string? actor)
    => string.IsNullOrWhiteSpace(actor) ? "Usuario no registrado" : actor.Trim();

  protected static string FormatAuditMoment(DateTime value)
    => GetLocalAuditMoment(value).ToString("dd MMM yyyy · HH:mm", QuantityInputCulture);

  protected static string FormatAuditDay(DateTime value)
    => GetLocalAuditMoment(value).ToString("dd MMM yyyy", QuantityInputCulture);

  protected static string FormatAuditTime(DateTime value)
    => GetLocalAuditMoment(value).ToString("HH:mm:ss", QuantityInputCulture);

  protected static string FormatAuditMachineTime(DateTime value)
  {
    var utcValue = value.Kind switch
    {
      DateTimeKind.Utc => value,
      DateTimeKind.Local => value.ToUniversalTime(),
      _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
    return utcValue.ToString("O", CultureInfo.InvariantCulture);
  }

  private static string GetAuditMaterialTitle(PhysicalCountAuditEventDto auditEvent)
  {
    var description = auditEvent.MaterialDescription?.Trim();
    var code = auditEvent.MaterialCode?.Trim();
    if (!string.IsNullOrWhiteSpace(description) && !string.IsNullOrWhiteSpace(code))
    {
      return $"{description} ({code})";
    }

    return !string.IsNullOrWhiteSpace(description)
      ? description
      : !string.IsNullOrWhiteSpace(code) ? code : "material";
  }

  private static DateTime GetLocalAuditMoment(DateTime value)
  {
    if (value.Kind == DateTimeKind.Local)
    {
      return value;
    }

    var utcValue = value.Kind == DateTimeKind.Utc
      ? value
      : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    return utcValue.ToLocalTime();
  }

  protected string GetReviewHeading()
    => NormalizeSessionStatus(SelectedSession?.Status) switch
    {
      "submitted" => CanManageCounts ? "Revisa antes de aprobar" : "Conteo enviado correctamente",
      "approved" => CanManageCounts ? "Listo para aplicar al inventario" : "Conteo aprobado",
      "posted" => "Conteo aplicado al inventario",
      "canceled" => "Conteo cancelado",
      _ => "Resumen del conteo"
    };

  protected string GetReviewDescription()
    => NormalizeSessionStatus(SelectedSession?.Status) switch
    {
      "submitted" => CanManageCounts ? "Las diferencias aparecen primero para que puedas revisarlas rápidamente." : "Ya no necesitas hacer nada. Logística decidirá si se aprueba o requiere un reconteo.",
      "approved" => CanManageCounts ? "Confirma el resumen antes de actualizar las existencias." : "Logística aprobó las cantidades capturadas.",
      "posted" => "Este resultado ya modificó las existencias y no se puede editar.",
      "canceled" => string.IsNullOrWhiteSpace(SelectedSession?.CancelReason) ? "Este conteo ya no puede continuar." : $"Razón: {SelectedSession.CancelReason}",
      _ => "Consulta las cantidades capturadas."
    };

  protected bool IsLineCapturedByCurrentUser(PhysicalCountLineDto line)
    => !string.IsNullOrWhiteSpace(CurrentUserName)
      && line.CapturedAt.HasValue
      && !string.IsNullOrWhiteSpace(line.CapturedBy)
      && string.Equals(line.CapturedBy.Trim(), CurrentUserName.Trim(), StringComparison.OrdinalIgnoreCase);

  protected string? GetMaterialThumbnailDataUrl(int materialId)
    => TryGetMaterialThumbnailDataUrl(materialId, out var dataUrl) ? dataUrl : null;

  private async Task ReloadSessionAfterSaveAsync(int sessionId, int savedLineId, bool selectNextPending)
  {
    var session = await PhysicalCountService.GetSessionAsync(sessionId);
    if (session is null)
    {
      ClearSelectedSession();
      return;
    }

    SelectedSession = session;
    Sessions = (await PhysicalCountService.GetSessionsAsync()).ToList();
    var lines = CaptureSessionLines;
    var nextLine = selectNextPending ? FindNextUncountedLine(lines, savedLineId) : null;
    nextLine ??= !selectNextPending ? lines.FirstOrDefault(line => line.Id == savedLineId) : null;
    if (nextLine is not null)
    {
      SeleccionarLinea(nextLine);
      return;
    }

    SelectedLine = null;
    ShowCompletionReview = true;
    PendingLineAttachmentBytes = null;
    PendingLineAttachmentName = null;
    PendingLineAttachmentContentType = null;
    _countedQuantityInput = string.Empty;
    await ScrollToTopAsync();
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
      await SeleccionarSesionAsync(sessionId, includeHistorical: true);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo actualizar el conteo. {ex.Message}");
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
      return false;
    }
  }

  private async Task<string> ResolveCurrentUserAsync()
  {
    var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
    var user = authState.User;
    CanManageCounts = user.IsInRole("Administrador") || user.IsInRole("Logistica");
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
      var thumbnails = await MaterialService.GetMaterialThumbnailsAsync(RfcState.RequireRfc(), SelectedSession.Lines.Select(line => line.MaterialId));
      MaterialThumbnailDataUrls = thumbnails
        .Where(thumbnail => thumbnail.Bytes.Length > 0)
        .ToDictionary(thumbnail => thumbnail.Id, thumbnail => BuildDataUrl(thumbnail.ContentType, thumbnail.Bytes));
    }
    catch (Exception ex)
    {
      MaterialThumbnailDataUrls = [];
      UiMessages.ShowWarning($"No se pudieron cargar las fotos de los materiales. {ex.Message}");
    }
  }

  private bool TryGetMaterialThumbnailDataUrl(int materialId, out string dataUrl)
    => MaterialThumbnailDataUrls.TryGetValue(materialId, out dataUrl!);

  private bool MatchesMaterialSearch(PhysicalCountLineDto line)
  {
    var search = MaterialSearch.Trim();
    return search.Length == 0
      || GetMaterialTitle(line).Contains(search, StringComparison.CurrentCultureIgnoreCase)
      || line.MaterialCode.Contains(search, StringComparison.OrdinalIgnoreCase)
      || (line.Barcode?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
  }

  private static bool MatchesFilter(PhysicalCountLineDto line, LineFilterMode filter)
    => filter switch
    {
      LineFilterMode.Pending => !line.CountedQuantity.HasValue,
      LineFilterMode.Counted => line.CountedQuantity.HasValue,
      LineFilterMode.Variance => line.VarianceQuantity is not null and not 0m,
      LineFilterMode.Flagged => line.IsMissing || line.IsDamaged || !string.IsNullOrWhiteSpace(line.Notes) || line.AttachmentCount > 0,
      _ => true
    };

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
      return lines.FirstOrDefault(line => !line.CountedQuantity.HasValue);
    }

    for (var offset = 1; offset < lines.Count; offset++)
    {
      var candidate = lines[(currentIndex + offset) % lines.Count];
      if (!candidate.CountedQuantity.HasValue)
      {
        return candidate;
      }
    }

    return null;
  }

  private void QueueCountedQuantityFocus()
    => _focusCountedQuantityInputPending = CanCaptureLine && SelectedLine?.Lots.Count == 0;

  private async Task ApplyQuerySessionSelectionAsync()
  {
    if (!QuerySessionId.HasValue || _appliedQuerySessionId == QuerySessionId.Value)
    {
      return;
    }

    _appliedQuerySessionId = QuerySessionId.Value;
    if (Sessions.Any(session => session.Id == QuerySessionId.Value))
    {
      await SeleccionarSesionAsync(QuerySessionId.Value, includeHistorical: true);
    }
  }

  private void SyncSelectedSessionAfterRefresh()
  {
    if (SelectedSession is not null && Sessions.All(session => session.Id != SelectedSession.Id))
    {
      ClearSelectedSession();
    }
  }

  private void ClearSelectedSession()
  {
    SelectedSession = null;
    SelectedLine = null;
    LineCapture = new();
    MaterialThumbnailDataUrls = [];
    MaterialSearch = string.Empty;
    PendingLineAttachmentBytes = null;
    PendingLineAttachmentName = null;
    PendingLineAttachmentContentType = null;
    _countedQuantityInput = string.Empty;
    _focusCountedQuantityInputPending = false;
    ShowCompletionReview = false;
    ShowFullAuditLog = false;
    ShowMobileMaterialList = false;
    ShowRecountPanel = false;
    CloseMaterialImageModal();
  }

  private async Task StartScannerAsync()
  {
    try
    {
      var module = await EnsureScannerModuleAsync();
      var supported = await module.InvokeAsync<bool>("isSupported");
      if (!supported)
      {
        IsStartingScanner = false;
        ScannerMessage = "Este navegador no permite escanear con la cámara. Usa la búsqueda manual.";
        StateHasChanged();
        return;
      }

      _scannerCallbackReference ??= DotNetObjectReference.Create(this);
      await module.InvokeVoidAsync("start", ScannerVideoElement, _scannerCallbackReference);
      IsStartingScanner = false;
      StateHasChanged();
    }
    catch (JSException ex)
    {
      IsStartingScanner = false;
      ScannerMessage = GetScannerErrorMessage(ex.Message);
      StateHasChanged();
    }
    catch (InvalidOperationException)
    {
      IsStartingScanner = false;
      ScannerMessage = "No se pudo iniciar la cámara. Usa la búsqueda manual.";
      StateHasChanged();
    }
  }

  private async Task<IJSObjectReference> EnsureScannerModuleAsync()
    => _scannerModule ??= await Js.InvokeAsync<IJSObjectReference>("import", "./js/physical-count-scanner.js");

  private async Task StopScannerAsync()
  {
    if (_scannerModule is null)
    {
      return;
    }

    try
    {
      await _scannerModule.InvokeVoidAsync("stop", ScannerVideoElement);
    }
    catch (JSDisconnectedException)
    {
    }
    catch (InvalidOperationException)
    {
    }
  }

  private static string GetScannerErrorMessage(string rawMessage)
  {
    if (rawMessage.Contains("NotAllowedError", StringComparison.OrdinalIgnoreCase)
      || rawMessage.Contains("Permission", StringComparison.OrdinalIgnoreCase))
    {
      return "No se autorizó la cámara. Permite el acceso en tu navegador o usa la búsqueda manual.";
    }

    if (rawMessage.Contains("NotFoundError", StringComparison.OrdinalIgnoreCase))
    {
      return "No encontramos una cámara disponible. Usa la búsqueda manual.";
    }

    return "No se pudo iniciar el escáner. Usa la búsqueda manual.";
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
        CountedQuantityInputRef,
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

  private void UpdateCountedQuantityInputFromCapture()
    => _countedQuantityInput = SelectedLine?.CountedQuantity is null ? string.Empty : FormatCountedQuantity(LineCapture.CountedQuantity);

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

    if (decimal.TryParse(trimmedValue, QuantityInputNumberStyles, QuantityInputCulture, out result)
      || decimal.TryParse(trimmedValue, QuantityInputNumberStyles, CultureInfo.InvariantCulture, out result)
      || decimal.TryParse(trimmedValue, QuantityInputNumberStyles, CultureInfo.CurrentCulture, out result))
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

  private static int GetLifecycleStep(string? status)
    => NormalizeSessionStatus(status) switch
    {
      "draft" or "recount" => 1,
      "submitted" => 2,
      "approved" => 3,
      "posted" => 4,
      _ => 0
    };

  private static string NormalizeSessionStatus(string? status)
    => string.IsNullOrWhiteSpace(status) ? string.Empty : status.Trim().ToLowerInvariant();

  protected static bool IsDraftStatus(string? status) => NormalizeSessionStatus(status) == "draft";
  protected static bool IsSubmittedStatus(string? status) => NormalizeSessionStatus(status) == "submitted";
  protected static bool IsApprovedStatus(string? status) => NormalizeSessionStatus(status) == "approved";
  protected static bool IsRecountStatus(string? status) => NormalizeSessionStatus(status) == "recount";
  protected static bool IsPostedStatus(string? status) => NormalizeSessionStatus(status) == "posted";
  protected static bool IsCanceledStatus(string? status) => NormalizeSessionStatus(status) == "canceled";
  private static bool IsCancelableStatus(string? status)
    => IsDraftStatus(status) || IsSubmittedStatus(status) || IsApprovedStatus(status) || IsRecountStatus(status);

  public async ValueTask DisposeAsync()
  {
    await StopScannerAsync();
    _scannerCallbackReference?.Dispose();
    if (_scannerModule is not null)
    {
      try
      {
        await _scannerModule.DisposeAsync();
      }
      catch (JSDisconnectedException)
      {
      }
    }
  }

  protected enum LineFilterMode
  {
    All,
    Pending,
    Counted,
    Variance,
    Flagged
  }

  protected sealed record FilterOption(LineFilterMode Value, string Label);

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
