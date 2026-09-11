namespace OrionERP.Application.Features.Reservaciones.CalendarSync;

public sealed class BonhomiaRoomCalendarRoomResult
{
  public string RoomName { get; set; } = string.Empty;
  public string? OutlookCalendarId { get; set; }
  public int LocalBlockCount { get; set; }
  public int RemoteOwnedEventCount { get; set; }
  public int CreatedCount { get; set; }
  public int UpdatedCount { get; set; }
  public int DeletedCount { get; set; }
  public int SkippedCount { get; set; }
  public int RecoveredMappingCount { get; set; }
  public string? ErrorMessage { get; set; }
}
