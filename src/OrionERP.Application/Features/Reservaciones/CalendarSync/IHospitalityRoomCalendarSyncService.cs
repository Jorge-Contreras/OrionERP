using System;
using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Reservaciones.CalendarSync;

public interface IBonhomiaRoomCalendarSyncService
{
  Task<BonhomiaRoomCalendarSyncResult> SyncAsync(
    DateTime startDate,
    DateTime endDateExclusive,
    CancellationToken ct = default);
}
