using System;
using System.Collections.Generic;
using System.Linq;
using OrionERP.Application.Features.Reservaciones.CalendarSync;

namespace OrionERP.Infrastructure.Features.Reservaciones.CalendarSync;

public enum BonhomiaCalendarSyncOperationType
{
  Create,
  Update,
  DeleteRemoteEvent,
  RecoverMapping,
  DeleteMapping,
  Skip
}

public sealed class BonhomiaCalendarSyncOperation
{
  public BonhomiaCalendarSyncOperationType Type { get; init; }
  public OrionRoomCalendarBlock? LocalBlock { get; init; }
  public OutlookRoomCalendarSyncMapping? Mapping { get; init; }
  public BonhomiaGraphCalendarRemoteEvent? RemoteEvent { get; init; }
  public OutlookRoomCalendarSyncMappingUpsert? MappingUpsert { get; init; }
}

public static class BonhomiaCalendarSyncReconciler
{
  public static IReadOnlyList<BonhomiaCalendarSyncOperation> BuildOperations(
    string roomName,
    string calendarId,
    IEnumerable<OrionRoomCalendarBlock> localBlocks,
    IEnumerable<OutlookRoomCalendarSyncMapping> mappings,
    IEnumerable<BonhomiaGraphCalendarRemoteEvent> remoteEvents)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(roomName);
    ArgumentException.ThrowIfNullOrWhiteSpace(calendarId);
    ArgumentNullException.ThrowIfNull(localBlocks);
    ArgumentNullException.ThrowIfNull(mappings);
    ArgumentNullException.ThrowIfNull(remoteEvents);

    var operations = new List<BonhomiaCalendarSyncOperation>();
    var localBySourceKey = localBlocks
      .GroupBy(block => block.SourceKey, StringComparer.OrdinalIgnoreCase)
      .ToDictionary(group => group.Key, group => group.OrderBy(block => block.StartDate).First(), StringComparer.OrdinalIgnoreCase);

    var mappingsBySourceKey = mappings
      .GroupBy(mapping => mapping.SourceKey, StringComparer.OrdinalIgnoreCase)
      .ToDictionary(group => group.Key, group => group.OrderByDescending(mapping => mapping.LastSyncedUtc).First(), StringComparer.OrdinalIgnoreCase);

    var remoteById = remoteEvents
      .GroupBy(remote => remote.Id, StringComparer.OrdinalIgnoreCase)
      .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

    var remoteBySourceKey = remoteEvents
      .Where(remote => !string.IsNullOrWhiteSpace(remote.SourceKey))
      .GroupBy(remote => remote.SourceKey!, StringComparer.OrdinalIgnoreCase)
      .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.EndDateExclusive).First(), StringComparer.OrdinalIgnoreCase);

    var matchedRemoteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var matchedMappingIds = new HashSet<int>();

    foreach (var localBlock in localBySourceKey.Values.OrderBy(item => item.StartDate).ThenBy(item => item.RoomName, StringComparer.OrdinalIgnoreCase))
    {
      mappingsBySourceKey.TryGetValue(localBlock.SourceKey, out var mapping);
      BonhomiaGraphCalendarRemoteEvent? remoteEvent = null;

      if (mapping is not null && remoteById.TryGetValue(mapping.OutlookEventId, out var mappedRemoteEvent))
      {
        remoteEvent = mappedRemoteEvent;
      }
      else if (remoteBySourceKey.TryGetValue(localBlock.SourceKey, out var recoveredRemoteEvent))
      {
        remoteEvent = recoveredRemoteEvent;
      }

      if (mapping is not null)
      {
        matchedMappingIds.Add(mapping.Id);
      }

      if (remoteEvent is null)
      {
        operations.Add(new BonhomiaCalendarSyncOperation
        {
          Type = BonhomiaCalendarSyncOperationType.Create,
          LocalBlock = localBlock
        });
        continue;
      }

      matchedRemoteIds.Add(remoteEvent.Id);

      var desiredHash = BonhomiaCalendarSyncPayloadBuilder.ComputeContentHash(localBlock);
      var mappingUpsert = BuildMappingUpsert(localBlock, calendarId, remoteEvent.Id, desiredHash);
      var remoteHash = BonhomiaCalendarSyncPayloadBuilder.ComputeRemoteContentHash(remoteEvent);
      var mappingNeedsRefresh = MappingNeedsRefresh(mapping, mappingUpsert);

      if (!string.Equals(remoteHash, desiredHash, StringComparison.Ordinal))
      {
        operations.Add(new BonhomiaCalendarSyncOperation
        {
          Type = BonhomiaCalendarSyncOperationType.Update,
          LocalBlock = localBlock,
          Mapping = mapping,
          RemoteEvent = remoteEvent,
          MappingUpsert = mappingUpsert
        });
        continue;
      }

      if (mappingNeedsRefresh)
      {
        operations.Add(new BonhomiaCalendarSyncOperation
        {
          Type = BonhomiaCalendarSyncOperationType.RecoverMapping,
          LocalBlock = localBlock,
          Mapping = mapping,
          RemoteEvent = remoteEvent,
          MappingUpsert = mappingUpsert
        });
        continue;
      }

      operations.Add(new BonhomiaCalendarSyncOperation
      {
        Type = BonhomiaCalendarSyncOperationType.Skip,
        LocalBlock = localBlock,
        Mapping = mapping,
        RemoteEvent = remoteEvent
      });
    }

    foreach (var mapping in mappings.Where(item => !matchedMappingIds.Contains(item.Id)))
    {
      if (localBySourceKey.ContainsKey(mapping.SourceKey))
      {
        continue;
      }

      if (remoteById.TryGetValue(mapping.OutlookEventId, out var remoteEvent))
      {
        matchedRemoteIds.Add(remoteEvent.Id);
        operations.Add(new BonhomiaCalendarSyncOperation
        {
          Type = BonhomiaCalendarSyncOperationType.DeleteRemoteEvent,
          Mapping = mapping,
          RemoteEvent = remoteEvent
        });
      }
      else
      {
        operations.Add(new BonhomiaCalendarSyncOperation
        {
          Type = BonhomiaCalendarSyncOperationType.DeleteMapping,
          Mapping = mapping
        });
      }
    }

    foreach (var remoteEvent in remoteEvents.Where(item => !matchedRemoteIds.Contains(item.Id)))
    {
      if (string.IsNullOrWhiteSpace(remoteEvent.SourceKey))
      {
        continue;
      }

      if (localBySourceKey.ContainsKey(remoteEvent.SourceKey))
      {
        continue;
      }

      operations.Add(new BonhomiaCalendarSyncOperation
      {
        Type = BonhomiaCalendarSyncOperationType.DeleteRemoteEvent,
        RemoteEvent = remoteEvent
      });
    }

    return operations;
  }

  private static OutlookRoomCalendarSyncMappingUpsert BuildMappingUpsert(
    OrionRoomCalendarBlock localBlock,
    string calendarId,
    string eventId,
    string contentHash)
  {
    return new OutlookRoomCalendarSyncMappingUpsert
    {
      SourceKey = localBlock.SourceKey,
      RoomName = localBlock.RoomName,
      ReservationId = localBlock.ReservationId,
      StartDate = localBlock.StartDate,
      EndDateExclusive = localBlock.EndDateExclusive,
      OutlookCalendarId = calendarId,
      OutlookEventId = eventId,
      ContentHash = contentHash
    };
  }

  private static bool MappingNeedsRefresh(
    OutlookRoomCalendarSyncMapping? existingMapping,
    OutlookRoomCalendarSyncMappingUpsert desiredMapping)
  {
    if (existingMapping is null)
    {
      return true;
    }

    return !string.Equals(existingMapping.OutlookCalendarId, desiredMapping.OutlookCalendarId, StringComparison.OrdinalIgnoreCase) ||
           !string.Equals(existingMapping.OutlookEventId, desiredMapping.OutlookEventId, StringComparison.OrdinalIgnoreCase) ||
           !string.Equals(existingMapping.ContentHash, desiredMapping.ContentHash, StringComparison.Ordinal) ||
           !string.Equals(existingMapping.RoomName, desiredMapping.RoomName, StringComparison.OrdinalIgnoreCase) ||
           existingMapping.ReservationId != desiredMapping.ReservationId ||
           existingMapping.StartDate != desiredMapping.StartDate ||
           existingMapping.EndDateExclusive != desiredMapping.EndDateExclusive;
  }
}
