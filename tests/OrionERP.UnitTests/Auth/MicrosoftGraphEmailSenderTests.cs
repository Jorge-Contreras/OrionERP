using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrionERP.Infrastructure.Features.Mail;
using OrionERP.Web.Identity;

namespace OrionERP.UnitTests.Auth;

public class MicrosoftGraphEmailSenderTests
{
  [Fact]
  public async Task GraphMailClient_RequestsTokenAndDispatchesMail()
  {
    var handler = new RecordingHttpMessageHandler();
    var client = new HttpClient(handler);
    var sender = new MicrosoftGraphMailClient<GraphMailOptions>(
      client,
      Options.Create(new GraphMailOptions
      {
        TenantId = "tenant-id",
        ClientId = "client-id",
        ClientSecret = "client-secret",
        SenderAddress = "info@orion.land"
      }),
      NullLogger<MicrosoftGraphMailClient<GraphMailOptions>>.Instance);

    await sender.SendEmailAsync("user@orion.land", "Restablecer", "<p>Hola desde OrionERP</p>");

    Assert.Equal(2, handler.Requests.Count);

    var tokenRequest = handler.Requests[0];
    Assert.Equal("POST", tokenRequest.Method);
    Assert.Equal("https://login.microsoftonline.com/tenant-id/oauth2/v2.0/token", tokenRequest.Uri);
    Assert.Contains("client_id=client-id", tokenRequest.Body, StringComparison.Ordinal);
    Assert.Contains("client_secret=client-secret", tokenRequest.Body, StringComparison.Ordinal);
    Assert.Contains("grant_type=client_credentials", tokenRequest.Body, StringComparison.Ordinal);

    var mailRequest = handler.Requests[1];
    Assert.Equal("POST", mailRequest.Method);
    Assert.Equal("https://graph.microsoft.com/v1.0/users/info%40orion.land/sendMail", mailRequest.Uri);
    Assert.Equal("Bearer test-access-token", mailRequest.AuthorizationHeader);
    Assert.Contains("\"subject\":\"Restablecer\"", mailRequest.Body, StringComparison.Ordinal);
    Assert.Contains("\"contentType\":\"HTML\"", mailRequest.Body, StringComparison.Ordinal);
    Assert.Contains("\"content\":\"\\u003Cp\\u003EHola desde OrionERP\\u003C/p\\u003E\"", mailRequest.Body, StringComparison.Ordinal);
    Assert.Contains("\"address\":\"user@orion.land\"", mailRequest.Body, StringComparison.Ordinal);
  }

  [Fact]
  public async Task GraphMailClient_SerializesBccRecipients()
  {
    var handler = new RecordingHttpMessageHandler();
    var client = new HttpClient(handler);
    var sender = new MicrosoftGraphMailClient<GraphMailOptions>(
      client,
      Options.Create(new GraphMailOptions
      {
        TenantId = "tenant-id",
        ClientId = "client-id",
        ClientSecret = "client-secret",
        SenderAddress = "info@orion.land"
      }),
      NullLogger<MicrosoftGraphMailClient<GraphMailOptions>>.Instance);

    await sender.SendEmailAsync(new MicrosoftGraphMailMessage
    {
      ToRecipients = ["cliente@example.com"],
      BccRecipients = ["recepcion@bonhomiasuites.com"],
      Subject = "Confirmacion",
      Message = "<p>Reserva confirmada</p>"
    });

    var mailRequest = Assert.Single(
      handler.Requests,
      request => request.Uri.Contains("/sendMail", StringComparison.Ordinal));
    Assert.Contains("\"toRecipients\"", mailRequest.Body, StringComparison.Ordinal);
    Assert.Contains("\"bccRecipients\"", mailRequest.Body, StringComparison.Ordinal);
    Assert.Contains("\"address\":\"cliente@example.com\"", mailRequest.Body, StringComparison.Ordinal);
    Assert.Contains("\"address\":\"recepcion@bonhomiasuites.com\"", mailRequest.Body, StringComparison.Ordinal);
  }

  [Fact]
  public async Task GraphMailClient_CreatesReadsAndSendsDurableDraftByImmutableId()
  {
    var handler = new RecordingHttpMessageHandler();
    var sender = CreateGraphClient(handler);

    var immutableMessageId = await sender.CreateDraftAsync(new MicrosoftGraphMailMessage
    {
      ToRecipients = ["cliente@example.com"],
      Subject = "Pedido confirmado",
      Message = "<p>Tu pedido fue confirmado.</p>"
    });
    var state = await sender.GetMessageStateAsync(immutableMessageId);
    await sender.SendDraftAsync(immutableMessageId);

    Assert.Equal("immutable-message-id", immutableMessageId);
    Assert.Equal(MicrosoftGraphMailMessageState.Draft, state);
    var graphRequests = handler.Requests
      .Where(request => request.Uri.StartsWith("https://graph.microsoft.com/", StringComparison.Ordinal))
      .ToArray();
    Assert.Equal(3, graphRequests.Length);
    Assert.Equal("https://graph.microsoft.com/v1.0/users/info%40orion.land/messages", graphRequests[0].Uri);
    Assert.Equal("https://graph.microsoft.com/v1.0/users/info%40orion.land/messages/immutable-message-id?$select=id,isDraft", graphRequests[1].Uri);
    Assert.Equal("https://graph.microsoft.com/v1.0/users/info%40orion.land/messages/immutable-message-id/send", graphRequests[2].Uri);
    Assert.All(graphRequests, request => Assert.Equal("IdType=\"ImmutableId\"", request.PreferHeader));
  }

  [Fact]
  public async Task GraphMailClient_RecognizesAlreadySentAndMissingDurableMessages()
  {
    var sentHandler = new RecordingHttpMessageHandler { ReturnedMessageIsDraft = false };
    var sentClient = CreateGraphClient(sentHandler);
    Assert.Equal(
      MicrosoftGraphMailMessageState.Sent,
      await sentClient.GetMessageStateAsync("immutable-message-id"));

    var missingHandler = new RecordingHttpMessageHandler { ReturnMessageNotFound = true };
    var missingClient = CreateGraphClient(missingHandler);
    Assert.Equal(
      MicrosoftGraphMailMessageState.Missing,
      await missingClient.GetMessageStateAsync("immutable-message-id"));
  }

  [Fact]
  public async Task GraphMailClient_ThrowsWhenGraphMailConfigIsIncomplete()
  {
    var sender = new MicrosoftGraphMailClient<GraphMailOptions>(
      new HttpClient(new RecordingHttpMessageHandler()),
      Options.Create(new GraphMailOptions()),
      NullLogger<MicrosoftGraphMailClient<GraphMailOptions>>.Instance);

    var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
      sender.SendEmailAsync("user@orion.land", "Restablecer", "Hola"));

    Assert.Contains("GraphMail", exception.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task PasswordResetSender_DelegatesToGraphMailClient()
  {
    var graphClient = new FakeGraphMailClient();
    var sender = new MicrosoftGraphEmailSender(
      graphClient,
      NullLogger<MicrosoftGraphEmailSender>.Instance);

    await sender.SendEmailAsync("user@orion.land", "Restablecer", "<p>Hola</p>");

    Assert.Equal("user@orion.land", graphClient.LastEmail);
    Assert.Equal("Restablecer", graphClient.LastSubject);
    Assert.Equal("<p>Hola</p>", graphClient.LastMessage);
  }

  private static MicrosoftGraphMailClient<GraphMailOptions> CreateGraphClient(
    RecordingHttpMessageHandler handler)
    => new(
      new HttpClient(handler),
      Options.Create(new GraphMailOptions
      {
        TenantId = "tenant-id",
        ClientId = "client-id",
        ClientSecret = "client-secret",
        SenderAddress = "info@orion.land"
      }),
      NullLogger<MicrosoftGraphMailClient<GraphMailOptions>>.Instance);

  private sealed class RecordingHttpMessageHandler : HttpMessageHandler
  {
    public List<CapturedRequest> Requests { get; } = new();
    public bool ReturnedMessageIsDraft { get; init; } = true;
    public bool ReturnMessageNotFound { get; init; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      var body = request.Content is null
        ? string.Empty
        : await request.Content.ReadAsStringAsync(cancellationToken);

      Requests.Add(new CapturedRequest(
        request.Method.Method,
        request.RequestUri?.ToString() ?? string.Empty,
        body,
        request.Headers.Authorization?.ToString(),
        request.Headers.TryGetValues("Prefer", out var preferValues)
          ? string.Join(",", preferValues)
          : null));

      if (request.RequestUri?.AbsoluteUri.Contains("/oauth2/v2.0/token", StringComparison.OrdinalIgnoreCase) == true)
      {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
          Content = new StringContent("{\"access_token\":\"test-access-token\"}", Encoding.UTF8, "application/json")
        };
      }

      if (request.Method == HttpMethod.Post &&
          request.RequestUri?.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal) == true)
      {
        return new HttpResponseMessage(HttpStatusCode.Created)
        {
          Content = new StringContent("{\"id\":\"immutable-message-id\",\"isDraft\":true}", Encoding.UTF8, "application/json")
        };
      }

      if (request.Method == HttpMethod.Get &&
          request.RequestUri?.AbsolutePath.Contains("/messages/", StringComparison.Ordinal) == true)
      {
        if (ReturnMessageNotFound)
          return new HttpResponseMessage(HttpStatusCode.NotFound);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
          Content = new StringContent(
            $"{{\"id\":\"immutable-message-id\",\"isDraft\":{ReturnedMessageIsDraft.ToString().ToLowerInvariant()}}}",
            Encoding.UTF8,
            "application/json")
        };
      }

      return new HttpResponseMessage(HttpStatusCode.Accepted)
      {
        Content = new StringContent(string.Empty, Encoding.UTF8, "application/json")
      };
    }
  }

  private sealed class FakeGraphMailClient : IMicrosoftGraphMailClient<GraphMailOptions>
  {
    public string LastEmail { get; private set; } = string.Empty;
    public string LastSubject { get; private set; } = string.Empty;
    public string LastMessage { get; private set; } = string.Empty;
    public MicrosoftGraphMailMessage? LastMail { get; private set; }

    public Task<string> CreateDraftAsync(
      MicrosoftGraphMailMessage mail,
      CancellationToken ct = default)
      => Task.FromResult("immutable-message-id");

    public Task<MicrosoftGraphMailMessageState> GetMessageStateAsync(
      string immutableMessageId,
      CancellationToken ct = default)
      => Task.FromResult(MicrosoftGraphMailMessageState.Draft);

    public Task SendDraftAsync(
      string immutableMessageId,
      CancellationToken ct = default)
      => Task.CompletedTask;

    public Task SendEmailAsync(
      MicrosoftGraphMailMessage mail,
      CancellationToken ct = default)
    {
      LastMail = mail;
      LastEmail = mail.ToRecipients.FirstOrDefault() ?? string.Empty;
      LastSubject = mail.Subject;
      LastMessage = mail.Message;
      return Task.CompletedTask;
    }

    public Task SendEmailAsync(
      string email,
      string subject,
      string message,
      CancellationToken ct = default)
    {
      return SendEmailAsync(
        new MicrosoftGraphMailMessage
        {
          ToRecipients = [email],
          Subject = subject,
          Message = message
        },
        ct);
    }
  }

  private sealed record CapturedRequest(
    string Method,
    string Uri,
    string Body,
    string? AuthorizationHeader,
    string? PreferHeader);
}
