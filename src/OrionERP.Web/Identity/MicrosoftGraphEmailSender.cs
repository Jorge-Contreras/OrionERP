using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrionERP.Web.Identity;

public sealed class MicrosoftGraphEmailSender : IEmailSender
{
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

  private readonly HttpClient _httpClient;
  private readonly IOptions<GraphMailOptions> _options;
  private readonly ILogger<MicrosoftGraphEmailSender> _logger;

  public MicrosoftGraphEmailSender(
    HttpClient httpClient,
    IOptions<GraphMailOptions> options,
    ILogger<MicrosoftGraphEmailSender> logger)
  {
    _httpClient = httpClient;
    _options = options;
    _logger = logger;
  }

  public async Task SendEmailAsync(string email, string subject, string htmlMessage)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(email);
    ArgumentException.ThrowIfNullOrWhiteSpace(subject);
    ArgumentException.ThrowIfNullOrWhiteSpace(htmlMessage);

    var options = _options.Value;
    EnsureConfigured(options);

    var accessToken = await RequestAccessTokenAsync(options);

    using var request = new HttpRequestMessage(
      HttpMethod.Post,
      $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(options.SenderAddress)}/sendMail");

    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    request.Content = JsonContent.Create(
      new GraphSendMailRequest(
        new GraphMessage(
          subject,
          new GraphItemBody(LooksLikeHtml(htmlMessage) ? "HTML" : "Text", htmlMessage),
          [new GraphRecipient(new GraphEmailAddress(email))]),
        true),
      options: JsonOptions);

    using var response = await _httpClient.SendAsync(request);
    if (!response.IsSuccessStatusCode)
    {
      var responseBody = await response.Content.ReadAsStringAsync();
      _logger.LogError(
        "Graph sendMail failed with status code {StatusCode}. Response: {ResponseBody}",
        (int)response.StatusCode,
        responseBody);

      throw new InvalidOperationException("No se pudo enviar el correo de recuperacion mediante Microsoft Graph.");
    }

    _logger.LogInformation("Password reset email queued for {Recipient}.", email);
  }

  private async Task<string> RequestAccessTokenAsync(GraphMailOptions options)
  {
    using var request = new HttpRequestMessage(
      HttpMethod.Post,
      $"https://login.microsoftonline.com/{options.TenantId}/oauth2/v2.0/token")
    {
      Content = new FormUrlEncodedContent(new Dictionary<string, string>
      {
        ["client_id"] = options.ClientId,
        ["client_secret"] = options.ClientSecret,
        ["scope"] = "https://graph.microsoft.com/.default",
        ["grant_type"] = "client_credentials"
      })
    };

    using var response = await _httpClient.SendAsync(request);
    var payload = await response.Content.ReadFromJsonAsync<GraphTokenResponse>(JsonOptions);

    if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(payload?.AccessToken))
    {
      var responseBody = await response.Content.ReadAsStringAsync();
      _logger.LogError(
        "Graph token request failed with status code {StatusCode}. Response: {ResponseBody}",
        (int)response.StatusCode,
        responseBody);

      throw new InvalidOperationException("No se pudo obtener el token de acceso para Microsoft Graph.");
    }

    return payload.AccessToken;
  }

  private static void EnsureConfigured(GraphMailOptions options)
  {
    if (string.IsNullOrWhiteSpace(options.TenantId) ||
        string.IsNullOrWhiteSpace(options.ClientId) ||
        string.IsNullOrWhiteSpace(options.ClientSecret) ||
        string.IsNullOrWhiteSpace(options.SenderAddress))
    {
      throw new InvalidOperationException(
        "GraphMail no esta configurado completamente. Revisa TenantId, ClientId, ClientSecret y SenderAddress.");
    }
  }

  private static bool LooksLikeHtml(string body)
    => body.Contains('<') && body.Contains('>');

  private sealed record GraphTokenResponse([property: JsonPropertyName("access_token")] string AccessToken);

  private sealed record GraphSendMailRequest(GraphMessage Message, bool SaveToSentItems);

  private sealed record GraphMessage(
    string Subject,
    GraphItemBody Body,
    IReadOnlyList<GraphRecipient> ToRecipients);

  private sealed record GraphItemBody(string ContentType, string Content);

  private sealed record GraphRecipient(GraphEmailAddress EmailAddress);

  private sealed record GraphEmailAddress(string Address);
}
