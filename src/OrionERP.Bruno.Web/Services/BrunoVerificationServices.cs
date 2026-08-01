using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using OrionERP.Bruno.Web.Configuration;

namespace OrionERP.Bruno.Web.Services;

public interface IBrunoTurnstileService
{
  Task<bool> ValidateAsync(string? token, string? remoteIp, string expectedAction, CancellationToken ct = default);
}

public sealed class BrunoTurnstileService : IBrunoTurnstileService
{
  private readonly HttpClient _httpClient;
  private readonly IOptions<BrunoTurnstileOptions> _options;
  private readonly IWebHostEnvironment _environment;
  private readonly ILogger<BrunoTurnstileService> _logger;

  public BrunoTurnstileService(
    HttpClient httpClient,
    IOptions<BrunoTurnstileOptions> options,
    IWebHostEnvironment environment,
    ILogger<BrunoTurnstileService> logger)
  {
    _httpClient = httpClient;
    _options = options;
    _environment = environment;
    _logger = logger;
  }

  public async Task<bool> ValidateAsync(
    string? token,
    string? remoteIp,
    string expectedAction,
    CancellationToken ct = default)
  {
    var options = _options.Value;
    if (!options.IsConfigured)
    {
      if (_environment.IsDevelopment()) return true;

      _logger.LogError(
        "Turnstile validation is unavailable because the production key pair is not configured.");
      return false;
    }
    if (string.IsNullOrWhiteSpace(token))
    {
      _logger.LogWarning("Turnstile validation was rejected because the client token was missing.");
      return false;
    }
    if (token.Length > 2048)
    {
      _logger.LogWarning("Turnstile validation was rejected because the client token was too long.");
      return false;
    }

    var form = new Dictionary<string, string>
    {
      ["secret"] = options.SecretKey,
      ["response"] = token
    };
    if (!string.IsNullOrWhiteSpace(remoteIp)) form["remoteip"] = remoteIp;

    HttpResponseMessage response;
    try
    {
      response = await _httpClient.PostAsync(
        "https://challenges.cloudflare.com/turnstile/v0/siteverify",
        new FormUrlEncodedContent(form),
        ct);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
      _logger.LogError("Turnstile Siteverify timed out.");
      return false;
    }
    catch (HttpRequestException ex)
    {
      _logger.LogError(ex, "Turnstile Siteverify could not be reached.");
      return false;
    }

    using (response)
    {
      if (!response.IsSuccessStatusCode)
      {
        _logger.LogError(
          "Turnstile Siteverify returned HTTP status {StatusCode}.",
          (int)response.StatusCode);
        return false;
      }

      TurnstileResult? result;
      try
      {
        result = await response.Content.ReadFromJsonAsync<TurnstileResult>(cancellationToken: ct);
      }
      catch (System.Text.Json.JsonException ex)
      {
        _logger.LogError(ex, "Turnstile Siteverify returned an invalid JSON response.");
        return false;
      }

      if (result?.Success != true)
      {
        _logger.LogWarning(
          "Turnstile rejected the token. ErrorCodes={ErrorCodes} Hostname={Hostname} Action={Action}",
          result?.ErrorCodes is { Length: > 0 } ? string.Join(',', result.ErrorCodes) : "none",
          result?.Hostname ?? "unknown",
          result?.Action ?? "unknown");
        return false;
      }

      if (!string.Equals(result.Action, expectedAction, StringComparison.Ordinal))
      {
        _logger.LogWarning(
          "Turnstile returned an unexpected action. ExpectedAction={ExpectedAction} ActualAction={ActualAction}",
          expectedAction,
          result.Action ?? "none");
        return false;
      }

      if (!_environment.IsDevelopment() &&
          !string.Equals(result.Hostname, options.ExpectedHostname, StringComparison.OrdinalIgnoreCase))
      {
        _logger.LogWarning(
          "Turnstile returned an unexpected hostname. ExpectedHostname={ExpectedHostname} ActualHostname={ActualHostname}",
          options.ExpectedHostname,
          result.Hostname ?? "none");
        return false;
      }

      return true;
    }
  }

  private sealed class TurnstileResult
  {
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("error-codes")] public string[] ErrorCodes { get; set; } = [];
    [JsonPropertyName("hostname")] public string? Hostname { get; set; }
    [JsonPropertyName("action")] public string? Action { get; set; }
  }
}
