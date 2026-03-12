using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.Web.Features.Reservaciones.Calendario;

[Authorize(Roles = "Administrador,SatOperator")]
public partial class CalendarioReservacionesPage : ComponentBase
{
  [Inject] public IListaReservacionesService ReservacionesService { get; set; } = default!;

  protected RoomCalendarTimelineFilter Filter { get; set; } = CreateDefaultFilter();
  protected RoomCalendarTimelineDto? Timeline { get; set; }
  protected List<DateTime> VisibleDates { get; set; } = new();
  protected Dictionary<(int RoomId, DateTime RoomDate), RoomCalendarDayCellDto> CellLookup { get; set; } = new();
  protected bool IsLoading { get; set; }
  protected string? ErrorMessage { get; set; }
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

  protected override async Task OnInitializedAsync()
  {
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
    }
    catch (Exception ex)
    {
      ErrorMessage = ex.Message;
      Timeline = null;
      VisibleDates.Clear();
      CellLookup.Clear();
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

  protected static string GetCellTooltip(RoomCalendarDayCellDto? cell)
  {
    if (cell is null)
      return "No existe fila en ROOM_CALENDAR para esta fecha.";

    var parts = new List<string>
    {
      $"Estado: {GetStateLabel(cell.StateCode)}",
      $"Precio: {cell.Price:C2}"
    };

    if (cell.ReservationId.HasValue)
      parts.Add($"Reservación: {cell.ReservationId}");

    if (!string.IsNullOrWhiteSpace(cell.LockedBy))
      parts.Add($"Bloqueado por: {cell.LockedBy}");

    if (!string.IsNullOrWhiteSpace(cell.DataQualityFlag))
      parts.Add($"Calidad: {cell.DataQualityFlag}");

    return string.Join(" | ", parts);
  }

  protected static string FormatCurrency(decimal amount)
    => amount.ToString("C2", CultureInfo.GetCultureInfo("es-MX"));

  protected static string GetReservationHref(int reservationId)
    => $"/reservaciones/{reservationId}";

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
