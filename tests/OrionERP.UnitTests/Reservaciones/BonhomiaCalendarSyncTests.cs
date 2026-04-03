using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Reservaciones.CalendarSync;
using OrionERP.Infrastructure.Features.Reservaciones.CalendarSync;

namespace OrionERP.UnitTests.Reservaciones;

public class BonhomiaCalendarSyncTests
{
  [Fact]
  public void BuildBlocks_GroupsContiguousRows_AndKeepsCanceledReservationBlocks()
  {
    var rows = new[]
    {
      new OrionRoomCalendarLockRow
      {
        RoomName = "BERLIN",
        RoomDate = new DateTime(2026, 3, 27),
        ReservationId = 120,
        LockedBy = "Cliente Uno",
        LockDescription = "120",
        Status = "Cancelada"
      },
      new OrionRoomCalendarLockRow
      {
        RoomName = "BERLIN",
        RoomDate = new DateTime(2026, 3, 28),
        ReservationId = 120,
        LockedBy = "Cliente Uno",
        LockDescription = "120",
        Status = "Cancelada"
      },
      new OrionRoomCalendarLockRow
      {
        RoomName = "BERLIN",
        RoomDate = new DateTime(2026, 3, 29),
        LockedBy = "OPERACION",
        LockDescription = "maintenance"
      },
      new OrionRoomCalendarLockRow
      {
        RoomName = "BERLIN",
        RoomDate = new DateTime(2026, 3, 30),
        LockedBy = "OPERACION",
        LockDescription = "maintenance"
      }
    };

    var blocks = BonhomiaCalendarSyncBlockBuilder.BuildBlocks(rows);

    Assert.Equal(2, blocks.Count);

    var reservationBlock = blocks[0];
    Assert.Equal("reservation:120:BERLIN", reservationBlock.SourceKey);
    Assert.Equal(new DateTime(2026, 3, 27), reservationBlock.StartDate);
    Assert.Equal(new DateTime(2026, 3, 29), reservationBlock.EndDateExclusive);
    Assert.Equal("Cancelada", reservationBlock.Status);

    var manualBlock = blocks[1];
    Assert.Equal("manual:BERLIN:20260329:20260331:MAINTENANCE", manualBlock.SourceKey);
    Assert.Equal(new DateTime(2026, 3, 29), manualBlock.StartDate);
    Assert.Equal(new DateTime(2026, 3, 31), manualBlock.EndDateExclusive);
  }

  [Fact]
  public void PayloadBuilder_UsesGenericTitle_AndRoundTripsMarker()
  {
    var block = new OrionRoomCalendarBlock
    {
      SourceKey = "reservation:23891:BERLIN",
      RoomName = "BERLIN",
      ReservationId = 23891,
      StartDate = new DateTime(2026, 3, 27),
      EndDateExclusive = new DateTime(2026, 3, 30)
    };

    var bodyHtml = BonhomiaCalendarSyncPayloadBuilder.BuildBodyHtml(block);
    var extracted = BonhomiaCalendarSyncPayloadBuilder.TryExtractSourceKey(bodyHtml, out var sourceKey);

    Assert.Equal("ORION BLOCKED", BonhomiaCalendarSyncPayloadBuilder.Subject);
    Assert.True(extracted);
    Assert.Equal(block.SourceKey, sourceKey);

    var remoteEvent = new BonhomiaGraphCalendarRemoteEvent
    {
      Id = "evt-1",
      Subject = BonhomiaCalendarSyncPayloadBuilder.Subject,
      BodyHtml = bodyHtml,
      StartDate = block.StartDate,
      EndDateExclusive = block.EndDateExclusive,
      IsAllDay = true,
      ShowAs = "busy",
      SourceKey = sourceKey
    };

    Assert.Equal(
      BonhomiaCalendarSyncPayloadBuilder.ComputeContentHash(block),
      BonhomiaCalendarSyncPayloadBuilder.ComputeRemoteContentHash(remoteEvent));
  }

  [Fact]
  public void Reconciler_ReturnsCreate_WhenLocalBlockHasNoRemoteEvent()
  {
    var localBlock = CreateLocalBlock();

    var operations = BonhomiaCalendarSyncReconciler.BuildOperations(
      "BERLIN",
      "cal-berlin",
      new[] { localBlock },
      Array.Empty<OutlookRoomCalendarSyncMapping>(),
      Array.Empty<BonhomiaGraphCalendarRemoteEvent>());

    var operation = Assert.Single(operations);
    Assert.Equal(BonhomiaCalendarSyncOperationType.Create, operation.Type);
    Assert.Equal(localBlock.SourceKey, operation.LocalBlock?.SourceKey);
  }

  [Fact]
  public void Reconciler_ReturnsUpdate_WhenRemoteEventDriftsFromLocalBlock()
  {
    var localBlock = CreateLocalBlock();
    var mapping = CreateMapping(localBlock, "cal-berlin", "evt-1");
    var remoteEvent = new BonhomiaGraphCalendarRemoteEvent
    {
      Id = "evt-1",
      Subject = BonhomiaCalendarSyncPayloadBuilder.Subject,
      BodyHtml = BonhomiaCalendarSyncPayloadBuilder.BuildBodyHtml(localBlock),
      StartDate = localBlock.StartDate,
      EndDateExclusive = localBlock.EndDateExclusive.AddDays(-1),
      IsAllDay = true,
      ShowAs = "busy",
      SourceKey = localBlock.SourceKey
    };

    var operations = BonhomiaCalendarSyncReconciler.BuildOperations(
      "BERLIN",
      "cal-berlin",
      new[] { localBlock },
      new[] { mapping },
      new[] { remoteEvent });

    var operation = Assert.Single(operations);
    Assert.Equal(BonhomiaCalendarSyncOperationType.Update, operation.Type);
    Assert.Equal("evt-1", operation.RemoteEvent?.Id);
  }

  [Fact]
  public void Reconciler_ReturnsDelete_WhenMappingExistsButLocalBlockIsGone()
  {
    var localBlock = CreateLocalBlock();
    var mapping = CreateMapping(localBlock, "cal-berlin", "evt-1");
    var remoteEvent = new BonhomiaGraphCalendarRemoteEvent
    {
      Id = "evt-1",
      Subject = BonhomiaCalendarSyncPayloadBuilder.Subject,
      BodyHtml = BonhomiaCalendarSyncPayloadBuilder.BuildBodyHtml(localBlock),
      StartDate = localBlock.StartDate,
      EndDateExclusive = localBlock.EndDateExclusive,
      IsAllDay = true,
      ShowAs = "busy",
      SourceKey = localBlock.SourceKey
    };

    var operations = BonhomiaCalendarSyncReconciler.BuildOperations(
      "BERLIN",
      "cal-berlin",
      Array.Empty<OrionRoomCalendarBlock>(),
      new[] { mapping },
      new[] { remoteEvent });

    var operation = Assert.Single(operations);
    Assert.Equal(BonhomiaCalendarSyncOperationType.DeleteRemoteEvent, operation.Type);
    Assert.Equal(mapping.Id, operation.Mapping?.Id);
  }

  [Fact]
  public void Reconciler_ReturnsRecoverMapping_WhenMarkerExistsButMappingIsMissing()
  {
    var localBlock = CreateLocalBlock();
    var remoteEvent = new BonhomiaGraphCalendarRemoteEvent
    {
      Id = "evt-1",
      Subject = BonhomiaCalendarSyncPayloadBuilder.Subject,
      BodyHtml = BonhomiaCalendarSyncPayloadBuilder.BuildBodyHtml(localBlock),
      StartDate = localBlock.StartDate,
      EndDateExclusive = localBlock.EndDateExclusive,
      IsAllDay = true,
      ShowAs = "busy",
      SourceKey = localBlock.SourceKey
    };

    var operations = BonhomiaCalendarSyncReconciler.BuildOperations(
      "BERLIN",
      "cal-berlin",
      new[] { localBlock },
      Array.Empty<OutlookRoomCalendarSyncMapping>(),
      new[] { remoteEvent });

    var operation = Assert.Single(operations);
    Assert.Equal(BonhomiaCalendarSyncOperationType.RecoverMapping, operation.Type);
    Assert.Equal("evt-1", operation.MappingUpsert?.OutlookEventId);
  }

  [Fact]
  public async Task SyncService_CreatesRemoteEvent_AndPersistsMapping()
  {
    var localBlock = CreateLocalBlock();
    var repository = new FakeSyncRepository(new[] { localBlock }, Array.Empty<OutlookRoomCalendarSyncMapping>());
    var handler = new FakeHttpMessageHandler();
    handler.EnqueueJson("""{"access_token":"token-123"}""");
    handler.EnqueueJson("""{"value":[{"id":"cal-berlin","name":"BERLIN"}]}""");
    handler.EnqueueJson("""{"value":[]}""");
    handler.EnqueueJson("""{"id":"evt-100","subject":"ORION BLOCKED"}""");

    using var httpClient = new HttpClient(handler);
    var service = new BonhomiaRoomCalendarSyncService(
      httpClient,
      repository,
      Options.Create(new BonhomiaGraphCalendarSyncOptions
      {
        TenantId = "tenant",
        ClientId = "client",
        ClientSecret = "secret",
        MailboxAddress = "recepcion@bonhomiasuites.com",
        TimeZone = "America/Mexico_City",
        TargetCalendars = new List<string> { "BERLIN" }
      }),
      NullLogger<BonhomiaRoomCalendarSyncService>.Instance);

    var result = await service.SyncAsync(new DateTime(2026, 3, 27), new DateTime(2027, 1, 1));

    Assert.Equal(1, result.CreatedCount);
    Assert.Equal(0, result.ErrorCount);
    var upsert = Assert.Single(repository.UpsertedMappings);
    Assert.Equal("evt-100", upsert.OutlookEventId);
    Assert.Contains(handler.Requests, item => item.Method == HttpMethod.Post && item.RequestUri!.AbsoluteUri.Contains("/oauth2/v2.0/token", StringComparison.Ordinal));
    Assert.Contains(handler.Requests, item => item.Method == HttpMethod.Get && item.RequestUri!.AbsoluteUri.Contains("/calendars?$select=id,name", StringComparison.Ordinal));
    Assert.Contains(handler.Requests, item => item.Method == HttpMethod.Get && item.RequestUri!.AbsoluteUri.Contains("/calendarView", StringComparison.Ordinal));
    Assert.Contains(handler.Requests, item => item.Method == HttpMethod.Post && item.RequestUri!.AbsoluteUri.Contains("/events", StringComparison.Ordinal));
  }

  private static OrionRoomCalendarBlock CreateLocalBlock()
    => new()
    {
      SourceKey = "reservation:23891:BERLIN",
      RoomName = "BERLIN",
      ReservationId = 23891,
      StartDate = new DateTime(2026, 3, 27),
      EndDateExclusive = new DateTime(2026, 3, 30)
    };

  private static OutlookRoomCalendarSyncMapping CreateMapping(OrionRoomCalendarBlock block, string calendarId, string eventId)
    => new()
    {
      Id = 10,
      SourceKey = block.SourceKey,
      RoomName = block.RoomName,
      ReservationId = block.ReservationId,
      StartDate = block.StartDate,
      EndDateExclusive = block.EndDateExclusive,
      OutlookCalendarId = calendarId,
      OutlookEventId = eventId,
      ContentHash = BonhomiaCalendarSyncPayloadBuilder.ComputeContentHash(block),
      LastSyncedUtc = new DateTime(2026, 3, 27, 12, 0, 0, DateTimeKind.Utc)
    };

  private sealed class FakeSyncRepository : IOutlookRoomCalendarSyncRepository
  {
    private readonly IReadOnlyList<OrionRoomCalendarBlock> _blocks;
    private readonly IReadOnlyList<OutlookRoomCalendarSyncMapping> _mappings;

    public FakeSyncRepository(
      IReadOnlyList<OrionRoomCalendarBlock> blocks,
      IReadOnlyList<OutlookRoomCalendarSyncMapping> mappings)
    {
      _blocks = blocks;
      _mappings = mappings;
    }

    public List<OutlookRoomCalendarSyncMappingUpsert> UpsertedMappings { get; } = new();

    public Task<IReadOnlyList<OrionRoomCalendarBlock>> GetBlockedBlocksAsync(
      DateTime startDate,
      DateTime endDateExclusive,
      IReadOnlyCollection<string> roomNames,
      CancellationToken ct = default)
      => Task.FromResult(_blocks);

    public Task<IReadOnlyList<OutlookRoomCalendarSyncMapping>> GetMappingsAsync(
      DateTime startDate,
      DateTime endDateExclusive,
      IReadOnlyCollection<string> roomNames,
      CancellationToken ct = default)
      => Task.FromResult(_mappings);

    public Task UpsertMappingsAsync(
      IReadOnlyCollection<OutlookRoomCalendarSyncMappingUpsert> mappings,
      CancellationToken ct = default)
    {
      UpsertedMappings.AddRange(mappings);
      return Task.CompletedTask;
    }

    public Task DeleteMappingsAsync(
      IReadOnlyCollection<int> mappingIds,
      CancellationToken ct = default)
      => Task.CompletedTask;
  }

  private sealed class FakeHttpMessageHandler : HttpMessageHandler
  {
    private readonly Queue<HttpResponseMessage> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = new();

    public void EnqueueJson(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
      _responses.Enqueue(new HttpResponseMessage(statusCode)
      {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
      });
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      Requests.Add(request);
      if (_responses.Count == 0)
      {
        throw new InvalidOperationException($"No fake response queued for {request.Method} {request.RequestUri}");
      }

      return Task.FromResult(_responses.Dequeue());
    }
  }
}
