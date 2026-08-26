using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.JSInterop;
using OrionERP.Application.Common;
using OrionERP.Application.Features.OrdenesTrabajo;
using OrionERP.Infrastructure.Auth;
using OrionERP.Web.Services;
using OrionERP.Web.State;

namespace OrionERP.Web.Features.OrdenesTrabajo;

public partial class OrdenesTrabajoPage : ComponentBase, IDisposable
{
  protected const string WorkView = "trabajo";
  protected const string ManagementView = "gestion";
  protected const string ActiveQueueCode = "activas";
  protected const string TodayQueueCode = "hoy";
  protected const string OverdueQueueCode = "vencidas";
  protected const string HistoryQueueCode = "historial";
  private const int PageSize = 25;

  [Inject] private IOrdenTrabajoService OrdenTrabajoService { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;
  [Inject] private NavigationManager Navigation { get; set; } = default!;
  [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
  [Inject] private ICurrentCompanyContext RfcState { get; set; } = default!;
  [Inject] private IJSRuntime JSRuntime { get; set; } = default!;

  protected List<OrdenTrabajoCategoriaDto> Categories { get; set; } = [];
  protected List<OrdenTrabajoLookupDto> Employees { get; set; } = [];
  protected List<OrdenTrabajoListItemDto> Orders { get; set; } = [];
  protected OrdenTrabajoDashboardDto? Dashboard { get; set; }
  protected OrdenTrabajoSearchFilter Filter { get; set; } = new();
  protected OrdenTrabajoCreateRequest CreateRequest { get; set; } = new();
  protected HashSet<int> CreateHelperIds { get; set; } = [];
  protected string CurrentUserName { get; set; } = "OrionERP";
  protected int? CurrentEmployeeId { get; set; }
  protected bool IsPrivilegedUser { get; set; }
  protected bool IsLoading { get; set; }
  protected bool IsLoadingMore { get; set; }
  protected bool IsCreating { get; set; }
  protected bool IsCreatePanelOpen { get; set; }
  protected bool IsAdvancedFiltersOpen { get; set; }
  protected bool HasMoreOrders { get; set; }
  protected string ActiveView { get; set; } = WorkView;
  protected string ActiveQueue { get; set; } = ActiveQueueCode;
  protected string? ErrorMessage { get; set; }

  protected bool CanCreate => IsPrivilegedUser;
  protected bool HasActiveFilters => ActiveQueue != ActiveQueueCode
    || !string.IsNullOrWhiteSpace(Filter.SearchText)
    || !string.IsNullOrWhiteSpace(Filter.CategoriaCodigo)
    || Filter.OwnerEmployeeId.HasValue;
  protected int ActiveAdvancedFilterCount => (string.IsNullOrWhiteSpace(Filter.CategoriaCodigo) ? 0 : 1)
    + (Filter.OwnerEmployeeId.HasValue ? 1 : 0);
  protected string QueueEyebrow => ActiveQueue switch
  {
    TodayQueueCode => "Programadas para hoy",
    OverdueQueueCode => "Atención prioritaria",
    HistoryQueueCode => "Consulta",
    _ => IsPrivilegedUser ? "Trabajo pendiente" : "Tu trabajo pendiente"
  };
  protected string QueueTitle => ActiveQueue switch
  {
    TodayQueueCode => "Hoy",
    OverdueQueueCode => "Órdenes vencidas",
    HistoryQueueCode => "Historial",
    _ => "Órdenes activas"
  };
  protected string EmptyTitle => ActiveQueue switch
  {
    TodayQueueCode => "No tienes órdenes para hoy",
    OverdueQueueCode => "No hay órdenes vencidas",
    HistoryQueueCode => "No hay historial para mostrar",
    _ => "No hay trabajo pendiente"
  };
  protected string EmptyMessage => HasActiveFilters
    ? "Prueba con otro filtro o vuelve a las órdenes activas."
    : "Cuando te asignen una orden aparecerá aquí.";

  private string CurrentRfc => RfcState.RequireRfc();
  private CancellationTokenSource? SearchDebounce { get; set; }

  protected override async Task OnInitializedAsync()
  {
    await ResolveCurrentUserAsync();
    ApplyQueryState();
    CreateRequest = BuildDefaultCreateRequest();
    await LoadAsync();
  }

  protected async Task LoadAsync()
  {
    IsLoading = true;
    ErrorMessage = null;
    try
    {
      Categories = (await OrdenTrabajoService.GetCategoriesAsync()).ToList();
      Employees = (await OrdenTrabajoService.GetActiveEmployeeOptionsAsync(CurrentRfc)).ToList();
      if (CreateRequest.OwnerEmployeeId > 0 && !Employees.Any(employee => employee.Id == CreateRequest.OwnerEmployeeId))
      {
        CreateRequest.OwnerEmployeeId = 0;
      }

      CreateHelperIds.IntersectWith(Employees.Select(employee => employee.Id));
      Dashboard = await OrdenTrabajoService.GetDashboardAsync(new OrdenTrabajoDashboardFilter
      {
        Rfc = CurrentRfc,
        EmployeeId = IsPrivilegedUser ? null : CurrentEmployeeId,
        AssignedOnly = !IsPrivilegedUser
      });
      await LoadOrdersCoreAsync(append: false);
    }
    catch (Exception ex)
    {
      ErrorMessage = ex.Message;
      Orders.Clear();
    }
    finally
    {
      IsLoading = false;
    }
  }

  protected async Task LoadMoreAsync()
  {
    if (IsLoadingMore || !HasMoreOrders)
    {
      return;
    }

    IsLoadingMore = true;
    try
    {
      await LoadOrdersCoreAsync(append: true);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudieron cargar más órdenes. {ex.Message}");
    }
    finally
    {
      IsLoadingMore = false;
    }
  }

  protected async Task SetViewAsync(string view)
  {
    var normalized = IsPrivilegedUser && string.Equals(view, ManagementView, StringComparison.OrdinalIgnoreCase)
      ? ManagementView
      : WorkView;
    if (ActiveView == normalized)
    {
      return;
    }

    ActiveView = normalized;
    await ReloadOrdersAsync();
    await PersistQueryStateAsync();
  }

  protected async Task SetQueueAsync(string queue)
  {
    var normalized = NormalizeQueue(queue);
    if (ActiveQueue == normalized)
    {
      return;
    }

    ActiveQueue = normalized;
    await ReloadOrdersAsync();
    await PersistQueryStateAsync();
  }

  protected async Task OnSearchInput(ChangeEventArgs args)
  {
    Filter.SearchText = args.Value?.ToString();
    SearchDebounce?.Cancel();
    SearchDebounce?.Dispose();
    SearchDebounce = new CancellationTokenSource();
    var token = SearchDebounce.Token;

    try
    {
      await Task.Delay(350, token);
      await InvokeAsync(async () =>
      {
        await ReloadOrdersAsync();
        await PersistQueryStateAsync();
      });
    }
    catch (OperationCanceledException)
    {
    }
  }

  protected async Task ClearSearchAsync()
  {
    Filter.SearchText = null;
    await ReloadOrdersAsync();
    await PersistQueryStateAsync();
  }

  protected void ToggleAdvancedFilters()
    => IsAdvancedFiltersOpen = !IsAdvancedFiltersOpen;

  protected async Task OnCategoryChangedAsync(ChangeEventArgs args)
  {
    Filter.CategoriaCodigo = NullIfBlank(args.Value?.ToString());
    await ReloadOrdersAsync();
    await PersistQueryStateAsync();
  }

  protected async Task OnOwnerChangedAsync(ChangeEventArgs args)
  {
    Filter.OwnerEmployeeId = int.TryParse(args.Value?.ToString(), out var id) && id > 0 ? id : null;
    await ReloadOrdersAsync();
    await PersistQueryStateAsync();
  }

  protected async Task ClearAdvancedFiltersAsync()
  {
    Filter.CategoriaCodigo = null;
    Filter.OwnerEmployeeId = null;
    await ReloadOrdersAsync();
    await PersistQueryStateAsync();
  }

  protected async Task ResetWorkViewAsync()
  {
    ActiveQueue = ActiveQueueCode;
    Filter.SearchText = null;
    Filter.CategoriaCodigo = null;
    Filter.OwnerEmployeeId = null;
    await ReloadOrdersAsync();
    await PersistQueryStateAsync();
  }

  protected async Task OpenEmployeeWorkAsync(int employeeId)
  {
    ActiveView = WorkView;
    ActiveQueue = ActiveQueueCode;
    Filter.OwnerEmployeeId = employeeId;
    IsAdvancedFiltersOpen = true;
    await ReloadOrdersAsync();
    await PersistQueryStateAsync();
  }

  protected void OpenCreatePanel()
  {
    CreateRequest = BuildDefaultCreateRequest();
    CreateHelperIds.Clear();
    IsCreatePanelOpen = true;
  }

  protected void CloseCreatePanel()
  {
    if (!IsCreating)
    {
      IsCreatePanelOpen = false;
    }
  }

  protected async Task CreateManualAsync()
  {
    if (!CanCreate)
    {
      return;
    }

    if (CreateRequest.OwnerEmployeeId <= 0)
    {
      UiMessages.ShowWarning("Selecciona un responsable.");
      return;
    }

    IsCreating = true;
    try
    {
      CreateRequest.Rfc = CurrentRfc;
      CreateRequest.HelperEmployeeIds = CreateHelperIds.ToList();
      CreateRequest.CreatedBy = CurrentUserName;
      var result = await OrdenTrabajoService.CreateManualAsync(CreateRequest);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      var newId = result.EntityId;
      IsCreatePanelOpen = false;
      CreateRequest = BuildDefaultCreateRequest();
      CreateHelperIds.Clear();
      if (newId.HasValue)
      {
        Navigation.NavigateTo(BuildOrderHref(newId.Value));
        return;
      }

      await LoadAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo crear la orden. {ex.Message}");
    }
    finally
    {
      IsCreating = false;
    }
  }

  protected void ToggleCreateHelper(int employeeId, ChangeEventArgs args)
  {
    if ((args.Value is bool selected && selected)
      || (bool.TryParse(args.Value?.ToString(), out var parsed) && parsed))
    {
      CreateHelperIds.Add(employeeId);
      return;
    }

    CreateHelperIds.Remove(employeeId);
  }

  protected string BuildOrderHref(int id)
  {
    var safeReturn = BuildRelativeListUri().TrimStart('/');
    return QueryHelpers.AddQueryString($"/ordenes-trabajo/{id}", "from", safeReturn);
  }

  protected static char GetInitial(string? name)
    => string.IsNullOrWhiteSpace(name) ? '?' : char.ToUpperInvariant(name.Trim()[0]);

  public static string GetStatusLabel(string? status)
    => status switch
    {
      OrdenTrabajoCodes.EstadoBorrador => "Borrador",
      OrdenTrabajoCodes.EstadoAsignada => "Asignada",
      OrdenTrabajoCodes.EstadoEnProceso => "En proceso",
      OrdenTrabajoCodes.EstadoEnRevision => "En revisión",
      OrdenTrabajoCodes.EstadoRechazada => "Rechazada",
      OrdenTrabajoCodes.EstadoCerrada => "Cerrada",
      OrdenTrabajoCodes.EstadoCancelada => "Cancelada",
      _ => string.IsNullOrWhiteSpace(status) ? "Sin estado" : status
    };

  public static string GetStatusBadgeClass(string? status)
    => status switch
    {
      OrdenTrabajoCodes.EstadoAsignada => "badge text-bg-primary",
      OrdenTrabajoCodes.EstadoEnProceso => "badge text-bg-info",
      OrdenTrabajoCodes.EstadoEnRevision => "badge text-bg-warning",
      OrdenTrabajoCodes.EstadoRechazada => "badge text-bg-danger",
      OrdenTrabajoCodes.EstadoCerrada => "badge text-bg-success",
      OrdenTrabajoCodes.EstadoCancelada => "badge text-bg-secondary",
      _ => "badge text-bg-light"
    };

  public static string FormatWindow(TimeSpan? start, TimeSpan? end)
    => start.HasValue && end.HasValue ? $"{start.Value:hh\\:mm} - {end.Value:hh\\:mm}" : "Sin horario";

  public static string FormatTarget(OrdenTrabajoListItemDto item)
    => !string.IsNullOrWhiteSpace(item.RoomName)
      ? item.RoomName
      : string.IsNullOrWhiteSpace(item.Ubicacion) ? "Sin ubicación" : item.Ubicacion;

  public void Dispose()
  {
    SearchDebounce?.Cancel();
    SearchDebounce?.Dispose();
  }

  private async Task ReloadOrdersAsync()
  {
    IsLoading = true;
    ErrorMessage = null;
    try
    {
      await LoadOrdersCoreAsync(append: false);
    }
    catch (Exception ex)
    {
      ErrorMessage = ex.Message;
      Orders.Clear();
    }
    finally
    {
      IsLoading = false;
    }
  }

  private async Task LoadOrdersCoreAsync(bool append)
  {
    if (!IsPrivilegedUser && !CurrentEmployeeId.HasValue)
    {
      Orders.Clear();
      HasMoreOrders = false;
      return;
    }

    var query = BuildSearchFilter(append ? Orders.Count : 0);
    var rows = (await OrdenTrabajoService.SearchWorkOrdersAsync(query)).ToList();
    if (!append)
    {
      Orders = rows;
    }
    else
    {
      Orders.AddRange(rows.Where(row => Orders.All(existing => existing.Id != row.Id)));
    }

    HasMoreOrders = rows.Count == PageSize;
  }

  private OrdenTrabajoSearchFilter BuildSearchFilter(int skip)
  {
    var query = new OrdenTrabajoSearchFilter
    {
      Rfc = CurrentRfc,
      SearchText = NullIfBlank(Filter.SearchText),
      CategoriaCodigo = NullIfBlank(Filter.CategoriaCodigo),
      OwnerEmployeeId = IsPrivilegedUser ? Filter.OwnerEmployeeId : null,
      ParticipantEmployeeId = IsPrivilegedUser ? null : CurrentEmployeeId,
      SortMode = OrdenTrabajoSearchSort.OperationalPriority,
      Skip = skip,
      Take = PageSize
    };

    if (ActiveView == ManagementView && IsPrivilegedUser)
    {
      query.Estado = OrdenTrabajoCodes.EstadoEnRevision;
      return query;
    }

    switch (ActiveQueue)
    {
      case TodayQueueCode:
        query.ScheduledFrom = DateTime.Today;
        query.ScheduledTo = DateTime.Today;
        break;
      case OverdueQueueCode:
        query.OverdueOnly = true;
        break;
      case HistoryQueueCode:
        query.IncludeClosed = true;
        query.ClosedOnly = true;
        query.SortMode = OrdenTrabajoSearchSort.Newest;
        break;
    }

    return query;
  }

  private void ApplyQueryState()
  {
    var query = QueryHelpers.ParseQuery(Navigation.ToAbsoluteUri(Navigation.Uri).Query);
    var requestedView = query.TryGetValue("vista", out var view) ? view.ToString() : null;
    ActiveView = IsPrivilegedUser && string.Equals(requestedView, ManagementView, StringComparison.OrdinalIgnoreCase)
      ? ManagementView
      : WorkView;
    ActiveQueue = NormalizeQueue(query.TryGetValue("cola", out var queue) ? queue.ToString() : null);
    Filter.SearchText = NullIfBlank(query.TryGetValue("q", out var search) ? search.ToString() : null);
    Filter.CategoriaCodigo = NullIfBlank(query.TryGetValue("categoria", out var category) ? category.ToString() : null);
    Filter.OwnerEmployeeId = IsPrivilegedUser
      && query.TryGetValue("responsable", out var owner)
      && int.TryParse(owner.ToString(), out var ownerId)
      && ownerId > 0
        ? ownerId
        : null;
    IsAdvancedFiltersOpen = !string.IsNullOrWhiteSpace(Filter.CategoriaCodigo) || Filter.OwnerEmployeeId.HasValue;
  }

  private async ValueTask PersistQueryStateAsync()
  {
    var absolute = Navigation.ToAbsoluteUri(BuildRelativeListUri()).ToString();
    try
    {
      await JSRuntime.InvokeVoidAsync("history.replaceState", null, string.Empty, absolute);
    }
    catch
    {
      Navigation.NavigateTo(absolute, new NavigationOptions { ReplaceHistoryEntry = true });
    }
  }

  private string BuildRelativeListUri()
  {
    var values = new Dictionary<string, string?>();
    if (ActiveView == ManagementView)
    {
      values["vista"] = ManagementView;
    }
    else if (ActiveQueue != ActiveQueueCode)
    {
      values["cola"] = ActiveQueue;
    }

    if (!string.IsNullOrWhiteSpace(Filter.SearchText)) values["q"] = Filter.SearchText.Trim();
    if (!string.IsNullOrWhiteSpace(Filter.CategoriaCodigo)) values["categoria"] = Filter.CategoriaCodigo.Trim();
    if (IsPrivilegedUser && Filter.OwnerEmployeeId.HasValue) values["responsable"] = Filter.OwnerEmployeeId.Value.ToString();

    return QueryHelpers.AddQueryString("/ordenes-trabajo", values);
  }

  private OrdenTrabajoCreateRequest BuildDefaultCreateRequest()
    => new()
    {
      Rfc = CurrentRfc,
      CategoriaCodigo = OrdenTrabajoCodes.CategoriaMantenimiento,
      Prioridad = OrdenTrabajoCodes.PrioridadNormal,
      FechaProgramada = DateTime.Today,
      FechaVencimiento = DateTime.Today,
      OwnerEmployeeId = 0
    };

  private async Task ResolveCurrentUserAsync()
  {
    var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
    var user = authState.User;
    CurrentUserName = user.Identity?.Name?.Trim() is { Length: > 0 } name ? name : "OrionERP";
    IsPrivilegedUser = OrdenTrabajoPermissions.CanAccessManagement(user.IsInRole);
    CurrentEmployeeId = int.TryParse(user.FindFirst("employee_id")?.Value, out var employeeId) ? employeeId : null;
  }

  private static string NormalizeQueue(string? queue)
    => queue?.Trim().ToLowerInvariant() switch
    {
      TodayQueueCode => TodayQueueCode,
      OverdueQueueCode => OverdueQueueCode,
      HistoryQueueCode => HistoryQueueCode,
      _ => ActiveQueueCode
    };

  private static string? NullIfBlank(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
