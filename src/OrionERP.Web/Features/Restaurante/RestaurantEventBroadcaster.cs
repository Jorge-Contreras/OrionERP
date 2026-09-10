using System.Data.Common;
using Dapper;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

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
  private readonly string? _connectionString;
  private readonly RestaurantEventBroadcastOptions _options;
  private readonly IHubContext<RestaurantEventsHub> _hub;
  private readonly ILogger<RestaurantEventBroadcaster> _logger;

  public RestaurantEventBroadcaster(
    IConfiguration configuration,
    IOptions<RestaurantEventBroadcastOptions> options,
    IHubContext<RestaurantEventsHub> hub,
    ILogger<RestaurantEventBroadcaster> logger)
  {
    ArgumentNullException.ThrowIfNull(configuration);
    _connectionString = configuration.GetConnectionString("OrionDb");
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _hub = hub;
    _logger = logger;
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
    if (string.IsNullOrWhiteSpace(_connectionString))
      throw new InvalidOperationException("Falta ConnectionStrings:OrionDb para difundir eventos de Restaurante.");

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
    var sites = await LoadEnabledSitesAsync(ct);
    foreach (var site in sites)
      await PublishSiteBatchAsync(site, ct);
  }

  private async Task<IReadOnlyList<EnabledSiteRow>> LoadEnabledSitesAsync(CancellationToken ct)
  {
    await using var conn = new SqlConnection(_connectionString);
    await conn.OpenAsync(ct);
    var rows = await conn.QueryAsync<EnabledSiteRow>(new CommandDefinition(
      """
      SELECT company.CompanyId,company.Rfc,site.SiteKey
      FROM orion.Company company
      JOIN orion.Site site ON site.CompanyId=company.CompanyId AND site.IsActive=1
      JOIN orion.CompanyModule companyModule
        ON companyModule.CompanyId=company.CompanyId AND companyModule.ModuleCode='RESTAURANT'
      JOIN orion.Module moduleInfo
        ON moduleInfo.ModuleCode=companyModule.ModuleCode AND moduleInfo.IsActive=1
      JOIN orion.SiteCapability capability
        ON capability.CompanyId=company.CompanyId AND capability.SiteId=site.SiteId
       AND capability.ModuleCode=companyModule.ModuleCode AND capability.IsEnabled=1
      WHERE company.IsActive=1 AND companyModule.[Status]='Enabled'
        AND (companyModule.EffectiveFromUtc IS NULL OR companyModule.EffectiveFromUtc<=SYSUTCDATETIME())
        AND (companyModule.EffectiveToUtc IS NULL OR companyModule.EffectiveToUtc>SYSUTCDATETIME())
      ORDER BY company.CompanyId,site.SiteId;
      """, cancellationToken: ct));
    return rows.AsList();
  }

  private async Task PublishSiteBatchAsync(EnabledSiteRow site, CancellationToken ct)
  {
    await using var conn = new SqlConnection(_connectionString);
    await conn.OpenAsync(ct);
    await InitializeCompanyScopeAsync(conn, site, ct);

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
      JOIN restaurante.Site legacySite
        ON legacySite.Rfc=eventInfo.Rfc AND legacySite.Id=eventInfo.SiteId
      WHERE eventInfo.Rfc=@Rfc AND legacySite.SiteCode=@SiteKey
        AND eventInfo.PublishedAt IS NULL AND eventInfo.Attempts<20
      ORDER BY eventInfo.Id;
      """, new
      {
        BatchSize = Math.Clamp(_options.BatchSize, 1, 500),
        site.Rfc,
        site.SiteKey
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

  private static Task InitializeCompanyScopeAsync(
    DbConnection connection,
    EnabledSiteRow site,
    CancellationToken ct)
    => connection.ExecuteAsync(new CommandDefinition(
      """
      EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@Rfc,@read_only=0;
      EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=@CompanyId,@read_only=0;
      IF NOT EXISTS
      (
        SELECT 1 FROM orion.Company
        WHERE CompanyId=@CompanyId AND Rfc=@Rfc AND IsActive=1
      ) THROW 52310,'La empresa de difusión de Restaurante no es válida.',1;
      """, new { site.Rfc, site.CompanyId }, cancellationToken: ct));

  private sealed class EnabledSiteRow
  {
    public long CompanyId { get; set; }
    public string Rfc { get; set; } = string.Empty;
    public string SiteKey { get; set; } = string.Empty;
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
