using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrionERP.Infrastructure.Features.Mail;

public sealed class MicrosoftGraphMailClient<TOptions> : IMicrosoftGraphMailClient<TOptions>
  where TOptions : MicrosoftGraphMailOptions
{
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

  private readonly HttpClient _httpClient;
  private readonly IOptions<TOptions> _options;
  private readonly ILogger<MicrosoftGraphMailClient<TOptions>> _logger;

  public MicrosoftGraphMailClient(
    HttpClient httpClient,
    IOptions<TOptions> options,
    ILogger<MicrosoftGraphMailClient<TOptions>> logger)
  {
    _httpClient = httpClient;
    _options = options;
    _logger = logger;
  }

  public async Task SendEmailAsync(
    string email,
    string subject,
    string message,
    CancellationToken ct = default)
    => await SendEmailAsync(
      new MicrosoftGraphMailMessage
      {
        ToRecipients = [email],
        Subject = subject,
        Message = message
      },
      ct);

  public async Task SendEmailAsync(
    MicrosoftGraphMailMessage mail,
    CancellationToken ct = default)
  {
    var graphMessage = BuildGraphMessage(mail);

    var options = _options.Value;
    EnsureConfigured(options);

    var accessToken = await RequestAccessTokenAsync(options, ct);

    using var request = new HttpRequestMessage(
      HttpMethod.Post,
      $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(options.SenderAddress)}/sendMail");

    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    request.Content = JsonContent.Create(
      new GraphSendMailRequest(graphMessage, true),
      options: JsonOptions);

    using var response = await _httpClient.SendAsync(request, ct);
    if (!response.IsSuccessStatusCode)
    {
      var responseBody = await response.Content.ReadAsStringAsync(ct);
      _logger.LogError(
        "Graph sendMail failed for sender {SenderAddress} with status code {StatusCode}. Response: {ResponseBody}",
        options.SenderAddress,
        (int)response.StatusCode,
        responseBody);

      throw new InvalidOperationException("No se pudo enviar el correo mediante Microsoft Graph.");
    }

    _logger.LogInformation(
      "Graph email queued from {SenderAddress} to {RecipientCount} recipient(s).",
      options.SenderAddress,
      graphMessage.ToRecipients.Count + graphMessage.CcRecipients.Count + graphMessage.BccRecipients.Count);
  }

  public async Task<string> CreateDraftAsync(
    MicrosoftGraphMailMessage mail,
    CancellationToken ct = default)
  {
    var graphMessage = BuildGraphMessage(mail);
    var options = _options.Value;
    EnsureConfigured(options);

    var accessToken = await RequestAccessTokenAsync(options, ct);
    using var request = CreateImmutableMessageRequest(
      HttpMethod.Post,
      $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(options.SenderAddress)}/messages",
      accessToken);
    request.Content = JsonContent.Create(graphMessage, options: JsonOptions);

    using var response = await _httpClient.SendAsync(request, ct);
    var responseBody = await response.Content.ReadAsStringAsync(ct);
    var payload = response.IsSuccessStatusCode
      ? JsonSerializer.Deserialize<GraphMessageStateResponse>(responseBody, JsonOptions)
      : null;
    if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(payload?.Id) || payload.IsDraft != true)
    {
      _logger.LogError(
        "Graph draft creation failed for sender {SenderAddress} with status code {StatusCode}.",
        options.SenderAddress,
        (int)response.StatusCode);
      throw new InvalidOperationException("No se pudo crear el borrador de correo mediante Microsoft Graph.");
    }

    _logger.LogInformation(
      "Graph durable email draft created from {SenderAddress} for {RecipientCount} recipient(s).",
      options.SenderAddress,
      graphMessage.ToRecipients.Count + graphMessage.CcRecipients.Count + graphMessage.BccRecipients.Count);
    return payload.Id;
  }

  public async Task<MicrosoftGraphMailMessageState> GetMessageStateAsync(
    string immutableMessageId,
    CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(immutableMessageId);
    var options = _options.Value;
    EnsureConfigured(options);

    var accessToken = await RequestAccessTokenAsync(options, ct);
    using var request = CreateImmutableMessageRequest(
      HttpMethod.Get,
      $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(options.SenderAddress)}/messages/{Uri.EscapeDataString(immutableMessageId.Trim())}?$select=id,isDraft",
      accessToken);
    using var response = await _httpClient.SendAsync(request, ct);
    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
      return MicrosoftGraphMailMessageState.Missing;

    var responseBody = await response.Content.ReadAsStringAsync(ct);
    var payload = response.IsSuccessStatusCode
      ? JsonSerializer.Deserialize<GraphMessageStateResponse>(responseBody, JsonOptions)
      : null;
    if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(payload?.Id) || payload.IsDraft is null)
    {
      _logger.LogError(
        "Graph message-state lookup failed for sender {SenderAddress} with status code {StatusCode}.",
        options.SenderAddress,
        (int)response.StatusCode);
      throw new InvalidOperationException("No se pudo consultar el estado del correo mediante Microsoft Graph.");
    }

    return payload.IsDraft.Value
      ? MicrosoftGraphMailMessageState.Draft
      : MicrosoftGraphMailMessageState.Sent;
  }

  public async Task SendDraftAsync(
    string immutableMessageId,
    CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(immutableMessageId);
    var options = _options.Value;
    EnsureConfigured(options);

    var accessToken = await RequestAccessTokenAsync(options, ct);
    using var request = CreateImmutableMessageRequest(
      HttpMethod.Post,
      $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(options.SenderAddress)}/messages/{Uri.EscapeDataString(immutableMessageId.Trim())}/send",
      accessToken);
    using var response = await _httpClient.SendAsync(request, ct);
    if (!response.IsSuccessStatusCode)
    {
      _logger.LogError(
        "Graph draft send failed for sender {SenderAddress} with status code {StatusCode}.",
        options.SenderAddress,
        (int)response.StatusCode);
      throw new InvalidOperationException("No se pudo enviar el borrador mediante Microsoft Graph.");
    }

    _logger.LogInformation("Graph durable email draft accepted for delivery from {SenderAddress}.", options.SenderAddress);
  }

  private static HttpRequestMessage CreateImmutableMessageRequest(
    HttpMethod method,
    string requestUri,
    string accessToken)
  {
    var request = new HttpRequestMessage(method, requestUri);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    request.Headers.TryAddWithoutValidation("Prefer", "IdType=\"ImmutableId\"");
    return request;
  }

  private static GraphMessage BuildGraphMessage(MicrosoftGraphMailMessage mail)
  {
    ArgumentNullException.ThrowIfNull(mail);
    ArgumentException.ThrowIfNullOrWhiteSpace(mail.Subject);
    ArgumentException.ThrowIfNullOrWhiteSpace(mail.Message);

    var toRecipients = NormalizeRecipients(mail.ToRecipients);
    var ccRecipients = NormalizeRecipients(mail.CcRecipients);
    var bccRecipients = NormalizeRecipients(mail.BccRecipients);
    if (toRecipients.Count == 0)
      throw new ArgumentException("At least one recipient is required.", nameof(mail));

    return new GraphMessage(
      mail.Subject,
      new GraphItemBody(LooksLikeHtml(mail.Message) ? "HTML" : "Text", mail.Message),
      BuildRecipients(toRecipients),
      BuildRecipients(ccRecipients),
      BuildRecipients(bccRecipients));
  }

  private static IReadOnlyList<string> NormalizeRecipients(IReadOnlyList<string>? recipients)
    => recipients is null
      ? Array.Empty<string>()
      : recipients
        .Where(recipient => !string.IsNullOrWhiteSpace(recipient))
        .Select(recipient => recipient.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

  private static IReadOnlyList<GraphRecipient> BuildRecipients(IReadOnlyList<string> recipients)
    => recipients
      .Select(recipient => new GraphRecipient(new GraphEmailAddress(recipient)))
      .ToArray();

  private async Task<string> RequestAccessTokenAsync(TOptions options, CancellationToken ct)
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

    using var response = await _httpClient.SendAsync(request, ct);
    var responseBody = await response.Content.ReadAsStringAsync(ct);
    var payload = response.IsSuccessStatusCode
      ? JsonSerializer.Deserialize<GraphTokenResponse>(responseBody, JsonOptions)
      : null;

    if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(payload?.AccessToken))
    {
      _logger.LogError(
        "Graph token request failed for sender {SenderAddress} with status code {StatusCode}. Response: {ResponseBody}",
        options.SenderAddress,
        (int)response.StatusCode,
        responseBody);

      throw new InvalidOperationException("No se pudo obtener el token de acceso para Microsoft Graph.");
    }

    return payload.AccessToken;
  }

  private static void EnsureConfigured(TOptions options)
  {
    if (string.IsNullOrWhiteSpace(options.TenantId) ||
        string.IsNullOrWhiteSpace(options.ClientId) ||
        string.IsNullOrWhiteSpace(options.ClientSecret) ||
        string.IsNullOrWhiteSpace(options.SenderAddress))
    {
      throw new InvalidOperationException(
        $"{GetOptionsSectionName()} no esta configurado completamente. Revisa TenantId, ClientId, ClientSecret y SenderAddress.");
    }
  }

  private static string GetOptionsSectionName()
  {
    var sectionField = typeof(TOptions).GetField(
      "SectionName",
      BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

    return sectionField?.GetValue(null) as string ?? typeof(TOptions).Name;
  }

  private static bool LooksLikeHtml(string body)
    => body.Contains('<') && body.Contains('>');

  private sealed record GraphTokenResponse([property: JsonPropertyName("access_token")] string AccessToken);

  private sealed record GraphMessageStateResponse(string Id, bool? IsDraft);

  private sealed record GraphSendMailRequest(GraphMessage Message, bool SaveToSentItems);

  private sealed record GraphMessage(
    string Subject,
    GraphItemBody Body,
    IReadOnlyList<GraphRecipient> ToRecipients,
    IReadOnlyList<GraphRecipient> CcRecipients,
    IReadOnlyList<GraphRecipient> BccRecipients);

  private sealed record GraphItemBody(string ContentType, string Content);

  private sealed record GraphRecipient(GraphEmailAddress EmailAddress);

  private sealed record GraphEmailAddress(string Address);
}
