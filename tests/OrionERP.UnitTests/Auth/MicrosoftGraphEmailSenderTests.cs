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

  private sealed class RecordingHttpMessageHandler : HttpMessageHandler
  {
    public List<CapturedRequest> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      var body = request.Content is null
        ? string.Empty
        : await request.Content.ReadAsStringAsync(cancellationToken);

      Requests.Add(new CapturedRequest(
        request.Method.Method,
        request.RequestUri?.ToString() ?? string.Empty,
        body,
        request.Headers.Authorization?.ToString()));

      if (request.RequestUri?.AbsoluteUri.Contains("/oauth2/v2.0/token", StringComparison.OrdinalIgnoreCase) == true)
      {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
          Content = new StringContent("{\"access_token\":\"test-access-token\"}", Encoding.UTF8, "application/json")
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

    public Task SendEmailAsync(
      string email,
      string subject,
      string message,
      CancellationToken ct = default)
    {
      LastEmail = email;
      LastSubject = subject;
      LastMessage = message;
      return Task.CompletedTask;
    }
  }

  private sealed record CapturedRequest(
    string Method,
    string Uri,
    string Body,
    string? AuthorizationHeader);
}
