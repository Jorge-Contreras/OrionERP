using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OrionERP.Application.Features.Reservaciones.CalendarSync;

namespace OrionERP.Infrastructure.Features.Reservaciones.CalendarSync;

public sealed class OrionRoomCalendarLockRow
{
  public string RoomName { get; set; } = string.Empty;
  public DateTime RoomDate { get; set; }
  public int? ReservationId { get; set; }
  public string? LockedBy { get; set; }
  public string? LockDescription { get; set; }
  public string? Status { get; set; }
}

public static class BonhomiaCalendarSyncBlockBuilder
{
  public static IReadOnlyList<OrionRoomCalendarBlock> BuildBlocks(IEnumerable<OrionRoomCalendarLockRow> rows)
  {
    ArgumentNullException.ThrowIfNull(rows);

    var orderedRows = rows
      .Where(row => !string.IsNullOrWhiteSpace(row.RoomName))
      .Select(row =>
      {
        row.RoomName = row.RoomName.Trim();
        row.RoomDate = row.RoomDate.Date;
        row.LockedBy = TrimOrNull(row.LockedBy);
        row.LockDescription = TrimOrNull(row.LockDescription);
        row.Status = TrimOrNull(row.Status);
        row.ReservationId = row.ReservationId is > 0 ? row.ReservationId : null;
        return row;
      })
      .OrderBy(row => row.RoomName, StringComparer.OrdinalIgnoreCase)
      .ThenBy(row => row.RoomDate)
      .ToArray();

    if (orderedRows.Length == 0)
    {
      return Array.Empty<OrionRoomCalendarBlock>();
    }

    var blocks = new List<OrionRoomCalendarBlock>();
    OrionRoomCalendarBlock? currentBlock = null;
    OrionRoomCalendarLockRow? previousRow = null;

    foreach (var row in orderedRows)
    {
      if (currentBlock is null || previousRow is null || !CanAppend(currentBlock, previousRow, row))
      {
        currentBlock = StartBlock(row);
        blocks.Add(currentBlock);
      }
      else
      {
        currentBlock.EndDateExclusive = row.RoomDate.AddDays(1);
        if (!currentBlock.ReservationId.HasValue)
        {
          currentBlock.SourceKey = BuildSourceKey(
            currentBlock.RoomName,
            reservationId: null,
            currentBlock.StartDate,
            currentBlock.EndDateExclusive,
            currentBlock.LockDescription);
        }
      }

      previousRow = row;
    }

    return blocks;
  }

  private static OrionRoomCalendarBlock StartBlock(OrionRoomCalendarLockRow row)
  {
    var startDate = row.RoomDate.Date;
    var endDateExclusive = startDate.AddDays(1);
    return new OrionRoomCalendarBlock
    {
      SourceKey = BuildSourceKey(row.RoomName, row.ReservationId, startDate, endDateExclusive, row.LockDescription),
      RoomName = row.RoomName,
      ReservationId = row.ReservationId,
      StartDate = startDate,
      EndDateExclusive = endDateExclusive,
      LockedBy = row.LockedBy,
      LockDescription = row.LockDescription,
      Status = row.Status
    };
  }

  private static bool CanAppend(
    OrionRoomCalendarBlock currentBlock,
    OrionRoomCalendarLockRow previousRow,
    OrionRoomCalendarLockRow nextRow)
  {
    if (!string.Equals(currentBlock.RoomName, nextRow.RoomName, StringComparison.OrdinalIgnoreCase))
    {
      return false;
    }

    if (nextRow.RoomDate != previousRow.RoomDate.AddDays(1))
    {
      return false;
    }

    if (currentBlock.ReservationId.HasValue || nextRow.ReservationId.HasValue)
    {
      return currentBlock.ReservationId.HasValue &&
             nextRow.ReservationId.HasValue &&
             currentBlock.ReservationId == nextRow.ReservationId;
    }

    return string.Equals(
             NormalizeManualValue(currentBlock.LockDescription),
             NormalizeManualValue(nextRow.LockDescription),
             StringComparison.Ordinal) &&
           string.Equals(
             NormalizeManualValue(currentBlock.LockedBy),
             NormalizeManualValue(nextRow.LockedBy),
             StringComparison.Ordinal);
  }

  private static string BuildSourceKey(
    string roomName,
    int? reservationId,
    DateTime startDate,
    DateTime endDateExclusive,
    string? lockDescription)
  {
    if (reservationId.HasValue)
    {
      return string.Create(
        CultureInfo.InvariantCulture,
        $"reservation:{reservationId.Value}:{roomName}");
    }

    var normalizedDescription = NormalizeManualValue(lockDescription);
    return string.Create(
      CultureInfo.InvariantCulture,
      $"manual:{roomName}:{startDate:yyyyMMdd}:{endDateExclusive:yyyyMMdd}:{normalizedDescription}");
  }

  private static string NormalizeManualValue(string? value)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return "BLANK";
    }

    return string.Join(
      ' ',
      value
        .Trim()
        .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
      .ToUpperInvariant();
  }

  private static string? TrimOrNull(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
