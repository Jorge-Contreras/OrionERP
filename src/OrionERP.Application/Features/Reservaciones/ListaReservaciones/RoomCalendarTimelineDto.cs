using System;
using System.Collections.Generic;

namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public sealed class RoomCalendarTimelineDto
{
  public DateTime StartDate { get; set; }
  public DateTime EndDateExclusive { get; set; }
  public IReadOnlyList<RoomCalendarResourceDto> Resources { get; set; } = Array.Empty<RoomCalendarResourceDto>();
  public IReadOnlyList<RoomCalendarDayCellDto> DayCells { get; set; } = Array.Empty<RoomCalendarDayCellDto>();
  public IReadOnlyList<RoomCalendarEventDto> Events { get; set; } = Array.Empty<RoomCalendarEventDto>();
}
