using System;

namespace OrionERP.Application.Features.Reservaciones.CalendarSync;

public sealed class OrionRoomCalendarBlock
{
  public string SourceKey { get; set; } = string.Empty;
  public string RoomName { get; set; } = string.Empty;
  public int? ReservationId { get; set; }
  public DateTime StartDate { get; set; }
  public DateTime EndDateExclusive { get; set; }
  public string? LockedBy { get; set; }
  public string? LockDescription { get; set; }
  public string? Status { get; set; }
}
