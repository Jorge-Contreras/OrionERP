using System.Net.Http.Headers;
using System.Text;
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

public interface IBrunoPhoneVerificationService
{
  Task SendAsync(string phone, CancellationToken ct = default);
  Task<bool> CheckAsync(string phone, string code, CancellationToken ct = default);
}

public sealed class BrunoTwilioPhoneVerificationService : IBrunoPhoneVerificationService
{
  private readonly HttpClient _httpClient;
  private readonly IOptions<BrunoTwilioVerifyOptions> _options;
  private readonly IWebHostEnvironment _environment;

  public BrunoTwilioPhoneVerificationService(
    HttpClient httpClient,
    IOptions<BrunoTwilioVerifyOptions> options,
    IWebHostEnvironment environment)
  {
    _httpClient = httpClient;
    _options = options;
    _environment = environment;
  }

  public async Task SendAsync(string phone, CancellationToken ct = default)
  {
    var options = _options.Value;
    if (!options.IsConfigured)
    {
      if (_environment.IsDevelopment()) return;
      throw new InvalidOperationException("La verificación telefónica aún no está disponible.");
    }
    using var request = CreateRequest(
      HttpMethod.Post,
      $"https://verify.twilio.com/v2/Services/{Uri.EscapeDataString(options.ServiceSid)}/Verifications",
      options,
      new Dictionary<string, string> { ["To"] = NormalizePhone(phone), ["Channel"] = "sms" });
    using var response = await _httpClient.SendAsync(request, ct);
    if (!response.IsSuccessStatusCode)
      throw new InvalidOperationException("No fue posible enviar el código de verificación.");
  }

  public async Task<bool> CheckAsync(string phone, string code, CancellationToken ct = default)
  {
    var options = _options.Value;
    if (!options.IsConfigured)
      return _environment.IsDevelopment() && code == "000000";
    using var request = CreateRequest(
      HttpMethod.Post,
      $"https://verify.twilio.com/v2/Services/{Uri.EscapeDataString(options.ServiceSid)}/VerificationCheck",
      options,
      new Dictionary<string, string> { ["To"] = NormalizePhone(phone), ["Code"] = code.Trim() });
    using var response = await _httpClient.SendAsync(request, ct);
    if (!response.IsSuccessStatusCode) return false;
    var result = await response.Content.ReadFromJsonAsync<TwilioCheckResult>(cancellationToken: ct);
    return string.Equals(result?.Status, "approved", StringComparison.OrdinalIgnoreCase);
  }

  private static HttpRequestMessage CreateRequest(
    HttpMethod method,
    string url,
    BrunoTwilioVerifyOptions options,
    IReadOnlyDictionary<string, string> form)
  {
    var request = new HttpRequestMessage(method, url) { Content = new FormUrlEncodedContent(form) };
    var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{options.AccountSid}:{options.AuthToken}"));
    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    return request;
  }

  private static string NormalizePhone(string phone)
  {
    var digits = new string(phone.Where(char.IsDigit).ToArray());
    if (digits.Length == 10) digits = $"52{digits}";
    return $"+{digits}";
  }

  private sealed class TwilioCheckResult
  {
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
  }
}
