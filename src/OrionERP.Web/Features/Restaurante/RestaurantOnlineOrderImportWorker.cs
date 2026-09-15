using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Web.Features.Restaurante;

public sealed class RestaurantOnlineOrderProcessingOptions
{
  public const string SectionName = "OnlineOrderingProcessing";
  public bool Enabled { get; set; } = true;
  public int PollIntervalSeconds { get; set; } = 5;
  public int HeartbeatIntervalSeconds { get; set; } = 30;
  public int BatchSize { get; set; } = 10;
}

/// <summary>
/// Keeps the durable PayPal-to-POS import moving independently from the public
/// ordering switch. Pausing sales must never strand a payment already captured.
/// </summary>
public sealed class RestaurantOnlineOrderImportWorker : BackgroundService
{
  private readonly RestaurantOnlineOrderProcessingOptions _options;
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly ILogger<RestaurantOnlineOrderImportWorker> _logger;
  private readonly TimeProvider _clock;

  public RestaurantOnlineOrderImportWorker(
    IOptions<RestaurantOnlineOrderProcessingOptions> options,
    IServiceScopeFactory scopeFactory,
    ILogger<RestaurantOnlineOrderImportWorker> logger,
    TimeProvider? clock = null)
  {
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _scopeFactory = scopeFactory;
    _logger = logger;
    _clock = clock ?? TimeProvider.System;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    if (!_options.Enabled)
    {
      _logger.LogWarning(
        "El procesador de pedidos en linea esta deshabilitado. Los cobros nuevos quedaran bloqueados por falta de heartbeat.");
      return;
    }

    var pollInterval = TimeSpan.FromSeconds(Math.Clamp(_options.PollIntervalSeconds, 1, 60));
    var heartbeatInterval = TimeSpan.FromSeconds(Math.Clamp(_options.HeartbeatIntervalSeconds, 10, 60));
    var batchSize = Math.Clamp(_options.BatchSize, 1, 50);
    var nextHeartbeatAt = DateTimeOffset.MinValue;

    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<IOnlineOrderImportProcessor>();
        var now = _clock.GetUtcNow();
        if (now >= nextHeartbeatAt)
        {
          await processor.RecordHeartbeatAsync(stoppingToken);
          nextHeartbeatAt = now.Add(heartbeatInterval);
        }

        var processed = await processor.ProcessPendingAsync(batchSize, stoppingToken);
        if (processed > 0)
        {
          _logger.LogInformation("Se importaron {Count} pedido(s) en linea al POS.", processed);
        }
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        break;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Fallo un ciclo del importador de pedidos en linea; se reintentara automaticamente.");
      }

      try
      {
        await Task.Delay(pollInterval, _clock, stoppingToken);
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        break;
      }
    }
  }
}
