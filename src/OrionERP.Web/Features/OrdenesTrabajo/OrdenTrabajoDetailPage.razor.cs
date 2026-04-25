using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Identity;
using OrionERP.Application.Features.OrdenesTrabajo;
using OrionERP.Infrastructure.Auth;
using OrionERP.Web.Services;

namespace OrionERP.Web.Features.OrdenesTrabajo;

public partial class OrdenTrabajoDetailPage : ComponentBase
{
  private const long MaxImageBytes = 12 * 1024 * 1024;
  private const int ImageMaxPixels = 1600;
  private const int ThumbnailMaxPixels = 320;

  [Parameter] public int Id { get; set; }

  [Inject] private IOrdenTrabajoService OrdenTrabajoService { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;
  [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
  [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;

  protected CultureInfo CurrencyCulture { get; } = CultureInfo.GetCultureInfo("es-MX");
  protected OrdenTrabajoDetailDto? Order { get; set; }
  protected List<OrdenTrabajoLookupDto> Employees { get; set; } = [];
  protected OrdenTrabajoUpdateRequest EditRequest { get; set; } = new();
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
  protected string? ErrorMessage { get; set; }

  protected bool CanReview => IsPrivilegedUser;
  protected bool IsInReview => Order?.Estado == OrdenTrabajoCodes.EstadoEnRevision;
  protected bool CanEdit => Order is not null && IsPrivilegedUser && IsEditableStatus(Order.Estado);
  protected bool CanExecute => Order is not null
    && IsExecutableStatus(Order.Estado)
    && IsCurrentUserAssigned();
  protected bool CanRemoveEvidence => CanExecute && Order?.HasBeenSubmittedForReview == false;

  private int? ActorEmployeeIdForExecution => CurrentEmployeeId;

  protected override async Task OnInitializedAsync()
  {
    await ResolveCurrentUserAsync();
    await LoadAsync();
  }

  protected async Task LoadAsync()
  {
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
          DeviceInfo = "Blazor InputFile camera capture",
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

  protected async Task RemoveEvidenceAsync(OrdenTrabajoStepDto step, int evidenceId)
  {
    if (!CanExecute || Order is null)
    {
      return;
    }

    await MutateAsync(() => OrdenTrabajoService.RemoveStepEvidenceAsync(Order.Id, step.Id, evidenceId, CurrentUserName, ActorEmployeeIdForExecution));
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

  protected static string BuildThumbnailDataUrl(OrdenTrabajoEvidenceDto evidence)
  {
    var contentType = string.IsNullOrWhiteSpace(evidence.ThumbnailContentType)
      ? evidence.ContentType
      : evidence.ThumbnailContentType;
    var bytes = evidence.ThumbnailBytes ?? Array.Empty<byte>();
    return $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
  }

  protected string? GetStepNotes(OrdenTrabajoStepDto step)
    => StepNotes.TryGetValue(step.Id, out var notes) ? notes : step.Notas;

  protected void SetStepNotes(int stepId, string? notes)
    => StepNotes[stepId] = notes;

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
  }

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
}
