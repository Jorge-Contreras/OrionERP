using System;

namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public sealed class RoomCalendarTimelineFilter
{
  public DateTime StartDate { get; set; } = DateTime.Today;
  public DateTime EndDateExclusive { get; set; } = DateTime.Today.AddDays(14);
  public string RoomType { get; set; } = "SUITE";
}
