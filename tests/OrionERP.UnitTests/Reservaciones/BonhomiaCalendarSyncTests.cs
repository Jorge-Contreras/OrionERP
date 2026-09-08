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
using OrionERP.Application.Features.Reservaciones;
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
  public void CalendarOptions_UseSharedGraphCredentials_WhenTenantAndClientIdMatch()
  {
    var options = new BonhomiaGraphCalendarSyncOptions
    {
      TenantId = "aea961c0-7e15-4be8-9f81-2388eb2f9e96",
      ClientId = "6bfe945f-8423-4eef-b78c-934cc41876b7",
      ClientSecret = "stale-secret"
    };

    options.ApplySharedGraphCredentials(
      "aea961c0-7e15-4be8-9f81-2388eb2f9e96",
      "6BFE945F-8423-4EEF-B78C-934CC41876B7",
      "rotated-secret");

    Assert.Equal("aea961c0-7e15-4be8-9f81-2388eb2f9e96", options.TenantId);
    Assert.Equal("6BFE945F-8423-4EEF-B78C-934CC41876B7", options.ClientId);
    Assert.Equal("rotated-secret", options.ClientSecret);
  }

  [Fact]
  public void CalendarOptions_KeepCalendarCredentials_WhenClientIdDiffers()
  {
    var options = new BonhomiaGraphCalendarSyncOptions
    {
      TenantId = "calendar-tenant",
      ClientId = "calendar-client",
      ClientSecret = "calendar-secret"
    };

    options.ApplySharedGraphCredentials(
      "shared-tenant",
      "shared-client",
      "shared-secret");

    Assert.Equal("calendar-tenant", options.TenantId);
    Assert.Equal("calendar-client", options.ClientId);
    Assert.Equal("calendar-secret", options.ClientSecret);
  }

  [Theory]
  [InlineData("", "")]
  [InlineData("", "shared-client")]
  [InlineData("other-tenant", "shared-client")]
  public void CalendarOptions_DoNotAdoptSharedIdentityWithoutExplicitMatchingTenant(string tenantId, string clientId)
  {
    var options = new BonhomiaGraphCalendarSyncOptions { TenantId = tenantId, ClientId = clientId };
    options.ApplySharedGraphCredentials("shared-tenant", "shared-client", "fake-shared-secret");
    Assert.Equal(tenantId, options.TenantId);
    Assert.Equal(clientId, options.ClientId);
    Assert.Empty(options.ClientSecret);
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
        Enabled = true,
        CompanyId = 1,
        SiteId = 10,
        CompanyRfc = "TEST010101AAA",
        TenantId = "tenant",
        ClientId = "client",
        ClientSecret = "secret",
        MailboxAddress = "recepcion@bonhomiasuites.com",
        TimeZone = "America/Mexico_City",
        TargetCalendars = new List<string> { "BERLIN" }
      }),
      NullLogger<BonhomiaRoomCalendarSyncService>.Instance,
      new FakeScopeAccessor());

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

  [Theory]
  [InlineData(false, 1, 10, "TEST010101AAA")]
  [InlineData(true, 0, 10, "TEST010101AAA")]
  [InlineData(true, 1, 0, "TEST010101AAA")]
  [InlineData(true, 1, 10, "")]
  [InlineData(true, 2, 10, "TEST010101AAA")]
  [InlineData(true, 1, 20, "TEST010101AAA")]
  [InlineData(true, 1, 10, "OTHER010101AA")]
  public async Task SyncService_RejectsDisabledOrDifferentScope_BeforeSqlAndHttp(
    bool enabled, long companyId, long siteId, string rfc)
  {
    var repository = new FakeSyncRepository([], []);
    var handler = new FakeHttpMessageHandler();
    var options = CreateOptions();
    options.Enabled = enabled;
    options.CompanyId = companyId;
    options.SiteId = siteId;
    options.CompanyRfc = rfc;
    using var http = new HttpClient(handler);
    var service = CreateService(http, repository, options);

    await Assert.ThrowsAnyAsync<Exception>(() => service.SyncAsync(new DateTime(2026, 3, 27), new DateTime(2026, 3, 30)));

    Assert.Empty(handler.Requests);
    Assert.Equal(0, repository.ReadCount);
  }

  [Fact]
  public async Task SyncService_LocalScopeRejection_PrecedesOAuth()
  {
    var repository = new FakeSyncRepository([], []) { ThrowOnRead = true };
    var handler = new FakeHttpMessageHandler();
    using var http = new HttpClient(handler);
    var service = CreateService(http, repository, CreateOptions());
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SyncAsync(new DateTime(2026, 3, 27), new DateTime(2026, 3, 30)));
    Assert.Empty(handler.Requests);
  }

  [Theory]
  [InlineData(2, 10)]
  [InlineData(1, 20)]
  [InlineData(0, 0)]
  public async Task SyncService_DoesNotTouchForeignOrUnattributedRemoteMarkers(long companyId, long siteId)
  {
    var block = CreateLocalBlock();
    block.CompanyId = companyId;
    block.SiteId = siteId;
    var handler = new FakeHttpMessageHandler();
    handler.EnqueueJson("""{"access_token":"fake-token"}""");
    handler.EnqueueJson("""{"value":[{"id":"cal-berlin","name":"BERLIN"}]}""");
    handler.EnqueueJson(JsonSerializer.Serialize(new { value = new[] { new {
      id = "foreign-event", subject = BonhomiaCalendarSyncPayloadBuilder.Subject,
      body = new { content = BonhomiaCalendarSyncPayloadBuilder.BuildBodyHtml(block) },
      start = new { dateTime = "2026-03-27T00:00:00", timeZone = "UTC" },
      end = new { dateTime = "2026-03-30T00:00:00", timeZone = "UTC" },
      isAllDay = true, showAs = "busy"
    } } }));
    var repository = new FakeSyncRepository([], []);
    using var http = new HttpClient(handler);
    var result = await CreateService(http, repository, CreateOptions())
      .SyncAsync(new DateTime(2026, 3, 27), new DateTime(2026, 3, 30));
    Assert.Equal(0, result.ErrorCount);
    Assert.Equal(0, result.DeletedCount);
    Assert.Equal(0, Assert.Single(result.Rooms).RemoteOwnedEventCount);
    Assert.Equal(3, handler.Requests.Count);
    Assert.Empty(repository.UpsertedMappings);
    Assert.All(repository.Scopes, scope => Assert.Equal(new HospitalityScope(1, 10, "TEST010101AAA"), scope));
  }

  [Fact]
  public void PayloadBuilder_ScopeMarkerDistinguishesCompaniesAndSites()
  {
    var block = CreateLocalBlock();
    block.CompanyId = 1;
    block.SiteId = 10;
    var body = BonhomiaCalendarSyncPayloadBuilder.BuildBodyHtml(block);
    Assert.True(BonhomiaCalendarSyncPayloadBuilder.BelongsToScope(body, 1, 10));
    Assert.False(BonhomiaCalendarSyncPayloadBuilder.BelongsToScope(body, 2, 10));
    Assert.False(BonhomiaCalendarSyncPayloadBuilder.BelongsToScope(body, 1, 20));
    Assert.False(BonhomiaCalendarSyncPayloadBuilder.BelongsToScope(body + "<!-- OrionScope:1:10 -->", 1, 10));
    var hash = BonhomiaCalendarSyncPayloadBuilder.ComputeContentHash(block);
    block.SiteId = 20;
    Assert.NotEqual(hash, BonhomiaCalendarSyncPayloadBuilder.ComputeContentHash(block));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task SyncService_PreservesOwnedEvent_AndRecoversOrUpgradesMapping(bool legacyMappedEvent)
  {
    var block = CreateLocalBlock();
    if (!legacyMappedEvent)
    {
      block.CompanyId = 1;
      block.SiteId = 10;
    }
    var existingMappings = legacyMappedEvent
      ? new[] { CreateMapping(block, "cal-berlin", "existing-event") }
      : Array.Empty<OutlookRoomCalendarSyncMapping>();
    var handler = new FakeHttpMessageHandler();
    handler.EnqueueJson("""{"access_token":"fake-token"}""");
    handler.EnqueueJson("""{"value":[{"id":"cal-berlin","name":"BERLIN"}]}""");
    handler.EnqueueJson(JsonSerializer.Serialize(new { value = new[] { new {
      id = "existing-event", subject = BonhomiaCalendarSyncPayloadBuilder.Subject,
      body = new { content = BonhomiaCalendarSyncPayloadBuilder.BuildBodyHtml(block) },
      start = new { dateTime = "2026-03-27T00:00:00", timeZone = "UTC" },
      end = new { dateTime = "2026-03-30T00:00:00", timeZone = "UTC" },
      isAllDay = true, showAs = "busy"
    } } }));
    if (legacyMappedEvent) handler.EnqueueJson("{}");
    var repository = new FakeSyncRepository([block], existingMappings);
    using var http = new HttpClient(handler);

    var result = await CreateService(http, repository, CreateOptions())
      .SyncAsync(new DateTime(2026, 3, 27), new DateTime(2026, 3, 30));

    Assert.Equal(0, result.ErrorCount);
    Assert.Equal(0, result.CreatedCount);
    Assert.Equal(0, result.DeletedCount);
    Assert.Equal(legacyMappedEvent ? 1 : 0, result.UpdatedCount);
    Assert.Equal(legacyMappedEvent ? 0 : 1, result.RecoveredMappingCount);
    Assert.Equal("existing-event", Assert.Single(repository.UpsertedMappings).OutlookEventId);
    Assert.All(repository.Scopes, scope => Assert.Equal(new HospitalityScope(1, 10, "TEST010101AAA"), scope));
  }

  [Fact]
  public void CalendarOptions_HaveNoEnabledOrNamedTenantFallback()
  {
    var options = new BonhomiaGraphCalendarSyncOptions();
    Assert.False(options.Enabled);
    Assert.Empty(options.MailboxAddress);
    Assert.Empty(options.GetTargetCalendars());
  }

  private static BonhomiaGraphCalendarSyncOptions CreateOptions() => new()
  {
    Enabled = true, CompanyId = 1, SiteId = 10, CompanyRfc = "TEST010101AAA",
    TenantId = "fake-tenant", ClientId = "fake-client", ClientSecret = "fake-secret",
    MailboxAddress = "calendar@example.test", TargetCalendars = ["BERLIN"]
  };

  private static BonhomiaRoomCalendarSyncService CreateService(
    HttpClient http, FakeSyncRepository repository, BonhomiaGraphCalendarSyncOptions options)
    => new(http, repository, Options.Create(options),
      NullLogger<BonhomiaRoomCalendarSyncService>.Instance, new FakeScopeAccessor());

  private sealed class FakeScopeAccessor : IHospitalityScopeAccessor
  {
    public Task<HospitalityScope> ResolveRequiredAsync(CancellationToken ct = default)
      => Task.FromResult(new HospitalityScope(1, 10, "TEST010101AAA"));
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

    public int ReadCount { get; private set; }
    public bool ThrowOnRead { get; set; }
    public List<HospitalityScope> Scopes { get; } = new();
    public List<OutlookRoomCalendarSyncMappingUpsert> UpsertedMappings { get; } = new();

    public Task<IReadOnlyList<OrionRoomCalendarBlock>> GetBlockedBlocksAsync(
      HospitalityScope scope,
      DateTime startDate,
      DateTime endDateExclusive,
      IReadOnlyCollection<string> roomNames,
      CancellationToken ct = default)
    {
      ReadCount++;
      Scopes.Add(scope);
      if (ThrowOnRead) throw new UnauthorizedAccessException("Rejected local scope");
      return Task.FromResult(_blocks);
    }

    public Task<IReadOnlyList<OutlookRoomCalendarSyncMapping>> GetMappingsAsync(
      HospitalityScope scope,
      DateTime startDate,
      DateTime endDateExclusive,
      IReadOnlyCollection<string> roomNames,
      CancellationToken ct = default)
    {
      ReadCount++;
      Scopes.Add(scope);
      return Task.FromResult(_mappings);
    }

    public Task UpsertMappingsAsync(
      HospitalityScope scope,
      IReadOnlyCollection<OutlookRoomCalendarSyncMappingUpsert> mappings,
      CancellationToken ct = default)
    {
      Scopes.Add(scope);
      UpsertedMappings.AddRange(mappings);
      return Task.CompletedTask;
    }

    public Task DeleteMappingsAsync(
      HospitalityScope scope,
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
