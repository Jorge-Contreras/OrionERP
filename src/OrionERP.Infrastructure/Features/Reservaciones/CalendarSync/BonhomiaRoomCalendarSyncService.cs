using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Reservaciones.CalendarSync;

namespace OrionERP.Infrastructure.Features.Reservaciones.CalendarSync;

public sealed class BonhomiaRoomCalendarSyncService : IBonhomiaRoomCalendarSyncService
{
  private const string MappingScriptPath = "src/OrionERP.Infrastructure/Features/Reservaciones/ListaReservaciones/Sql/20260327_room_calendar_outlook_sync.sql";
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

  private readonly HttpClient _httpClient;
  private readonly IOutlookRoomCalendarSyncRepository _repository;
  private readonly IOptions<BonhomiaGraphCalendarSyncOptions> _options;
  private readonly ILogger<BonhomiaRoomCalendarSyncService> _logger;

  public BonhomiaRoomCalendarSyncService(
    HttpClient httpClient,
    IOutlookRoomCalendarSyncRepository repository,
    IOptions<BonhomiaGraphCalendarSyncOptions> options,
    ILogger<BonhomiaRoomCalendarSyncService> logger)
  {
    _httpClient = httpClient;
    _repository = repository;
    _options = options;
    _logger = logger;
  }

  public async Task<BonhomiaRoomCalendarSyncResult> SyncAsync(
    DateTime startDate,
    DateTime endDateExclusive,
    CancellationToken ct = default)
  {
    var syncStartDate = startDate.Date;
    var syncEndDateExclusive = endDateExclusive.Date;
    if (syncEndDateExclusive <= syncStartDate)
    {
      throw new ArgumentException("EndDateExclusive must be after StartDate.", nameof(endDateExclusive));
    }

    var options = _options.Value;
    EnsureConfigured(options);

    var targetCalendars = options.GetTargetCalendars();
    var result = new BonhomiaRoomCalendarSyncResult
    {
      StartDate = syncStartDate,
      EndDateExclusive = syncEndDateExclusive
    };

    var accessToken = await RequestAccessTokenAsync(options, ct);
    var calendarLookup = await GetCalendarLookupAsync(options.MailboxAddress, accessToken, ct);

    IReadOnlyList<OrionRoomCalendarBlock> localBlocks;
    IReadOnlyList<OutlookRoomCalendarSyncMapping> mappings;
    try
    {
      localBlocks = await _repository.GetBlockedBlocksAsync(syncStartDate, syncEndDateExclusive, targetCalendars, ct);
      mappings = await _repository.GetMappingsAsync(syncStartDate, syncEndDateExclusive, targetCalendars, ct);
    }
    catch (SqlException ex) when (IsMissingMappingTable(ex))
    {
      throw new InvalidOperationException(
        $"Falta aplicar la tabla de sincronización para Outlook. Ejecuta el script {MappingScriptPath}.",
        ex);
    }

    var roomResults = new List<BonhomiaRoomCalendarRoomResult>();
    foreach (var roomName in targetCalendars)
    {
      if (!calendarLookup.TryGetValue(roomName, out var calendarId))
      {
        roomResults.Add(new BonhomiaRoomCalendarRoomResult
        {
          RoomName = roomName,
          ErrorMessage = $"No se encontró el calendario '{roomName}' en {options.MailboxAddress}."
        });
        continue;
      }

      var roomResult = new BonhomiaRoomCalendarRoomResult
      {
        RoomName = roomName,
        OutlookCalendarId = calendarId
      };

      try
      {
        var roomLocalBlocks = localBlocks
          .Where(block => string.Equals(block.RoomName, roomName, StringComparison.OrdinalIgnoreCase))
          .OrderBy(block => block.StartDate)
          .ToArray();

        var roomMappings = mappings
          .Where(mapping =>
            string.Equals(mapping.RoomName, roomName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(mapping.OutlookCalendarId, calendarId, StringComparison.OrdinalIgnoreCase))
          .ToArray();

        var roomRemoteEvents = await GetOwnedCalendarEventsAsync(
          options,
          accessToken,
          calendarId,
          syncStartDate,
          syncEndDateExclusive,
          roomMappings,
          ct);

        roomResult.LocalBlockCount = roomLocalBlocks.Length;
        roomResult.RemoteOwnedEventCount = roomRemoteEvents.Count;

        var operations = BonhomiaCalendarSyncReconciler.BuildOperations(
          roomName,
          calendarId,
          roomLocalBlocks,
          roomMappings,
          roomRemoteEvents);

        var mappingUpserts = new List<OutlookRoomCalendarSyncMappingUpsert>();
        var mappingDeletes = new List<int>();

        foreach (var operation in operations)
        {
          switch (operation.Type)
          {
            case BonhomiaCalendarSyncOperationType.Create:
            {
              var localBlock = operation.LocalBlock!;
              var createdEventId = await CreateEventAsync(options, accessToken, calendarId, localBlock, ct);
              mappingUpserts.Add(BuildMappingUpsert(localBlock, calendarId, createdEventId));
              roomResult.CreatedCount++;
              break;
            }

            case BonhomiaCalendarSyncOperationType.Update:
            {
              var localBlock = operation.LocalBlock!;
              var remoteEvent = operation.RemoteEvent!;
              await UpdateEventAsync(options, accessToken, calendarId, remoteEvent.Id, localBlock, ct);
              mappingUpserts.Add(BuildMappingUpsert(localBlock, calendarId, remoteEvent.Id));
              roomResult.UpdatedCount++;
              break;
            }

            case BonhomiaCalendarSyncOperationType.DeleteRemoteEvent:
            {
              var remoteEvent = operation.RemoteEvent!;
              await DeleteEventAsync(options, accessToken, calendarId, remoteEvent.Id, ct);
              if (operation.Mapping?.Id > 0)
              {
                mappingDeletes.Add(operation.Mapping.Id);
              }

              roomResult.DeletedCount++;
              break;
            }

            case BonhomiaCalendarSyncOperationType.RecoverMapping:
            {
              mappingUpserts.Add(operation.MappingUpsert!);
              roomResult.RecoveredMappingCount++;
              roomResult.SkippedCount++;
              break;
            }

            case BonhomiaCalendarSyncOperationType.DeleteMapping:
            {
              if (operation.Mapping?.Id > 0)
              {
                mappingDeletes.Add(operation.Mapping.Id);
              }

              roomResult.SkippedCount++;
              break;
            }

            case BonhomiaCalendarSyncOperationType.Skip:
            {
              roomResult.SkippedCount++;
              break;
            }
          }
        }

        if (mappingUpserts.Count > 0)
        {
          await _repository.UpsertMappingsAsync(mappingUpserts, ct);
        }

        if (mappingDeletes.Count > 0)
        {
          await _repository.DeleteMappingsAsync(mappingDeletes.Distinct().ToArray(), ct);
        }
      }
      catch (SqlException ex) when (IsMissingMappingTable(ex))
      {
        throw new InvalidOperationException(
          $"Falta aplicar la tabla de sincronización para Outlook. Ejecuta el script {MappingScriptPath}.",
          ex);
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Error syncing Outlook calendar for room {RoomName}.", roomName);
        roomResult.ErrorMessage = ex.Message;
      }

      roomResults.Add(roomResult);
    }

    result.Rooms = roomResults;
    result.CreatedCount = roomResults.Sum(item => item.CreatedCount);
    result.UpdatedCount = roomResults.Sum(item => item.UpdatedCount);
    result.DeletedCount = roomResults.Sum(item => item.DeletedCount);
    result.SkippedCount = roomResults.Sum(item => item.SkippedCount);
    result.RecoveredMappingCount = roomResults.Sum(item => item.RecoveredMappingCount);
    result.ErrorCount = roomResults.Count(item => !string.IsNullOrWhiteSpace(item.ErrorMessage));

    return result;
  }

  private async Task<string> RequestAccessTokenAsync(
    BonhomiaGraphCalendarSyncOptions options,
    CancellationToken ct)
  {
    using var request = new HttpRequestMessage(
      HttpMethod.Post,
      $"https://login.microsoftonline.com/{options.TenantId}/oauth2/v2.0/token")
    {
      Content = new FormUrlEncodedContent(new Dictionary<string, string>
      {
        ["client_id"] = options.ClientId,
        ["client_secret"] = options.ClientSecret,
        ["scope"] = "https://graph.microsoft.com/.default",
        ["grant_type"] = "client_credentials"
      })
    };

    using var response = await _httpClient.SendAsync(request, ct);
    var payload = await response.Content.ReadFromJsonAsync<GraphTokenResponse>(JsonOptions, ct);

    if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(payload?.AccessToken))
    {
      var responseBody = await response.Content.ReadAsStringAsync(ct);
      _logger.LogError(
        "Bonhomia Graph token request failed with status code {StatusCode}. Response: {ResponseBody}",
        (int)response.StatusCode,
        responseBody);

      throw new InvalidOperationException("No se pudo obtener el token de acceso de Bonhomia para Microsoft Graph.");
    }

    return payload.AccessToken!;
  }

  private async Task<Dictionary<string, string>> GetCalendarLookupAsync(
    string mailboxAddress,
    string accessToken,
    CancellationToken ct)
  {
    var calendars = await ReadPagedCollectionAsync<GraphCalendarPayload>(
      $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(mailboxAddress)}/calendars?$select=id,name&$top=100",
      accessToken,
      ct);

    return calendars
      .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Id))
      .GroupBy(item => item.Name!.Trim(), StringComparer.OrdinalIgnoreCase)
      .ToDictionary(group => group.Key, group => group.First().Id!, StringComparer.OrdinalIgnoreCase);
  }

  private async Task<IReadOnlyList<BonhomiaGraphCalendarRemoteEvent>> GetOwnedCalendarEventsAsync(
    BonhomiaGraphCalendarSyncOptions options,
    string accessToken,
    string calendarId,
    DateTime startDate,
    DateTime endDateExclusive,
    IReadOnlyCollection<OutlookRoomCalendarSyncMapping> roomMappings,
    CancellationToken ct)
  {
    var timeZoneInfo = ResolveTimeZoneInfo(options.TimeZone);
    var startOffset = new DateTimeOffset(startDate, timeZoneInfo.GetUtcOffset(startDate));
    var endOffset = new DateTimeOffset(endDateExclusive, timeZoneInfo.GetUtcOffset(endDateExclusive));

    var url =
      $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(options.MailboxAddress)}/calendars/{Uri.EscapeDataString(calendarId)}/calendarView" +
      $"?startDateTime={Uri.EscapeDataString(startOffset.ToString("O", CultureInfo.InvariantCulture))}" +
      $"&endDateTime={Uri.EscapeDataString(endOffset.ToString("O", CultureInfo.InvariantCulture))}" +
      "&$select=id,subject,body,start,end,isAllDay,showAs&$top=100";

    var payloads = await ReadPagedCollectionAsync<GraphEventPayload>(url, accessToken, ct);
    var mappedIds = roomMappings
      .Where(item => !string.IsNullOrWhiteSpace(item.OutlookEventId))
      .Select(item => item.OutlookEventId)
      .ToHashSet(StringComparer.OrdinalIgnoreCase);

    return payloads
      .Where(item => !string.IsNullOrWhiteSpace(item.Id) && item.Start is not null && item.End is not null)
      .Select(MapRemoteEvent)
      .Where(item =>
        item is not null &&
        (!string.IsNullOrWhiteSpace(item.SourceKey) || mappedIds.Contains(item.Id)))
      .Cast<BonhomiaGraphCalendarRemoteEvent>()
      .ToArray();
  }

  private async Task<string> CreateEventAsync(
    BonhomiaGraphCalendarSyncOptions options,
    string accessToken,
    string calendarId,
    OrionRoomCalendarBlock block,
    CancellationToken ct)
  {
    var payload = BuildGraphEventRequest(options, block);
    using var request = new HttpRequestMessage(
      HttpMethod.Post,
      $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(options.MailboxAddress)}/calendars/{Uri.EscapeDataString(calendarId)}/events");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    request.Content = JsonContent.Create(payload, options: JsonOptions);

    using var response = await _httpClient.SendAsync(request, ct);
    if (!response.IsSuccessStatusCode)
    {
      await ThrowGraphRequestExceptionAsync(response, $"No se pudo crear el evento en el calendario {calendarId}.", ct);
    }

    var created = await response.Content.ReadFromJsonAsync<GraphEventPayload>(JsonOptions, ct);
    if (string.IsNullOrWhiteSpace(created?.Id))
    {
      throw new InvalidOperationException($"Microsoft Graph no devolvió el ID del evento creado para {block.RoomName}.");
    }

    return created.Id;
  }

  private async Task UpdateEventAsync(
    BonhomiaGraphCalendarSyncOptions options,
    string accessToken,
    string calendarId,
    string eventId,
    OrionRoomCalendarBlock block,
    CancellationToken ct)
  {
    using var request = new HttpRequestMessage(
      HttpMethod.Patch,
      $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(options.MailboxAddress)}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    request.Content = JsonContent.Create(BuildGraphEventRequest(options, block), options: JsonOptions);

    using var response = await _httpClient.SendAsync(request, ct);
    if (!response.IsSuccessStatusCode)
    {
      await ThrowGraphRequestExceptionAsync(response, $"No se pudo actualizar el evento {eventId} del calendario {calendarId}.", ct);
    }
  }

  private async Task DeleteEventAsync(
    BonhomiaGraphCalendarSyncOptions options,
    string accessToken,
    string calendarId,
    string eventId,
    CancellationToken ct)
  {
    using var request = new HttpRequestMessage(
      HttpMethod.Delete,
      $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(options.MailboxAddress)}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    using var response = await _httpClient.SendAsync(request, ct);
    if (!response.IsSuccessStatusCode)
    {
      await ThrowGraphRequestExceptionAsync(response, $"No se pudo borrar el evento {eventId} del calendario {calendarId}.", ct);
    }
  }

  private async Task<List<TPayload>> ReadPagedCollectionAsync<TPayload>(
    string initialUrl,
    string accessToken,
    CancellationToken ct)
  {
    var items = new List<TPayload>();
    var nextUrl = initialUrl;

    while (!string.IsNullOrWhiteSpace(nextUrl))
    {
      using var request = new HttpRequestMessage(HttpMethod.Get, nextUrl);
      request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

      using var response = await _httpClient.SendAsync(request, ct);
      if (!response.IsSuccessStatusCode)
      {
        await ThrowGraphRequestExceptionAsync(response, $"No se pudo consultar Microsoft Graph: {nextUrl}", ct);
      }

      var payload = await response.Content.ReadFromJsonAsync<GraphCollectionResponse<TPayload>>(JsonOptions, ct);
      if (payload?.Value is not null)
      {
        items.AddRange(payload.Value);
      }

      nextUrl = payload?.NextLink;
    }

    return items;
  }

  private static BonhomiaGraphCalendarRemoteEvent? MapRemoteEvent(GraphEventPayload payload)
  {
    if (payload.Start is null || payload.End is null || string.IsNullOrWhiteSpace(payload.Id))
    {
      return null;
    }

    var bodyHtml = payload.Body?.Content;
    BonhomiaCalendarSyncPayloadBuilder.TryExtractSourceKey(bodyHtml, out var sourceKey);

    return new BonhomiaGraphCalendarRemoteEvent
    {
      Id = payload.Id,
      Subject = payload.Subject ?? string.Empty,
      BodyHtml = bodyHtml,
      StartDate = ParseGraphDate(payload.Start.DateTime),
      EndDateExclusive = ParseGraphDate(payload.End.DateTime),
      IsAllDay = payload.IsAllDay,
      ShowAs = payload.ShowAs ?? string.Empty,
      SourceKey = string.IsNullOrWhiteSpace(sourceKey) ? null : sourceKey
    };
  }

  private static GraphEventRequest BuildGraphEventRequest(
    BonhomiaGraphCalendarSyncOptions options,
    OrionRoomCalendarBlock block)
  {
    var graphTimeZone = ResolveGraphTimeZoneId(options.TimeZone);
    return new GraphEventRequest(
      BonhomiaCalendarSyncPayloadBuilder.Subject,
      true,
      "busy",
      new GraphItemBody("HTML", BonhomiaCalendarSyncPayloadBuilder.BuildBodyHtml(block)),
      new GraphDateTimeValue(block.StartDate.ToString("yyyy-MM-dd'T'00:00:00", CultureInfo.InvariantCulture), graphTimeZone),
      new GraphDateTimeValue(block.EndDateExclusive.ToString("yyyy-MM-dd'T'00:00:00", CultureInfo.InvariantCulture), graphTimeZone));
  }

  private static OutlookRoomCalendarSyncMappingUpsert BuildMappingUpsert(
    OrionRoomCalendarBlock block,
    string calendarId,
    string eventId)
  {
    return new OutlookRoomCalendarSyncMappingUpsert
    {
      SourceKey = block.SourceKey,
      RoomName = block.RoomName,
      ReservationId = block.ReservationId,
      StartDate = block.StartDate,
      EndDateExclusive = block.EndDateExclusive,
      OutlookCalendarId = calendarId,
      OutlookEventId = eventId,
      ContentHash = BonhomiaCalendarSyncPayloadBuilder.ComputeContentHash(block)
    };
  }

  private static void EnsureConfigured(BonhomiaGraphCalendarSyncOptions options)
  {
    if (string.IsNullOrWhiteSpace(options.TenantId) ||
        string.IsNullOrWhiteSpace(options.ClientId) ||
        string.IsNullOrWhiteSpace(options.ClientSecret) ||
        string.IsNullOrWhiteSpace(options.MailboxAddress))
    {
      throw new InvalidOperationException(
        "BonhomiaGraphCalendarSync no está configurado completamente. Revisa TenantId, ClientId, ClientSecret y MailboxAddress.");
    }
  }

  private static bool IsMissingMappingTable(SqlException exception)
    => exception.Number == 208 &&
       exception.Message.Contains("ROOM_CALENDAR_OUTLOOK_SYNC", StringComparison.OrdinalIgnoreCase);

  private static DateTime ParseGraphDate(string? value)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return DateTime.MinValue;
    }

    return DateTime.Parse(
      value,
      CultureInfo.InvariantCulture,
      DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces).Date;
  }

  private static string ResolveGraphTimeZoneId(string? configuredTimeZone)
  {
    if (string.IsNullOrWhiteSpace(configuredTimeZone))
    {
      return "Central Standard Time (Mexico)";
    }

    return configuredTimeZone.Trim() switch
    {
      "America/Mexico_City" => "Central Standard Time (Mexico)",
      _ => configuredTimeZone.Trim()
    };
  }

  private static TimeZoneInfo ResolveTimeZoneInfo(string? configuredTimeZone)
  {
    var graphTimeZone = ResolveGraphTimeZoneId(configuredTimeZone);
    try
    {
      return TimeZoneInfo.FindSystemTimeZoneById(graphTimeZone);
    }
    catch (TimeZoneNotFoundException)
    {
      return TimeZoneInfo.Local;
    }
  }

  private static async Task ThrowGraphRequestExceptionAsync(
    HttpResponseMessage response,
    string prefix,
    CancellationToken ct)
  {
    var responseBody = await response.Content.ReadAsStringAsync(ct);
    throw new InvalidOperationException($"{prefix} Graph respondió {(int)response.StatusCode}: {responseBody}");
  }

  private sealed class GraphTokenResponse
  {
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }
  }

  private sealed class GraphCollectionResponse<TPayload>
  {
    [JsonPropertyName("value")]
    public List<TPayload> Value { get; set; } = new();

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; set; }
  }

  private sealed class GraphCalendarPayload
  {
    public string? Id { get; set; }
    public string? Name { get; set; }
  }

  private sealed class GraphEventPayload
  {
    public string Id { get; set; } = string.Empty;
    public string? Subject { get; set; }
    public bool IsAllDay { get; set; }
    public string? ShowAs { get; set; }
    public GraphItemBody? Body { get; set; }
    public GraphDateTimeValue? Start { get; set; }
    public GraphDateTimeValue? End { get; set; }
  }

  private sealed record GraphEventRequest(
    string Subject,
    bool IsAllDay,
    string ShowAs,
    GraphItemBody Body,
    GraphDateTimeValue Start,
    GraphDateTimeValue End);

  private sealed record GraphItemBody(string ContentType, string Content);

  private sealed record GraphDateTimeValue(string DateTime, string TimeZone);
}
