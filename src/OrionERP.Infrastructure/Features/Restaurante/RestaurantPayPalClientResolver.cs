using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Payments.PayPal;
using OrionERP.Application.Features.Restaurante;
using OrionERP.Infrastructure.Features.Payments.PayPal;

namespace OrionERP.Infrastructure.Features.Restaurante;

public sealed class RestaurantPayPalClientResolver : IRestaurantPayPalClientResolver
{
  private readonly IPayPalOrdersClient _activeClient;
  private readonly RestaurantCheckoutOptions _options;
  private readonly IHttpClientFactory _httpClients;
  private readonly ILoggerFactory _loggers;
  private readonly Dictionary<string, IPayPalOrdersClient> _historical = new(StringComparer.Ordinal);
  private readonly object _gate = new();

  public RestaurantPayPalClientResolver(
    IPayPalOrdersClient activeClient,
    IOptions<RestaurantCheckoutOptions> options,
    IHttpClientFactory httpClients,
    ILoggerFactory loggers)
  {
    _activeClient = activeClient;
    _options = options.Value;
    _httpClients = httpClients;
    _loggers = loggers;
  }

  public IPayPalOrdersClient Resolve(string merchantProfileKey)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(merchantProfileKey);
    var key = merchantProfileKey.Trim();
    if (string.Equals(key, _options.MerchantProfileKey, StringComparison.Ordinal))
      return _activeClient;
    if (!_options.HistoricalMerchantProfiles.TryGetValue(key, out var profile))
      throw new InvalidOperationException($"El perfil mercantil histórico '{key}' no está configurado.");
    lock (_gate)
    {
      if (_historical.TryGetValue(key, out var existing)) return existing;
      var client = new PayPalOrdersClient<PayPalClientOptions>(
        _httpClients.CreateClient("RestaurantPayPalHistorical"),
        Options.Create(profile),
        _loggers.CreateLogger<PayPalOrdersClient<PayPalClientOptions>>());
      _historical[key] = client;
      return client;
    }
  }
}
