using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrionERP.Bruno.Web.Configuration;
using OrionERP.Bruno.Web.Services;

namespace OrionERP.UnitTests.Restaurante;

public sealed class BrunoTurnstileServiceTests
{
  [Fact]
  public async Task MissingProductionKeys_FailsClosedWithoutCallingCloudflare()
  {
    using var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP should not be called."));
    using var client = new HttpClient(handler);
    var service = CreateService(client, new BrunoTurnstileOptions(), "Production");

    var result = await service.ValidateAsync("token", "203.0.113.10", "membership-register");

    Assert.False(result);
    Assert.Equal(0, handler.CallCount);
  }

  [Fact]
  public async Task MissingDevelopmentKeys_AllowsLocalFormTesting()
  {
    using var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP should not be called."));
    using var client = new HttpClient(handler);
    var service = CreateService(client, new BrunoTurnstileOptions(), "Development");

    var result = await service.ValidateAsync(null, null, "membership-register");

    Assert.True(result);
    Assert.Equal(0, handler.CallCount);
  }

  [Fact]
  public async Task ValidCloudflareResponse_RequiresExpectedActionAndHostname()
  {
    using var handler = new StubHttpMessageHandler(_ => JsonResponse(
      """
      {"success":true,"hostname":"brunosgarden.com","action":"membership-register","error-codes":[]}
      """));
    using var client = new HttpClient(handler);
    var service = CreateService(client, ConfiguredOptions(), "Production");

    var result = await service.ValidateAsync("token", "203.0.113.10", "membership-register");

    Assert.True(result);
    Assert.Equal(1, handler.CallCount);
  }

  [Theory]
  [InlineData("unexpected-action", "brunosgarden.com")]
  [InlineData("membership-register", "example.com")]
  public async Task UnexpectedActionOrHostname_IsRejected(string action, string hostname)
  {
    using var handler = new StubHttpMessageHandler(_ => JsonResponse(
      $$"""
      {"success":true,"hostname":"{{hostname}}","action":"{{action}}","error-codes":[]}
      """));
    using var client = new HttpClient(handler);
    var service = CreateService(client, ConfiguredOptions(), "Production");

    var result = await service.ValidateAsync("token", null, "membership-register");

    Assert.False(result);
  }

  [Fact]
  public async Task CloudflareErrorResponse_IsRejected()
  {
    using var handler = new StubHttpMessageHandler(_ => JsonResponse(
      """
      {"success":false,"error-codes":["invalid-input-secret"]}
      """));
    using var client = new HttpClient(handler);
    var service = CreateService(client, ConfiguredOptions(), "Production");

    var result = await service.ValidateAsync("token", null, "member-login");

    Assert.False(result);
  }

  private static BrunoTurnstileService CreateService(
    HttpClient client,
    BrunoTurnstileOptions options,
    string environmentName)
    => new(
      client,
      Options.Create(options),
      new FakeWebHostEnvironment { EnvironmentName = environmentName },
      NullLogger<BrunoTurnstileService>.Instance);

  private static BrunoTurnstileOptions ConfiguredOptions() => new()
  {
    SiteKey = "site-key",
    SecretKey = "secret-key",
    ExpectedHostname = "brunosgarden.com"
  };

  private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
  {
    Content = new StringContent(json, Encoding.UTF8, "application/json")
  };

  private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    : HttpMessageHandler
  {
    public int CallCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
      HttpRequestMessage request,
      CancellationToken cancellationToken)
    {
      CallCount++;
      return Task.FromResult(responseFactory(request));
    }
  }

  private sealed class FakeWebHostEnvironment : IWebHostEnvironment
  {
    public string ApplicationName { get; set; } = "OrionERP.Bruno.Web.Tests";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = string.Empty;
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = string.Empty;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
  }
}
