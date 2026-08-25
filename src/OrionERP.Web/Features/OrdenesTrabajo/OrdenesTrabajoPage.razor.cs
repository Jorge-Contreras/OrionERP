using OrionERP.Application.Common;
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.JSInterop;
using OrionERP.Application.Features.OrdenesTrabajo;
using OrionERP.Infrastructure.Auth;
using OrionERP.Web.Services;
using OrionERP.Web.State;

namespace OrionERP.Web.Features.OrdenesTrabajo;

public partial class OrdenesTrabajoPage : ComponentBase
{
  [Inject] private IOrdenTrabajoService OrdenTrabajoService { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;
  [Inject] private NavigationManager Navigation { get; set; } = default!;
  [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
  [Inject] private ICurrentCompanyContext RfcState { get; set; } = default!;
  [Inject] private IJSRuntime JSRuntime { get; set; } = default!;

  protected CultureInfo CurrencyCulture { get; } = CultureInfo.GetCultureInfo("es-MX");
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
  protected bool IsCreating { get; set; }
  protected int? DeletingOrderId { get; set; }
  protected string? ErrorMessage { get; set; }

  protected bool CanCreate => IsPrivilegedUser;
  protected bool CanDelete => IsPrivilegedUser;
  private string CurrentRfc => RfcState.RequireRfc();

  protected override async Task OnInitializedAsync()
  {
    await ResolveCurrentUserAsync();
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
      if ((CreateRequest.OwnerEmployeeId <= 0 || !Employees.Any(employee => employee.Id == CreateRequest.OwnerEmployeeId)) && Employees.Count > 0)
      {
        CreateRequest.OwnerEmployeeId = Employees[0].Id;
      }
      CreateHelperIds.IntersectWith(Employees.Select(employee => employee.Id));
      await LoadDashboardAsync();
      await LoadOrdersAsync();
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

  protected async Task LoadOrdersAsync()
  {
    Filter.Rfc = CurrentRfc;
    if (!IsPrivilegedUser)
    {
      Filter.ParticipantEmployeeId = CurrentEmployeeId;
    }

    Orders = (await OrdenTrabajoService.SearchWorkOrdersAsync(Filter)).ToList();
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
      CreateRequest = BuildDefaultCreateRequest();
      CreateHelperIds.Clear();
      await LoadDashboardAsync();
      await LoadOrdersAsync();
      if (newId.HasValue)
      {
        Navigation.NavigateTo($"/ordenes-trabajo/{newId.Value}");
      }
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
    if (args.Value is bool selected && selected)
    {
      CreateHelperIds.Add(employeeId);
      return;
    }

    if (bool.TryParse(args.Value?.ToString(), out var parsed) && parsed)
    {
      CreateHelperIds.Add(employeeId);
      return;
    }

    CreateHelperIds.Remove(employeeId);
  }

  protected void OpenOrder(int id)
    => Navigation.NavigateTo($"/ordenes-trabajo/{id}");

  protected async Task DeleteOrderAsync(OrdenTrabajoListItemDto item)
  {
    if (!CanDelete || item is null)
    {
      return;
    }

    var confirmed = await ConfirmAsync($"Estas seguro que deseas eliminar la orden {item.Folio}? Esta accion no se puede deshacer.");
    if (!confirmed)
    {
      return;
    }

    DeletingOrderId = item.Id;
    try
    {
      var result = await OrdenTrabajoService.DeleteWorkOrderAsync(item.Id, CurrentUserName);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      await LoadDashboardAsync();
      await LoadOrdersAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo eliminar la orden. {ex.Message}");
    }
    finally
    {
      DeletingOrderId = null;
    }
  }

  public static string GetStatusLabel(string? status)
    => status switch
    {
      "BORRADOR" => "Borrador",
      "ASIGNADA" => "Asignada",
      "EN_PROCESO" => "En proceso",
      "EN_REVISION" => "En revision",
      "RECHAZADA" => "Rechazada",
      "CERRADA" => "Cerrada",
      "CANCELADA" => "Cancelada",
      _ => string.IsNullOrWhiteSpace(status) ? "Sin estado" : status
    };

  public static string GetStatusBadgeClass(string? status)
    => status switch
    {
      "ASIGNADA" => "badge text-bg-primary",
      "EN_PROCESO" => "badge text-bg-info",
      "EN_REVISION" => "badge text-bg-warning",
      "RECHAZADA" => "badge text-bg-danger",
      "CERRADA" => "badge text-bg-success",
      "CANCELADA" => "badge text-bg-secondary",
      _ => "badge text-bg-light"
    };

  public static string GetOrderRowClass(OrdenTrabajoListItemDto item)
    => item.IsOverdue ? "ordenes-row-overdue" : string.Empty;

  public static string FormatWindow(TimeSpan? start, TimeSpan? end)
    => start.HasValue && end.HasValue
      ? $"{start.Value:hh\\:mm} - {end.Value:hh\\:mm}"
      : "Sin ventana";

  public static string FormatTarget(OrdenTrabajoListItemDto item)
  {
    if (!string.IsNullOrWhiteSpace(item.RoomName))
    {
      return item.RoomName;
    }

    return string.IsNullOrWhiteSpace(item.Ubicacion) ? "Sin ubicacion" : item.Ubicacion;
  }

  private async Task LoadDashboardAsync()
  {
    Dashboard = await OrdenTrabajoService.GetDashboardAsync(new OrdenTrabajoDashboardFilter
    {
      Rfc = CurrentRfc,
      EmployeeId = IsPrivilegedUser ? null : CurrentEmployeeId,
      AssignedOnly = !IsPrivilegedUser
    });
  }

  private OrdenTrabajoCreateRequest BuildDefaultCreateRequest()
    => new()
    {
      Rfc = CurrentRfc,
      CategoriaCodigo = OrdenTrabajoCodes.CategoriaMantenimiento,
      Prioridad = OrdenTrabajoCodes.PrioridadNormal,
      FechaProgramada = DateTime.Today,
      FechaVencimiento = DateTime.Today,
      OwnerEmployeeId = Employees.FirstOrDefault()?.Id ?? 0
    };

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

    CurrentEmployeeId = int.TryParse(user.FindFirst("employee_id")?.Value, out var employeeId) ? employeeId : null;
  }

  private async Task<bool> ConfirmAsync(string message)
  {
    try
    {
      return await JSRuntime.InvokeAsync<bool>("confirm", message);
    }
    catch
    {
      return false;
    }
  }

}
