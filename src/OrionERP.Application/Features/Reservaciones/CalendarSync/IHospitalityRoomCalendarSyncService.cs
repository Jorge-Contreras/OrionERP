using System;
using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Reservaciones.CalendarSync;

public interface IHospitalityRoomCalendarSyncService
{
  Task<HospitalityRoomCalendarSyncResult> SyncAsync(
    DateTime startDate,
    DateTime endDateExclusive,
    CancellationToken ct = default);
}
