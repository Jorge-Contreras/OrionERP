using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using OrionERP.Bruno.Web.Configuration;

namespace OrionERP.Bruno.Web.Services;

public interface IBrunoTurnstileService
{
  Task<bool> ValidateAsync(string? token, string? remoteIp, CancellationToken ct = default);
}

public sealed class BrunoTurnstileService : IBrunoTurnstileService
{
  private readonly HttpClient _httpClient;
  private readonly IOptions<BrunoTurnstileOptions> _options;
  private readonly IWebHostEnvironment _environment;

  public BrunoTurnstileService(
    HttpClient httpClient,
    IOptions<BrunoTurnstileOptions> options,
    IWebHostEnvironment environment)
  {
    _httpClient = httpClient;
    _options = options;
    _environment = environment;
  }

  public async Task<bool> ValidateAsync(string? token, string? remoteIp, CancellationToken ct = default)
  {
    var options = _options.Value;
    if (!options.IsConfigured)
    {
      return _environment.IsDevelopment();
    }
    if (string.IsNullOrWhiteSpace(token))
    {
      return false;
    }
    using var response = await _httpClient.PostAsync(
      "https://challenges.cloudflare.com/turnstile/v0/siteverify",
      new FormUrlEncodedContent(new Dictionary<string, string>
      {
        ["secret"] = options.SecretKey,
        ["response"] = token,
        ["remoteip"] = remoteIp ?? string.Empty
      }),
      ct);
    if (!response.IsSuccessStatusCode) return false;
    var result = await response.Content.ReadFromJsonAsync<TurnstileResult>(cancellationToken: ct);
    return result?.Success == true;
  }

  private sealed class TurnstileResult
  {
    [JsonPropertyName("success")] public bool Success { get; set; }
  }
}
