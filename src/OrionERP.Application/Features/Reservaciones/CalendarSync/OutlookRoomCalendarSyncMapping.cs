using System;

namespace OrionERP.Application.Features.Reservaciones.CalendarSync;

public sealed class OutlookRoomCalendarSyncMapping
{
  public int Id { get; set; }
  public string SourceKey { get; set; } = string.Empty;
  public string RoomName { get; set; } = string.Empty;
  public int? ReservationId { get; set; }
  public DateTime StartDate { get; set; }
  public DateTime EndDateExclusive { get; set; }
  public string OutlookCalendarId { get; set; } = string.Empty;
  public string OutlookEventId { get; set; } = string.Empty;
  public string ContentHash { get; set; } = string.Empty;
  public DateTime LastSyncedUtc { get; set; }
}
