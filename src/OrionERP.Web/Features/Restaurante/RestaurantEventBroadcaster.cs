using Dapper;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Platform;

namespace OrionERP.Web.Features.Restaurante;

public sealed class RestaurantEventBroadcastOptions
{
  public const string SectionName = "RestaurantEventBroadcasting";
  public bool Enabled { get; set; }
  public int PollIntervalMilliseconds { get; set; } = 1000;
  public int BatchSize { get; set; } = 50;
}

public sealed class RestaurantEventBroadcaster : BackgroundService
{
  private readonly RestaurantEventBroadcastOptions _options;
  private readonly IHubContext<RestaurantEventsHub> _hub;
  private readonly ILogger<RestaurantEventBroadcaster> _logger;
  private readonly IServiceScopeFactory _scopeFactory;

  public RestaurantEventBroadcaster(
    IOptions<RestaurantEventBroadcastOptions> options,
    IHubContext<RestaurantEventsHub> hub,
    ILogger<RestaurantEventBroadcaster> logger,
    IServiceScopeFactory scopeFactory)
  {
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _hub = hub;
    _logger = logger;
    _scopeFactory=scopeFactory;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    // El código de alcance está listo, pero habilitarlo consume todo el rezago
    // pendiente. La activación es una decisión operativa explícita, no un efecto de
    // publicar binarios nuevos.
    if (!_options.Enabled)
    {
      _logger.LogInformation("La difusión SignalR de Restaurante permanece deshabilitada.");
      return;
    }
    var interval = TimeSpan.FromMilliseconds(Math.Clamp(_options.PollIntervalMilliseconds, 250, 60_000));
    using var timer = new PeriodicTimer(interval);
    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        await PublishBatchAsync(stoppingToken);
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        break;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "No se pudo publicar el lote de eventos de Restaurante.");
      }

      try
      {
        if (!await timer.WaitForNextTickAsync(stoppingToken))
        {
          break;
        }
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        // La cancelación del temporizador es el cierre normal del host. Si escapa de
        // ExecuteAsync, BackgroundService la registra como un fallo de la aplicación.
        break;
      }
    }
  }

  private async Task PublishBatchAsync(CancellationToken ct)
  {
    await using var serviceScope=_scopeFactory.CreateAsyncScope();
    var sites=serviceScope.ServiceProvider.GetRequiredService<IEnabledModuleSiteEnumerator>();
    var bindings=serviceScope.ServiceProvider.GetRequiredService<IModuleSiteBindingResolver>();
    var leases=serviceScope.ServiceProvider.GetRequiredService<IModuleJobLeaseManager>();
    var sessions=serviceScope.ServiceProvider.GetRequiredService<IOrionSqlSessionFactory>();
    foreach (var scope in await sites.ListAsync(PlatformModuleCodes.Restaurant,ct))
    {
      var binding=await bindings.ResolveRequiredAsync(scope,ct);
      var boundScope=scope with { ModuleLocalSiteId=binding.ModuleLocalSiteId };
      await using var lease=await leases.TryAcquireAsync("RestaurantEventBroadcast",boundScope,ct);
      if (lease.IsAcquired) await PublishSiteBatchAsync(sessions,boundScope,ct);
    }
  }

  private async Task PublishSiteBatchAsync(
    IOrionSqlSessionFactory sessions,PlatformExecutionScope scope,CancellationToken ct)
  {
    await using var conn=await sessions.OpenAsync(scope,ct);

    // A pooled connection can retain the isolation level used by an earlier
    // workflow. Establish the locking level in its own command so SQL Server
    // compiles the READPAST query under READ COMMITTED as well.
    await conn.ExecuteAsync(new CommandDefinition(
      "SET TRANSACTION ISOLATION LEVEL READ COMMITTED;",
      cancellationToken: ct));

    var events = (await conn.QueryAsync<OutboxRow>(new CommandDefinition(
      """
      SELECT TOP (@BatchSize)
        eventInfo.Id,eventInfo.Rfc,eventInfo.SiteId,eventInfo.EventType,
        eventInfo.AggregateId,eventInfo.Payload,eventInfo.OccurredAt
      FROM restaurante.EventOutbox eventInfo WITH (READPAST,UPDLOCK,ROWLOCK)
      WHERE eventInfo.Rfc=@Rfc AND eventInfo.SiteId=@LegacySiteId
        AND eventInfo.PublishedAt IS NULL AND eventInfo.Attempts<20
      ORDER BY eventInfo.Id;
      """, new
      {
        BatchSize = Math.Clamp(_options.BatchSize, 1, 500),
        Rfc=scope.CompanyRfc,
        LegacySiteId=scope.ModuleLocalSiteId!.Value
      }, cancellationToken: ct))).AsList();
    if (events.Count == 0) return;

    foreach (var eventInfo in events)
    {
      try
      {
        await _hub.Clients.Group(RestaurantEventsHub.GroupName(eventInfo.Rfc, eventInfo.SiteId))
          .SendAsync("restaurantEvent", new
          {
            eventInfo.Id,
            eventInfo.EventType,
            eventInfo.AggregateId,
            eventInfo.Payload,
            eventInfo.OccurredAt
          }, ct);
        await conn.ExecuteAsync(new CommandDefinition(
          "UPDATE restaurante.EventOutbox SET PublishedAt=SYSUTCDATETIME(), Attempts=Attempts+1 WHERE Id=@Id AND PublishedAt IS NULL;",
          new { eventInfo.Id }, cancellationToken: ct));
      }
      catch
      {
        await conn.ExecuteAsync(new CommandDefinition(
          "UPDATE restaurante.EventOutbox SET Attempts=Attempts+1 WHERE Id=@Id AND PublishedAt IS NULL;",
          new { eventInfo.Id }, cancellationToken: ct));
        throw;
      }
    }
  }

  private sealed class OutboxRow
  {
    public long Id { get; set; }
    public string Rfc { get; set; } = string.Empty;
    public int SiteId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string AggregateId { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }
  }
}
