using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace OrionERP.Web.Features.Restaurante;

public sealed class RestaurantRealtimeClient : IAsyncDisposable
{
  private readonly NavigationManager _navigation;
  private readonly IHttpContextAccessor _httpContextAccessor;
  private readonly ILogger<RestaurantRealtimeClient> _logger;
  private readonly SemaphoreSlim _connectionLock = new(1, 1);
  private HubConnection? _connection;
  private string? _rfc;
  private int _siteId;

  public RestaurantRealtimeClient(
    NavigationManager navigation,
    IHttpContextAccessor httpContextAccessor,
    ILogger<RestaurantRealtimeClient> logger)
  {
    _navigation = navigation;
    _httpContextAccessor = httpContextAccessor;
    _logger = logger;
  }

  public event Func<RestaurantRealtimeEvent, Task>? EventReceived;
  public bool IsConnected => _connection?.State == HubConnectionState.Connected;

  public async Task SubscribeAsync(string rfc, int siteId, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(rfc) || siteId <= 0) return;
    _rfc = rfc.Trim().ToUpperInvariant();
    _siteId = siteId;
    await EnsureConnectedAsync(ct);
    if (_connection?.State == HubConnectionState.Connected)
    {
      await _connection.InvokeAsync("Subscribe", _rfc, _siteId, ct);
    }
  }

  private async Task EnsureConnectedAsync(CancellationToken ct)
  {
    if (_connection?.State is HubConnectionState.Connected or HubConnectionState.Connecting or HubConnectionState.Reconnecting) return;
    await _connectionLock.WaitAsync(ct);
    try
    {
      if (_connection is null)
      {
        var hubUri = new Uri(new Uri(_navigation.BaseUri), "hubs/restaurante");
        var cookies = BuildCookieContainer(hubUri);
        _connection = new HubConnectionBuilder()
          .WithUrl(hubUri, options => options.Cookies = cookies)
          .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)])
          .Build();
        _connection.On<RestaurantRealtimeEvent>("restaurantEvent", HandleEventAsync);
        _connection.Reconnected += async _ =>
        {
          if (!string.IsNullOrWhiteSpace(_rfc) && _siteId > 0)
          {
            await _connection.InvokeAsync("Subscribe", _rfc, _siteId);
          }
        };
      }
      if (_connection.State == HubConnectionState.Disconnected)
      {
        try { await _connection.StartAsync(ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "SignalR de Restaurante no pudo conectarse; continúa el sondeo periódico."); }
      }
    }
    finally { _connectionLock.Release(); }
  }

  private CookieContainer BuildCookieContainer(Uri hubUri)
  {
    var container = new CookieContainer();
    var requestCookies = _httpContextAccessor.HttpContext?.Request.Cookies;
    if (requestCookies is null) return container;
    foreach (var cookie in requestCookies)
    {
      try { container.Add(hubUri, new Cookie(cookie.Key, cookie.Value, "/", hubUri.Host)); }
      catch (CookieException) { }
    }
    return container;
  }

  private async Task HandleEventAsync(RestaurantRealtimeEvent eventInfo)
  {
    var handlers = EventReceived;
    if (handlers is null) return;
    foreach (Func<RestaurantRealtimeEvent, Task> handler in handlers.GetInvocationList())
    {
      try { await handler(eventInfo); }
      catch (Exception ex) { _logger.LogWarning(ex, "Un consumidor no procesó el evento {EventType} de Restaurante.", eventInfo.EventType); }
    }
  }

  public async ValueTask DisposeAsync()
  {
    if (_connection is not null) await _connection.DisposeAsync();
    _connectionLock.Dispose();
  }
}

public sealed class RestaurantRealtimeEvent
{
  public long Id { get; set; }
  public string EventType { get; set; } = string.Empty;
  public string AggregateId { get; set; } = string.Empty;
  public string Payload { get; set; } = string.Empty;
  public DateTime OccurredAt { get; set; }
}
