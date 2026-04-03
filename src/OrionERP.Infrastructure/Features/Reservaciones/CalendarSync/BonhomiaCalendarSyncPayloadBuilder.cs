using System;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using OrionERP.Application.Features.Reservaciones.CalendarSync;

namespace OrionERP.Infrastructure.Features.Reservaciones.CalendarSync;

public static class BonhomiaCalendarSyncPayloadBuilder
{
  public const string Subject = "ORION BLOCKED";
  private const string MarkerPrefix = "<!-- OrionSync:";
  private const string MarkerSuffix = " -->";

  public static string BuildBodyHtml(OrionRoomCalendarBlock block)
  {
    ArgumentNullException.ThrowIfNull(block);

    var sourceKey = WebUtility.HtmlEncode(block.SourceKey);
    var roomName = WebUtility.HtmlEncode(block.RoomName);
    var reservationId = block.ReservationId?.ToString(CultureInfo.InvariantCulture) ?? "manual";
    var startDate = block.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    var endDate = block.EndDateExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    return $"""
<p>Managed by OrionERP calendar sync.</p>
{MarkerPrefix}{sourceKey}{MarkerSuffix}
<!-- Room:{roomName} -->
<!-- Reservation:{reservationId} -->
<!-- Start:{startDate} -->
<!-- EndExclusive:{endDate} -->
""";
  }

  public static string ComputeContentHash(OrionRoomCalendarBlock block)
  {
    ArgumentNullException.ThrowIfNull(block);
    return ComputeHash(
      Subject,
      block.SourceKey,
      block.StartDate,
      block.EndDateExclusive,
      isAllDay: true,
      showAs: "busy");
  }

  public static string ComputeRemoteContentHash(BonhomiaGraphCalendarRemoteEvent remoteEvent)
  {
    ArgumentNullException.ThrowIfNull(remoteEvent);
    return ComputeHash(
      remoteEvent.Subject,
      remoteEvent.SourceKey ?? string.Empty,
      remoteEvent.StartDate,
      remoteEvent.EndDateExclusive,
      remoteEvent.IsAllDay,
      remoteEvent.ShowAs);
  }

  public static bool TryExtractSourceKey(string? bodyHtml, out string sourceKey)
  {
    sourceKey = string.Empty;
    if (string.IsNullOrWhiteSpace(bodyHtml))
    {
      return false;
    }

    var startIndex = bodyHtml.IndexOf(MarkerPrefix, StringComparison.OrdinalIgnoreCase);
    if (startIndex < 0)
    {
      return false;
    }

    startIndex += MarkerPrefix.Length;
    var endIndex = bodyHtml.IndexOf(MarkerSuffix, startIndex, StringComparison.OrdinalIgnoreCase);
    if (endIndex < 0)
    {
      return false;
    }

    sourceKey = WebUtility.HtmlDecode(bodyHtml[startIndex..endIndex]).Trim();
    return !string.IsNullOrWhiteSpace(sourceKey);
  }

  private static string ComputeHash(
    string subject,
    string sourceKey,
    DateTime startDate,
    DateTime endDateExclusive,
    bool isAllDay,
    string showAs)
  {
    var payload = string.Create(
      CultureInfo.InvariantCulture,
      $"{subject}|{sourceKey}|{startDate:yyyy-MM-dd}|{endDateExclusive:yyyy-MM-dd}|{isAllDay}|{showAs}");

    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
    return Convert.ToHexString(bytes);
  }
}
