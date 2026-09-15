using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

public sealed class RestaurantOnlineQuoteTokenService : IOnlineRestaurantQuoteTokenService
{
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
  private readonly IDataProtector _protector;

  public RestaurantOnlineQuoteTokenService(IDataProtectionProvider provider)
  {
    ArgumentNullException.ThrowIfNull(provider);
    _protector = provider.CreateProtector("OrionERP.Restaurant.OnlineQuote.v1");
  }

  public string Protect(RestaurantOnlineQuoteSnapshot quote)
  {
    ArgumentNullException.ThrowIfNull(quote);
    return _protector.Protect(JsonSerializer.Serialize(quote, JsonOptions));
  }

  public bool TryUnprotect(string token, out RestaurantOnlineQuoteSnapshot? quote)
  {
    quote = null;
    if (string.IsNullOrWhiteSpace(token)) return false;
    try
    {
      quote = JsonSerializer.Deserialize<RestaurantOnlineQuoteSnapshot>(
        _protector.Unprotect(token), JsonOptions);
      return quote is not null;
    }
    catch (Exception exception) when (exception is CryptographicException or JsonException)
    {
      return false;
    }
  }
}
