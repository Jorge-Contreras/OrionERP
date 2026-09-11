using System;
using System.Collections.Generic;

namespace OrionERP.Application.Features.Reservaciones.CalendarSync;

public sealed class BonhomiaRoomCalendarSyncResult
{
  public DateTime StartDate { get; set; }
  public DateTime EndDateExclusive { get; set; }
  public int CreatedCount { get; set; }
  public int UpdatedCount { get; set; }
  public int DeletedCount { get; set; }
  public int SkippedCount { get; set; }
  public int RecoveredMappingCount { get; set; }
  public int ErrorCount { get; set; }
  public IReadOnlyList<BonhomiaRoomCalendarRoomResult> Rooms { get; set; } = Array.Empty<BonhomiaRoomCalendarRoomResult>();
}
