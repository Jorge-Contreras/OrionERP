using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.JSInterop;
using OrionERP.Application.Features.OrdenesTrabajo;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;
using OrionERP.Infrastructure.Auth;
using OrionERP.Web.Services;
using OrionERP.Web.State;

namespace OrionERP.Web.Features.Reservaciones.Calendario;


public partial class CalendarioReservacionesPage : ComponentBase
{
  [Inject] public IListaReservacionesService ReservacionesService { get; set; } = default!;
  [Inject] public IOrdenTrabajoService OrdenTrabajoService { get; set; } = default!;
  [Inject] public IUiMessageService UiMessages { get; set; } = default!;
  [Inject] public AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
  [Inject] public UserManager<ApplicationUser> UserManager { get; set; } = default!;
  [Inject] public IUserRfcState RfcState { get; set; } = default!;
  [Inject] public NavigationManager Navigation { get; set; } = default!;
  [Inject] public IJSRuntime Js { get; set; } = default!;

  protected RoomCalendarTimelineFilter Filter { get; set; } = CreateDefaultFilter();
  protected RoomCalendarTimelineDto? Timeline { get; set; }
  protected List<DateTime> VisibleDates { get; set; } = new();
  protected Dictionary<(int RoomId, DateTime RoomDate), RoomCalendarDayCellDto> CellLookup { get; set; } = new();
  protected Dictionary<int, List<OrdenTrabajoCalendarBadgeDto>> WorkOrderBadgesByRoomCalendar { get; set; } = new();
  protected List<OrdenTrabajoLookupDto> WorkOrderEmployees { get; set; } = new();
  protected HashSet<int> SelectedRoomCalendarIds { get; set; } = new();
  protected HashSet<int> CleaningHelperIds { get; set; } = new();
  protected int CleaningOwnerEmployeeId { get; set; }
  protected bool IsLoading { get; set; }
  protected bool ShowCleaningModal { get; set; }
  protected bool IsCreatingCleaningOrders { get; set; }
  protected bool IsCreatingReservation { get; set; }
  protected bool CanUseCalendarActions { get; set; }
  protected string? ErrorMessage { get; set; }
  protected string CurrentUserName { get; set; } = "OrionERP";
  protected int? CurrentEmployeeId { get; set; }
  protected CultureInfo HeaderCulture { get; } = CultureInfo.GetCultureInfo("es-MX");
  protected IReadOnlyList<(string Value, string Label)> RoomTypeOptions { get; } = new[]
  {
    ("SUITE", "Suites"),
    ("SERVICIO", "Servicios"),
    ("", "Todos")
  };

  protected int ReservedCellCount => Timeline?.DayCells.Count(x => x.StateCode == "reserved") ?? 0;
  protected int SoftHoldCellCount => Timeline?.DayCells.Count(x => x.StateCode == "soft_hold") ?? 0;
  protected int BlockedCellCount => Timeline?.DayCells.Count(x => x.StateCode == "blocked") ?? 0;
  protected int OrphanCellCount => Timeline?.DayCells.Count(x => x.StateCode == "orphan") ?? 0;

  protected string StartDateText => Filter.StartDate.ToString("yyyy-MM-dd");
  protected string EndDateText => Filter.EndDateExclusive.ToString("yyyy-MM-dd");
  private string CurrentRfc => RfcState.CurrentRfc ?? RfcState.AllowedRfcs.FirstOrDefault() ?? "OHM191112Q26";

  protected override async Task OnInitializedAsync()
  {
    await ResolveCurrentUserAsync();
    if (CanUseCalendarActions)
    {
      await LoadEmployeeOptionsAsync();
    }

    await LoadCalendarAsync();
  }

  protected async Task LoadCalendarAsync()
  {
    IsLoading = true;
    ErrorMessage = null;

    try
    {
      Timeline = await ReservacionesService.GetCalendarTimelineAsync(Filter);
      BuildVisibleDates();
      BuildLookup();
      PruneSelectionToVisibleCells();
      await LoadCalendarWorkOrderBadgesAsync();
    }
    catch (Exception ex)
    {
      ErrorMessage = ex.Message;
      Timeline = null;
      VisibleDates.Clear();
      CellLookup.Clear();
      WorkOrderBadgesByRoomCalendar.Clear();
    }
    finally
    {
      IsLoading = false;
    }
  }

  protected void OnStartDateChanged(ChangeEventArgs args)
  {
    if (DateTime.TryParse(args.Value?.ToString(), out var parsed))
      Filter.StartDate = parsed.Date;
  }

  protected void OnEndDateChanged(ChangeEventArgs args)
  {
    if (DateTime.TryParse(args.Value?.ToString(), out var parsed))
      Filter.EndDateExclusive = parsed.Date;
  }

  protected void OnRoomTypeChanged(ChangeEventArgs args)
  {
    Filter.RoomType = args.Value?.ToString() ?? string.Empty;
  }

  protected RoomCalendarDayCellDto? GetCell(int roomId, DateTime roomDate)
  {
    return CellLookup.TryGetValue((roomId, roomDate.Date), out var cell)
      ? cell
      : null;
  }

  protected static string GetStateLabel(string? stateCode)
  {
    return stateCode switch
    {
      "available" => "Libre",
      "reserved" => "Reservada",
      "soft_hold" => "Soft hold",
      "blocked" => "Bloqueada",
      "orphan" => "Huérfana",
      "missing" => "Sin fila",
      _ => "N/D"
    };
  }

  protected static string GetCellCssClass(RoomCalendarDayCellDto? cell)
  {
    if (cell is null)
      return "calendar-cell calendar-cell-missing";

    return cell.StateCode switch
    {
      "available" => "calendar-cell calendar-cell-available",
      "reserved" => "calendar-cell calendar-cell-reserved",
      "soft_hold" => "calendar-cell calendar-cell-soft-hold",
      "blocked" => "calendar-cell calendar-cell-blocked",
      "orphan" => "calendar-cell calendar-cell-orphan",
      "missing" => "calendar-cell calendar-cell-missing",
      _ => "calendar-cell"
    };
  }

  protected static string GetSummaryChipCssClass(string variant)
  {
    return variant switch
    {
      "resources" => "calendar-stat-chip calendar-stat-chip-neutral",
      "reserved" => "calendar-stat-chip calendar-cell-reserved",
      "soft_hold" => "calendar-stat-chip calendar-cell-soft-hold",
      "blocked" => "calendar-stat-chip calendar-cell-blocked",
      "orphan" => "calendar-stat-chip calendar-cell-orphan",
      "events" => "calendar-stat-chip calendar-stat-chip-dark",
      _ => "calendar-stat-chip calendar-stat-chip-neutral"
    };
  }

  protected static bool IsToday(DateTime day)
    => day.Date == DateTime.Today;

  protected static string GetDayHeaderCssClass(DateTime day)
    => IsToday(day)
      ? "calendar-day-col calendar-day-today"
      : "calendar-day-col";

  protected static string GetCellTooltip(RoomCalendarDayCellDto? cell)
  {
    if (cell is null)
      return "No existe fila en ROOM_CALENDAR para esta fecha.";

    var parts = new List<string>
    {
      $"Estado: {GetStateLabel(cell.StateCode)}"
    };

    if (cell.ReservationId.HasValue)
      parts.Add($"Reservación: {cell.ReservationId}");

    if (!string.IsNullOrWhiteSpace(cell.LockedBy))
      parts.Add($"Bloqueado por: {cell.LockedBy}");

    if (!string.IsNullOrWhiteSpace(cell.DataQualityFlag))
      parts.Add($"Calidad: {cell.DataQualityFlag}");

    return string.Join(" | ", parts);
  }

  protected static string GetReservationHref(int reservationId)
    => $"/reservaciones/{reservationId}";

  protected void OnCellClick(MouseEventArgs args, RoomCalendarDayCellDto? cell)
  {
    if (!CanUseCalendarActions)
    {
      return;
    }

    if (args.Detail > 1)
    {
      return;
    }

    ToggleCellSelection(cell);
  }

  protected void OpenReservationFromCell(RoomCalendarDayCellDto? cell)
  {
    if (!CanUseCalendarActions)
    {
      return;
    }

    if (cell?.ReservationId is int reservationId)
    {
      Navigation.NavigateTo(GetReservationHref(reservationId));
    }
  }

  protected void ToggleCellSelection(RoomCalendarDayCellDto? cell)
  {
    if (!CanUseCalendarActions)
    {
      return;
    }

    if (cell?.RoomCalendarId is not int roomCalendarId)
    {
      return;
    }

    if (!SelectedRoomCalendarIds.Add(roomCalendarId))
    {
      SelectedRoomCalendarIds.Remove(roomCalendarId);
    }
  }

  protected bool IsCellSelected(RoomCalendarDayCellDto? cell)
    => cell?.RoomCalendarId is int roomCalendarId && SelectedRoomCalendarIds.Contains(roomCalendarId);

  protected IReadOnlyList<OrdenTrabajoCalendarBadgeDto> GetCellWorkOrderBadges(RoomCalendarDayCellDto? cell)
  {
    if (cell?.RoomCalendarId is not int roomCalendarId)
    {
      return Array.Empty<OrdenTrabajoCalendarBadgeDto>();
    }

    return WorkOrderBadgesByRoomCalendar.TryGetValue(roomCalendarId, out var badges)
      ? badges
      : Array.Empty<OrdenTrabajoCalendarBadgeDto>();
  }

  protected string GetCalendarOrderBadgeClass(string status)
    => status switch
    {
      "CERRADA" => "calendar-wo-badge calendar-wo-badge-closed",
      "EN_REVISION" => "calendar-wo-badge calendar-wo-badge-review",
      "RECHAZADA" => "calendar-wo-badge calendar-wo-badge-rejected",
      "EN_PROCESO" => "calendar-wo-badge calendar-wo-badge-progress",
      _ => "calendar-wo-badge calendar-wo-badge-open"
    };

  protected string GetCellCompositeCssClass(RoomCalendarDayCellDto? cell, DateTime day)
  {
    var parts = new List<string> { GetCellCssClass(cell) };

    if (IsToday(day))
    {
      parts.Add("calendar-day-today");
    }

    if (CanUseCalendarActions && IsCellSelected(cell))
    {
      parts.Add("calendar-cell-selected");
    }

    if (CanUseCalendarActions && cell?.RoomCalendarId is not null)
    {
      parts.Add("calendar-cell-selectable");
    }

    return string.Join(" ", parts);
  }

  protected void OpenCleaningModal()
  {
    if (!CanUseCalendarActions)
    {
      UiMessages.ShowWarning("Este rol solo puede consultar el calendario.");
      return;
    }

    if (IsCreatingReservation)
    {
      return;
    }

    if (SelectedRoomCalendarIds.Count == 0)
    {
      UiMessages.ShowWarning("Selecciona una o mas celdas del calendario.");
      return;
    }

    if (CleaningOwnerEmployeeId <= 0 && WorkOrderEmployees.Count > 0)
    {
      CleaningOwnerEmployeeId = CurrentEmployeeId.HasValue && WorkOrderEmployees.Any(item => item.Id == CurrentEmployeeId.Value)
        ? CurrentEmployeeId.Value
        : WorkOrderEmployees[0].Id;
    }

    ShowCleaningModal = true;
  }

  protected async Task CreateReservationFromSelectionAsync()
  {
    if (!CanUseCalendarActions)
    {
      UiMessages.ShowWarning("Este rol solo puede consultar el calendario.");
      return;
    }

    if (IsCreatingReservation || IsCreatingCleaningOrders)
    {
      return;
    }

    if (SelectedRoomCalendarIds.Count == 0)
    {
      UiMessages.ShowWarning("Selecciona una o mas celdas del calendario.");
      return;
    }

    var selectedCells = GetSelectedCalendarCells();
    var availableCells = selectedCells.Where(IsAvailableForReservation).ToList();
    var skippedCount = selectedCells.Count - availableCells.Count;

    if (skippedCount > 0)
    {
      var confirm = await Js.InvokeAsync<bool>(
        "confirm",
        $"La selección incluye {skippedCount} celda(s) no disponible(s), reservada(s) o bloqueada(s). ¿Deseas crear la reservación solo con las {availableCells.Count} celda(s) disponible(s)?");

      if (!confirm)
      {
        return;
      }
    }

    if (availableCells.Count == 0)
    {
      UiMessages.ShowWarning("No hay celdas disponibles en la selección para crear una reservación.");
      return;
    }

    IsCreatingReservation = true;
    try
    {
      var cliente = await ReservacionesService.GetDefaultClienteForNewReservationAsync();
      if (cliente is null || cliente.Id <= 0)
      {
        UiMessages.ShowError("No se encontró un cliente de cotización para crear la reservación.");
        return;
      }

      var checkIn = availableCells.Min(cell => cell.RoomDate).Date;
      var checkOut = availableCells.Max(cell => cell.RoomDate).Date.AddDays(1);
      var status = "NUEVA";
      var notes = BuildCalendarReservationNotes(availableCells.Count, skippedCount);
      var totals = ReservacionTotalsCalculator.Calculate(
        checkIn,
        checkOut,
        taxable: true,
        suiteLineTotals: availableCells.Select(cell => cell.Price),
        extraLineTotals: Array.Empty<decimal>(),
        totalPagado: 0m);

      var reservationId = await ReservacionesService.CreateReservationAsync(new ListaReservacionCreateRequest
      {
        ClienteId = cliente.Id,
        Notes = notes
      });

      var attachResult = await ReservacionesService.AddSuitesToReservationAsync(
        reservationId,
        status,
        cliente.Nombre,
        availableCells.Select(cell => cell.RoomCalendarId!.Value).ToArray());

      if (!attachResult.Success)
      {
        UiMessages.ShowError(attachResult.Message);
        return;
      }

      var saveResult = await ReservacionesService.SaveReservationAsync(new ReservacionUpdateRequest
      {
        Id = reservationId,
        ClienteId = cliente.Id,
        CheckIn = checkIn,
        CheckOut = checkOut,
        Status = status,
        Notes = notes,
        Taxable = true,
        TotalPrice = totals.TotalReservacion
      });

      if (!saveResult.Success)
      {
        UiMessages.ShowError(saveResult.Message);
        return;
      }

      UiMessages.ShowSuccess($"Reservación {reservationId} creada con {availableCells.Count} celda(s) disponible(s).");
      SelectedRoomCalendarIds.Clear();
      await LoadCalendarAsync();
      Navigation.NavigateTo(GetReservationHref(reservationId));
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo crear la reservación desde el calendario. {ex.Message}");
    }
    finally
    {
      IsCreatingReservation = false;
    }
  }

  protected void CloseCleaningModal()
  {
    if (IsCreatingCleaningOrders)
    {
      return;
    }

    ShowCleaningModal = false;
  }

  protected void ToggleCleaningHelper(int employeeId, ChangeEventArgs args)
  {
    if (args.Value is bool selected && selected)
    {
      CleaningHelperIds.Add(employeeId);
      return;
    }

    if (bool.TryParse(args.Value?.ToString(), out var parsed) && parsed)
    {
      CleaningHelperIds.Add(employeeId);
      return;
    }

    CleaningHelperIds.Remove(employeeId);
  }

  protected async Task CreateCleaningOrdersAsync()
  {
    if (!CanUseCalendarActions)
    {
      UiMessages.ShowWarning("Este rol solo puede consultar el calendario.");
      return;
    }

    if (IsCreatingReservation)
    {
      return;
    }

    if (CleaningOwnerEmployeeId <= 0)
    {
      UiMessages.ShowWarning("Selecciona un responsable para las ordenes de limpieza.");
      return;
    }

    IsCreatingCleaningOrders = true;
    try
    {
      var result = await OrdenTrabajoService.CreateCleaningFromCalendarAsync(new OrdenTrabajoCalendarCreateRequest
      {
        Rfc = CurrentRfc,
        OwnerEmployeeId = CleaningOwnerEmployeeId,
        HelperEmployeeIds = CleaningHelperIds.ToList(),
        RoomCalendarIds = SelectedRoomCalendarIds.ToList(),
        CreatedBy = CurrentUserName
      });

      var failures = result.Cells
        .Where(item => !item.Success)
        .Select(item => item.Message)
        .Distinct()
        .Take(3)
        .ToList();

      if (result.Success)
      {
        UiMessages.ShowSuccess(result.Message);
      }
      else if (failures.Count > 0)
      {
        UiMessages.ShowWarning($"{result.Message} {string.Join(" | ", failures)}");
      }
      else
      {
        UiMessages.ShowWarning(result.Message);
      }

      SelectedRoomCalendarIds.Clear();
      ShowCleaningModal = false;
      await LoadCalendarWorkOrderBadgesAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudieron crear las ordenes de limpieza. {ex.Message}");
    }
    finally
    {
      IsCreatingCleaningOrders = false;
    }
  }

  private void BuildVisibleDates()
  {
    VisibleDates = new List<DateTime>();
    if (Timeline is null)
      return;

    var startDate = Timeline.StartDate.Date;
    var totalDays = Math.Max((Timeline.EndDateExclusive.Date - startDate).Days, 0);
    for (var i = 0; i < totalDays; i++)
    {
      VisibleDates.Add(startDate.AddDays(i));
    }
  }

  private void BuildLookup()
  {
    CellLookup = Timeline?.DayCells.ToDictionary(
      keySelector: x => (x.RoomId, x.RoomDate.Date),
      elementSelector: x => x)
      ?? new Dictionary<(int RoomId, DateTime RoomDate), RoomCalendarDayCellDto>();
  }

  private async Task LoadEmployeeOptionsAsync()
  {
    WorkOrderEmployees = (await OrdenTrabajoService.GetActiveEmployeeOptionsAsync(CurrentRfc)).ToList();
    if (CleaningOwnerEmployeeId <= 0 && WorkOrderEmployees.Count > 0)
    {
      CleaningOwnerEmployeeId = CurrentEmployeeId.HasValue && WorkOrderEmployees.Any(item => item.Id == CurrentEmployeeId.Value)
        ? CurrentEmployeeId.Value
        : WorkOrderEmployees[0].Id;
    }
  }

  private async Task LoadCalendarWorkOrderBadgesAsync()
  {
    if (VisibleDates.Count == 0)
    {
      WorkOrderBadgesByRoomCalendar.Clear();
      return;
    }

    var badges = await OrdenTrabajoService.GetCalendarBadgesAsync(Filter.StartDate, Filter.EndDateExclusive.AddDays(1));
    WorkOrderBadgesByRoomCalendar = badges
      .GroupBy(item => item.RoomCalendarId)
      .ToDictionary(group => group.Key, group => group.ToList());
  }

  private void PruneSelectionToVisibleCells()
  {
    var visibleRoomCalendarIds = Timeline?.DayCells
      .Where(item => item.RoomCalendarId.HasValue)
      .Select(item => item.RoomCalendarId!.Value)
      .ToHashSet()
      ?? new HashSet<int>();

    SelectedRoomCalendarIds.IntersectWith(visibleRoomCalendarIds);
  }

  private List<RoomCalendarDayCellDto> GetSelectedCalendarCells()
  {
    if (Timeline is null || SelectedRoomCalendarIds.Count == 0)
    {
      return new List<RoomCalendarDayCellDto>();
    }

    return Timeline.DayCells
      .Where(item => item.RoomCalendarId.HasValue && SelectedRoomCalendarIds.Contains(item.RoomCalendarId.Value))
      .ToList();
  }

  private static bool IsAvailableForReservation(RoomCalendarDayCellDto cell)
    => cell.RoomCalendarId.HasValue
      && string.Equals(cell.StateCode, "available", StringComparison.OrdinalIgnoreCase)
      && !cell.IsLocked
      && !cell.ReservationId.HasValue;

  private static string BuildCalendarReservationNotes(int usedCellCount, int skippedCellCount)
  {
    var notes = $"Creada desde selección de calendario. Celdas usadas: {usedCellCount}.";
    if (skippedCellCount > 0)
    {
      notes += $" Celdas omitidas por no estar disponibles: {skippedCellCount}.";
    }

    return notes;
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

    var appUser = await UserManager.GetUserAsync(user);
    CurrentEmployeeId = appUser?.EmployeeId;
    CanUseCalendarActions = user.IsInRole("Administrador")
      || user.IsInRole("SatOperator")
      || user.IsInRole("OrdenTrabajoOperador");
  }

  private static RoomCalendarTimelineFilter CreateDefaultFilter()
  {
    var today = DateTime.Today;
    return new RoomCalendarTimelineFilter
    {
      StartDate = today.AddDays(-3),
      EndDateExclusive = today.AddDays(21),
      RoomType = "SUITE"
    };
  }
}
