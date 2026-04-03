using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Reservaciones.CalendarSync;

public interface IOutlookRoomCalendarSyncRepository
{
  Task<IReadOnlyList<OrionRoomCalendarBlock>> GetBlockedBlocksAsync(
    DateTime startDate,
    DateTime endDateExclusive,
    IReadOnlyCollection<string> roomNames,
    CancellationToken ct = default);

  Task<IReadOnlyList<OutlookRoomCalendarSyncMapping>> GetMappingsAsync(
    DateTime startDate,
    DateTime endDateExclusive,
    IReadOnlyCollection<string> roomNames,
    CancellationToken ct = default);

  Task UpsertMappingsAsync(
    IReadOnlyCollection<OutlookRoomCalendarSyncMappingUpsert> mappings,
    CancellationToken ct = default);

  Task DeleteMappingsAsync(
    IReadOnlyCollection<int> mappingIds,
    CancellationToken ct = default);
}
