using System.IO;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using OrionERP.Application.Features.Logistica.Locations;
using OrionERP.Application.Features.Logistica.PhysicalCounts;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Web.Services;

namespace OrionERP.Web.Features.Logistica.PhysicalCounts;

public partial class ConteosFisicosPage : ComponentBase
{
  [Inject] private IPhysicalCountService PhysicalCountService { get; set; } = default!;
  [Inject] private ILocationService LocationService { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;
  [Inject] private IJSRuntime Js { get; set; } = default!;
  [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;

  protected List<LookupOptionDto> LocationOptions { get; set; } = [];
  protected List<PhysicalCountSessionSummaryDto> Sessions { get; set; } = [];
  protected PhysicalCountSessionCreateRequest SessionCreateRequest { get; set; } = new();
  protected PhysicalCountSessionDetailDto? SelectedSession { get; set; }
  protected PhysicalCountLineDto? SelectedLine { get; set; }
  protected PhysicalCountLineCaptureRequest LineCapture { get; set; } = new();
  protected string CurrentUserName { get; set; } = "OrionERP";
  protected bool IsLoadingSessions { get; set; }
  protected bool IsCreatingSession { get; set; }
  protected bool IsSavingLine { get; set; }
  protected bool IsMutatingSession { get; set; }
  protected byte[]? PendingLineAttachmentBytes { get; set; }
  protected string? PendingLineAttachmentName { get; set; }
  protected string? PendingLineAttachmentContentType { get; set; }

  protected bool CanSubmit => SelectedSession is not null && string.Equals(SelectedSession.Status, "Draft", StringComparison.OrdinalIgnoreCase);
  protected bool CanApprove => SelectedSession is not null && string.Equals(SelectedSession.Status, "Submitted", StringComparison.OrdinalIgnoreCase);
  protected bool CanPost => SelectedSession is not null && string.Equals(SelectedSession.Status, "Approved", StringComparison.OrdinalIgnoreCase);
  protected bool CanCaptureLine => SelectedSession is not null && SelectedLine is not null && string.Equals(SelectedSession.Status, "Draft", StringComparison.OrdinalIgnoreCase);
  protected string CurrentVarianceText => SelectedLine is null
    ? "0.00"
    : (LineCapture.CountedQuantity - SelectedLine.ExpectedQuantity).ToString("N2");

  protected override async Task OnInitializedAsync()
  {
    CurrentUserName = await ResolveCurrentUserAsync();
    LocationOptions = (await LocationService.GetLocationLookupAsync(inventoryOnly: true)).ToList();
    await CargarSesionesAsync();
  }

  protected async Task CargarSesionesAsync()
  {
    IsLoadingSessions = true;
    try
    {
      Sessions = (await PhysicalCountService.GetSessionsAsync()).ToList();
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
        await SeleccionarSesionAsync(result.EntityId.Value);
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
      SelectedSession = await PhysicalCountService.GetSessionAsync(sessionId);
      SelectedLine = null;
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
      CountedQuantity = line.CountedQuantity ?? line.ExpectedQuantity,
      Notes = line.Notes,
      IsMissing = line.IsMissing,
      IsDamaged = line.IsDamaged,
      CapturedBy = CurrentUserName
    };

    PendingLineAttachmentBytes = null;
    PendingLineAttachmentName = null;
    PendingLineAttachmentContentType = null;
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

  protected async Task GuardarLineaAsync()
  {
    if (!CanCaptureLine || SelectedLine is null || SelectedSession is null)
    {
      return;
    }

    IsSavingLine = true;
    try
    {
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
      if (SelectedSession is not null && SelectedLine is not null)
      {
        var refreshedLine = SelectedSession.Lines.FirstOrDefault(line => line.Id == LineCapture.LineId);
        if (refreshedLine is not null)
        {
          SeleccionarLinea(refreshedLine);
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

  protected async Task EnviarSesionAsync() => await EjecutarSesionAsync(
    () => PhysicalCountService.SubmitSessionAsync(SelectedSession!.Id, CurrentUserName));

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

  private async Task EjecutarSesionAsync(Func<Task<LogisticsCommandResult>> operation)
  {
    if (SelectedSession is null)
    {
      return;
    }

    IsMutatingSession = true;
    try
    {
      var result = await operation();
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      await CargarSesionesAsync();
      await SeleccionarSesionAsync(SelectedSession.Id);
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

  private async Task<string> ResolveCurrentUserAsync()
  {
    var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
    return authState.User.Identity?.Name?.Trim() switch
    {
      { Length: > 0 } name => name,
      _ => "Administrador"
    };
  }
}
