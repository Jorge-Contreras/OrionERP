using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Reservaciones.CalendarSync;

public interface IOutlookRoomCalendarSyncRepository
{
  Task<IReadOnlyList<OrionRoomCalendarBlock>> GetBlockedBlocksAsync(
    HospitalityScope scope,
    DateTime startDate,
    DateTime endDateExclusive,
    IReadOnlyCollection<string> roomNames,
    CancellationToken ct = default);

  Task<IReadOnlyList<OutlookRoomCalendarSyncMapping>> GetMappingsAsync(
    HospitalityScope scope,
    DateTime startDate,
    DateTime endDateExclusive,
    IReadOnlyCollection<string> roomNames,
    CancellationToken ct = default);

  Task UpsertMappingsAsync(
    HospitalityScope scope,
    IReadOnlyCollection<OutlookRoomCalendarSyncMappingUpsert> mappings,
    CancellationToken ct = default);

  Task DeleteMappingsAsync(
    HospitalityScope scope,
    IReadOnlyCollection<int> mappingIds,
    CancellationToken ct = default);
}
