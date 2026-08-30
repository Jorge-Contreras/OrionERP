using System.Data.Common;
using Dapper;
using Microsoft.AspNetCore.SignalR;
using OrionERP.Application.Common;

namespace OrionERP.Web.Features.Restaurante;

public sealed class RestaurantEventBroadcaster : BackgroundService
{
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly IHubContext<RestaurantEventsHub> _hub;
  private readonly ILogger<RestaurantEventBroadcaster> _logger;

  public RestaurantEventBroadcaster(
    IServiceScopeFactory scopeFactory,
    IHubContext<RestaurantEventsHub> hub,
    ILogger<RestaurantEventBroadcaster> logger)
  {
    _scopeFactory = scopeFactory;
    _hub = hub;
    _logger = logger;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
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
    using var scope = _scopeFactory.CreateScope();
    var connectionFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
    using var conn = connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica de conexiones no devolvió una DbConnection.");
    await conn.OpenAsync(ct);

    // A pooled connection can retain the isolation level used by an earlier
    // workflow. Establish the locking level in its own command so SQL Server
    // compiles the READPAST query under READ COMMITTED as well.
    await conn.ExecuteAsync(new CommandDefinition(
      "SET TRANSACTION ISOLATION LEVEL READ COMMITTED;",
      cancellationToken: ct));

    var events = (await conn.QueryAsync<OutboxRow>(new CommandDefinition(
      """
      SELECT TOP (50) Id, Rfc, SiteId, EventType, AggregateId, Payload, OccurredAt
      FROM restaurante.EventOutbox WITH (READPAST, UPDLOCK, ROWLOCK)
      WHERE PublishedAt IS NULL AND Attempts < 20
      ORDER BY Id;
      """, cancellationToken: ct))).AsList();

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
