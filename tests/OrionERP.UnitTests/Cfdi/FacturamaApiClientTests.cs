using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Configuration;
using OrionERP.Infrastructure.Features.Cfdi.Facturama;

namespace OrionERP.UnitTests.Cfdi;

public class FacturamaApiClientTests
{
  [Fact]
  public async Task CreateIssuedCfdiAsync_UsesSandboxDefaults_InDevelopment()
  {
    var handler = new RecordingHttpMessageHandler();
    var client = CreateClient(
        handler,
        new Dictionary<string, string?>
        {
          ["ENVIRONMENT"] = "Development"
        });

    var id = await client.CreateIssuedCfdiAsync("""{"test":true}""");

    Assert.Equal("sandbox-id", id);
    Assert.Equal(new Uri("https://apisandbox.facturama.mx/3/cfdis"), handler.LastRequest?.RequestUri);
    Assert.Equal(CreateBasicAuthHeader("jorgecontreras", "Orion2020"), handler.LastRequest?.Headers.Authorization);
  }

  [Fact]
  public async Task CreateIssuedCfdiAsync_UsesSandboxCredentials_WhenSandboxBaseUrlIsExplicitlyConfigured()
  {
    var handler = new RecordingHttpMessageHandler();
    var client = CreateClient(
        handler,
        new Dictionary<string, string?>
        {
          ["Facturama:BaseUrl"] = "https://apisandbox.facturama.mx"
        });

    var id = await client.CreateIssuedCfdiAsync("""{"test":true}""");

    Assert.Equal("sandbox-id", id);
    Assert.Equal(new Uri("https://apisandbox.facturama.mx/3/cfdis"), handler.LastRequest?.RequestUri);
    Assert.Equal(CreateBasicAuthHeader("jorgecontreras", "Orion2020"), handler.LastRequest?.Headers.Authorization);
  }

  [Fact]
  public async Task CreateIssuedCfdiAsync_HonorsExplicitCredentials_WhenProvided()
  {
    var handler = new RecordingHttpMessageHandler();
    var client = CreateClient(
        handler,
        new Dictionary<string, string?>
        {
          ["Facturama:BaseUrl"] = "https://apisandbox.facturama.mx",
          ["Facturama:User"] = "custom-user",
          ["Facturama:Password"] = "custom-password"
        });

    var id = await client.CreateIssuedCfdiAsync("""{"test":true}""");

    Assert.Equal("sandbox-id", id);
    Assert.Equal(new Uri("https://apisandbox.facturama.mx/3/cfdis"), handler.LastRequest?.RequestUri);
    Assert.Equal(CreateBasicAuthHeader("custom-user", "custom-password"), handler.LastRequest?.Headers.Authorization);
  }

  [Fact]
  public async Task CreateIssuedCfdiAsync_UsesProductionDefaults_WhenNoSandboxSignalExists()
  {
    var handler = new RecordingHttpMessageHandler();
    var client = CreateClient(
        handler,
        new Dictionary<string, string?>
        {
          ["ConnectionStrings:OrionDb"] = "Server=127.0.0.1,1433;Database=grupocarpio;User Id=sa;Password=secret;TrustServerCertificate=True;Encrypt=True;",
          ["Facturama:User"] = "prod-user",
          ["Facturama:Password"] = "prod-password"
        });

    var id = await client.CreateIssuedCfdiAsync("""{"test":true}""");

    Assert.Equal("sandbox-id", id);
    Assert.Equal(new Uri("https://api.facturama.mx/3/cfdis"), handler.LastRequest?.RequestUri);
    Assert.Equal(CreateBasicAuthHeader("prod-user", "prod-password"), handler.LastRequest?.Headers.Authorization);
  }

  [Fact]
  public async Task CreateIssuedCfdiAsync_DoesNotInferSandbox_FromSandboxNamedDatabaseAlone()
  {
    var handler = new RecordingHttpMessageHandler();
    var ex = Assert.Throws<InvalidOperationException>(() => CreateClient(
        handler,
        new Dictionary<string, string?>
        {
          ["ConnectionStrings:OrionDb"] = "Server=127.0.0.1,1433;Database=Orion_SandBox;User Id=sa;Password=secret;TrustServerCertificate=True;Encrypt=True;"
        }));

    Assert.Contains("Facturama:User", ex.Message, StringComparison.Ordinal);
    Assert.Contains("Facturama:Password", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CreateIssuedCfdiAsync_ThrowsHelpfulMessage_WhenProductionReturnsUnauthorized()
  {
    var handler = new RecordingHttpMessageHandler(HttpStatusCode.Unauthorized, string.Empty);
    var client = CreateClient(
        handler,
        new Dictionary<string, string?>
        {
          ["Facturama:User"] = "prod-user",
          ["Facturama:Password"] = "prod-password"
        });

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateIssuedCfdiAsync("""{"test":true}"""));

    Assert.Contains("devolvió 401", ex.Message, StringComparison.Ordinal);
    Assert.Contains("Facturama:User", ex.Message, StringComparison.Ordinal);
    Assert.Contains("Facturama:Password", ex.Message, StringComparison.Ordinal);
  }

  private static FacturamaApiClient CreateClient(HttpMessageHandler handler, IDictionary<string, string?> values)
  {
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(values)
        .Build();

    return new FacturamaApiClient(new HttpClient(handler), configuration);
  }

  private static AuthenticationHeaderValue CreateBasicAuthHeader(string user, string password)
  {
    var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{user}:{password}"));
    return new AuthenticationHeaderValue("Basic", token);
  }

  private sealed class RecordingHttpMessageHandler : HttpMessageHandler
  {
    private readonly HttpStatusCode _statusCode;
    private readonly string _body;

    public RecordingHttpMessageHandler(HttpStatusCode statusCode = HttpStatusCode.Created, string body = """{"Id":"sandbox-id"}""")
    {
      _statusCode = statusCode;
      _body = body;
    }

    public HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      LastRequest = request;

      return Task.FromResult(new HttpResponseMessage(_statusCode)
      {
        Content = new StringContent(_body, Encoding.UTF8, "application/json")
      });
    }
  }
}
