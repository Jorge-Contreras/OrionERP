using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrionERP.Application.Features.Platform;
using OrionERP.Application.Features.Restaurante;
using OrionERP.Bruno.Web.Features.Ordering;

namespace OrionERP.IntegrationTests.Restaurante;

public sealed class BrunoCheckoutApiTests
{
  private static readonly Guid ActiveMemberId = Guid.Parse("6A7B9D53-58AE-4A4D-9CAA-0D81993B5F4D");
  private static readonly Guid ForgedMemberId = Guid.Parse("BBE4B6DB-CEAA-48C1-9760-C39802840156");

  [Fact]
  public async Task Routes_are_anonymous_webhook_opts_out_and_browser_posts_enforce_antiforgery()
  {
    var checkout = new FakeCheckoutService();
    await using var app = await CreateAppAsync(checkout);
    var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
      .OfType<RouteEndpoint>()
      .ToArray();

    AssertAnonymousRoute(endpoints, "/api/restaurant/checkout/quote");
    AssertAnonymousRoute(endpoints, "/api/restaurant/checkout/paypal-orders");
    AssertAnonymousRoute(endpoints, "/api/restaurant/checkout/paypal-orders/{orderId}/capture");
    AssertAnonymousRoute(endpoints, "/api/restaurant/checkout/status/{trackingToken}");
    var webhook = AssertAnonymousRoute(endpoints, "/api/restaurant/checkout/paypal-webhook");
    var webhookAntiforgery = Assert.IsAssignableFrom<IAntiforgeryMetadata>(
      webhook.Metadata.GetMetadata<IAntiforgeryMetadata>());
    Assert.False(webhookAntiforgery.RequiresValidation);
    var webhookSizeLimit = Assert.IsAssignableFrom<IRequestSizeLimitMetadata>(
      webhook.Metadata.GetMetadata<IRequestSizeLimitMetadata>());
    Assert.Equal(1_048_576, webhookSizeLimit.MaxRequestBodySize);
    foreach (var route in new[]
    {
      "/api/restaurant/checkout/quote",
      "/api/restaurant/checkout/paypal-orders",
      "/api/restaurant/checkout/paypal-orders/{orderId}/capture"
    })
    {
      var browserPost = endpoints.Single(endpoint =>
        string.Equals(endpoint.RoutePattern.RawText, route, StringComparison.Ordinal));
      var browserPostSizeLimit = Assert.IsAssignableFrom<IRequestSizeLimitMetadata>(
        browserPost.Metadata.GetMetadata<IRequestSizeLimitMetadata>());
      Assert.Equal(262_144, browserPostSizeLimit.MaxRequestBodySize);
    }

    var response = await app.GetTestClient().PostAsJsonAsync(
      "/api/restaurant/checkout/quote",
      QuoteRequest());
    var problem = await response.Content.ReadAsStringAsync();

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.Contains("invalid_antiforgery", problem, StringComparison.Ordinal);
    Assert.Equal(0, checkout.QuoteCallCount);
  }

  [Fact]
  public async Task Guest_quote_ignores_forged_tenant_and_member_fields()
  {
    var checkout = new FakeCheckoutService();
    await using var app = await CreateAppAsync(checkout);
    using var client = await CreateAntiforgeryClientAsync(app);

    var response = await client.PostAsJsonAsync("/api/restaurant/checkout/quote", new
    {
      rfc = "OHM191112Q26",
      publicSiteId = 9999,
      memberId = ForgedMemberId,
      lines = QuoteRequest().Lines
    });

    response.EnsureSuccessStatusCode();
    Assert.Null(checkout.LastMemberId);
    Assert.Equal(FakeWebsiteContext.Binding, checkout.LastBinding);
  }

  [Fact]
  public async Task Authenticated_quote_binds_the_active_member_from_server_identity_not_payload()
  {
    var checkout = new FakeCheckoutService();
    var membership = new FakeMembershipService
    {
      Profile = new LoyaltyMemberProfileDto
      {
        Id = ActiveMemberId,
        Status = LoyaltyMemberStatuses.Active
      }
    };
    await using var app = await CreateAppAsync(checkout, membership);
    using var client = await CreateAntiforgeryClientAsync(app, "identity-user-17");

    var response = await client.PostAsJsonAsync("/api/restaurant/checkout/quote", new
    {
      memberId = ForgedMemberId,
      lines = QuoteRequest().Lines
    });

    response.EnsureSuccessStatusCode();
    Assert.Equal(ActiveMemberId, checkout.LastMemberId);
    Assert.Equal("identity-user-17", membership.LastIdentityUserId);
    Assert.Equal(FakeWebsiteContext.Binding.CompanyRfc, membership.LastRfc);
    Assert.Equal(FakeWebsiteContext.Binding.PublicSiteId, membership.LastPublicSiteId);
  }

  [Fact]
  public async Task Duplicate_create_preserves_the_existing_checkout_response_surface()
  {
    var checkout = new FakeCheckoutService
    {
      CreateResult = new RestaurantOnlinePayPalOrderResult
      {
        Succeeded = true,
        Code = "paypal_order_exists",
        Message = "La orden de pago ya existe.",
        PayPalOrderId = "PAYPAL-EXISTING",
        TrackingToken = "TRACK-EXISTING",
        CheckoutStatus = RestaurantOnlineCheckoutStatuses.PayPalCreated,
        WasExisting = true
      }
    };
    await using var app = await CreateAppAsync(checkout);
    using var client = await CreateAntiforgeryClientAsync(app);

    var response = await client.PostAsJsonAsync(
      "/api/restaurant/checkout/paypal-orders",
      CreateOrderRequest());
    var result = await response.Content.ReadFromJsonAsync<RestaurantOnlinePayPalOrderResult>();

    response.EnsureSuccessStatusCode();
    Assert.NotNull(result);
    Assert.True(result.WasExisting);
    Assert.Equal("PAYPAL-EXISTING", result.PayPalOrderId);
    Assert.Equal("TRACK-EXISTING", result.TrackingToken);
    Assert.Equal(RestaurantOnlineCheckoutStatuses.PayPalCreated, result.CheckoutStatus);
  }

  [Theory]
  [InlineData(RestaurantOnlineCheckoutStatuses.CapturePending, "capture_pending", false, true)]
  [InlineData(RestaurantOnlineCheckoutStatuses.PaymentDenied, "payment_denied", false, false)]
  [InlineData(RestaurantOnlineCheckoutStatuses.Refunded, "refunded", true, false)]
  public async Task Capture_pending_denied_and_refunded_states_remain_successful_durable_responses(
    string checkoutStatus,
    string code,
    bool paymentCaptured,
    bool isPending)
  {
    var checkout = new FakeCheckoutService
    {
      CaptureResult = new RestaurantOnlinePayPalCaptureResult
      {
        Succeeded = true,
        Code = code,
        Message = "Estado durable de prueba.",
        CheckoutStatus = checkoutStatus,
        TrackingToken = "TRACK-17",
        PaymentCaptured = paymentCaptured,
        IsPending = isPending
      }
    };
    await using var app = await CreateAppAsync(checkout);
    using var client = await CreateAntiforgeryClientAsync(app);

    var response = await client.PostAsJsonAsync(
      "/api/restaurant/checkout/paypal-orders/PAYPAL-17/capture",
      CaptureRequest());
    var result = await response.Content.ReadFromJsonAsync<RestaurantOnlinePayPalCaptureResult>();

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.NotNull(result);
    Assert.Equal(code, result.Code);
    Assert.Equal(checkoutStatus, result.CheckoutStatus);
    Assert.Equal(paymentCaptured, result.PaymentCaptured);
    Assert.Equal(isPending, result.IsPending);
  }

  [Fact]
  public async Task Valid_foreign_paypal_webhook_is_acknowledged_without_a_checkout_match()
  {
    var checkout = new FakeCheckoutService
    {
      WebhookResult = new RestaurantPayPalWebhookResult(true, false, "WH-FOREIGN")
    };
    await using var app = await CreateAppAsync(checkout);
    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/restaurant/checkout/paypal-webhook")
    {
      Content = new StringContent("{\"id\":\"WH-FOREIGN\",\"event_type\":\"PAYMENT.CAPTURE.COMPLETED\"}", Encoding.UTF8, "application/json")
    };
    AddWebhookHeaders(request);

    var response = await app.GetTestClient().SendAsync(request);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.NotNull(checkout.LastWebhookRequest);
    Assert.Contains("WH-FOREIGN", checkout.LastWebhookRequest.RawBody, StringComparison.Ordinal);
    Assert.Equal("transmission-17", checkout.LastWebhookRequest.TransmissionId);
    Assert.False(checkout.WebhookResult.Matched);
  }

  [Fact]
  public async Task Webhook_missing_signature_header_is_rejected_before_checkout_service()
  {
    var checkout = new FakeCheckoutService();
    await using var app = await CreateAppAsync(checkout);
    using var request = WebhookRequest(
      new StringContent("{\"id\":\"WH-17\",\"event_type\":\"PAYMENT.CAPTURE.COMPLETED\"}", Encoding.UTF8, "application/json"),
      omittedHeader: "PAYPAL-TRANSMISSION-SIG");

    var response = await app.GetTestClient().SendAsync(request);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.Equal(0, checkout.WebhookCallCount);
  }

  [Theory]
  [InlineData("PAYPAL-TRANSMISSION-ID", 257)]
  [InlineData("PAYPAL-TRANSMISSION-TIME", 65)]
  [InlineData("PAYPAL-CERT-URL", 2_049)]
  [InlineData("PAYPAL-AUTH-ALGO", 129)]
  [InlineData("PAYPAL-TRANSMISSION-SIG", 8_193)]
  public async Task Oversized_webhook_signature_header_is_rejected_before_checkout_service(
    string headerName,
    int length)
  {
    var checkout = new FakeCheckoutService();
    await using var app = await CreateAppAsync(checkout);
    using var request = WebhookRequest(
      new StringContent("{\"id\":\"WH-17\",\"event_type\":\"PAYMENT.CAPTURE.COMPLETED\"}", Encoding.UTF8, "application/json"));
    request.Headers.Remove(headerName);
    Assert.True(request.Headers.TryAddWithoutValidation(headerName, new string('x', length)));

    var response = await app.GetTestClient().SendAsync(request);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.Equal(0, checkout.WebhookCallCount);
  }

  [Theory]
  [InlineData("{")]
  [InlineData("[]")]
  [InlineData("{\"id\":17,\"event_type\":\"PAYMENT.CAPTURE.COMPLETED\"}")]
  [InlineData("{\"id\":\"WH-17\"}")]
  [InlineData("{\"id\":\"WH-17\",\"event_type\":17}")]
  public async Task Malformed_webhook_shape_is_rejected_before_checkout_service(string payload)
  {
    var checkout = new FakeCheckoutService();
    await using var app = await CreateAppAsync(checkout);
    using var request = WebhookRequest(new StringContent(payload, Encoding.UTF8, "application/json"));

    var response = await app.GetTestClient().SendAsync(request);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.Equal(0, checkout.WebhookCallCount);
  }

  [Theory]
  [InlineData("id")]
  [InlineData("event_type")]
  public async Task Oversized_webhook_identity_is_rejected_before_checkout_service(string propertyName)
  {
    var checkout = new FakeCheckoutService();
    await using var app = await CreateAppAsync(checkout);
    var payload = JsonSerializer.Serialize(new Dictionary<string, string>
    {
      ["id"] = propertyName == "id" ? new string('I', 101) : "WH-17",
      ["event_type"] = propertyName == "event_type" ? new string('E', 101) : "PAYMENT.CAPTURE.COMPLETED"
    });
    using var request = WebhookRequest(new StringContent(payload, Encoding.UTF8, "application/json"));

    var response = await app.GetTestClient().SendAsync(request);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.Equal(0, checkout.WebhookCallCount);
  }

  [Fact]
  public async Task Invalid_utf8_webhook_is_rejected_before_checkout_service()
  {
    var checkout = new FakeCheckoutService();
    await using var app = await CreateAppAsync(checkout);
    using var request = WebhookRequest(new ByteArrayContent([0xC3, 0x28]));

    var response = await app.GetTestClient().SendAsync(request);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.Equal(0, checkout.WebhookCallCount);
  }

  [Fact]
  public async Task Chunked_webhook_over_byte_limit_is_rejected_without_full_service_processing()
  {
    var checkout = new FakeCheckoutService();
    await using var app = await CreateAppAsync(checkout);
    using var request = WebhookRequest(new UnknownLengthByteContent(new byte[1_048_577]));

    var response = await app.GetTestClient().SendAsync(request);

    Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    Assert.Equal(0, checkout.WebhookCallCount);
  }

  private static async Task<WebApplication> CreateAppAsync(
    FakeCheckoutService checkout,
    FakeMembershipService? membership = null)
  {
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
      EnvironmentName = Environments.Development
    });
    builder.WebHost.UseTestServer();
    builder.Services.AddRouting();
    builder.Services.AddDataProtection();
    builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
    builder.Services.AddSingleton<IPublicWebsiteInstanceContext, FakeWebsiteContext>();
    builder.Services.AddSingleton<IRestaurantMembershipService>(membership ?? new FakeMembershipService());
    builder.Services.AddSingleton<IOnlineRestaurantCheckoutService>(checkout);

    var app = builder.Build();
    app.Use(async (context, next) =>
    {
      if (context.Request.Headers.TryGetValue("X-Test-Identity", out var identityUserId)
          && !string.IsNullOrWhiteSpace(identityUserId))
      {
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
          [new Claim(ClaimTypes.NameIdentifier, identityUserId.ToString())],
          "IntegrationTest"));
      }
      await next(context);
    });
    app.MapGet("/__test/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
      Results.Text(antiforgery.GetAndStoreTokens(context).RequestToken ?? string.Empty));
    app.MapRestaurantCheckoutApi();
    await app.StartAsync();
    return app;
  }

  private static HttpRequestMessage WebhookRequest(HttpContent content, string? omittedHeader = null)
  {
    var request = new HttpRequestMessage(HttpMethod.Post, "/api/restaurant/checkout/paypal-webhook")
    {
      Content = content
    };
    AddWebhookHeaders(request, omittedHeader);
    return request;
  }

  private static void AddWebhookHeaders(HttpRequestMessage request, string? omittedHeader = null)
  {
    var headers = new Dictionary<string, string>
    {
      ["PAYPAL-TRANSMISSION-ID"] = "transmission-17",
      ["PAYPAL-TRANSMISSION-TIME"] = "2026-09-14T18:00:00Z",
      ["PAYPAL-CERT-URL"] = "https://api.paypal.com/cert.pem",
      ["PAYPAL-AUTH-ALGO"] = "SHA256withRSA",
      ["PAYPAL-TRANSMISSION-SIG"] = "verified-signature"
    };
    foreach (var (name, value) in headers)
    {
      if (!string.Equals(name, omittedHeader, StringComparison.Ordinal))
        request.Headers.TryAddWithoutValidation(name, value);
    }
  }

  private static async Task<HttpClient> CreateAntiforgeryClientAsync(
    WebApplication app,
    string? identityUserId = null)
  {
    var client = app.GetTestClient();
    if (!string.IsNullOrWhiteSpace(identityUserId))
      client.DefaultRequestHeaders.TryAddWithoutValidation("X-Test-Identity", identityUserId);
    var tokenResponse = await client.GetAsync("/__test/antiforgery");
    tokenResponse.EnsureSuccessStatusCode();
    var token = await tokenResponse.Content.ReadAsStringAsync();
    var cookies = tokenResponse.Headers.GetValues("Set-Cookie")
      .Select(value => value.Split(';', 2)[0]);
    client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
    client.DefaultRequestHeaders.TryAddWithoutValidation("X-CSRF-TOKEN", token);
    return client;
  }

  private static RouteEndpoint AssertAnonymousRoute(
    IReadOnlyCollection<RouteEndpoint> endpoints,
    string routePattern)
  {
    var endpoint = Assert.Single(endpoints, item => item.RoutePattern.RawText == routePattern);
    Assert.NotNull(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
    return endpoint;
  }

  private static RestaurantOnlineQuoteRequest QuoteRequest()
    => new()
    {
      Lines =
      [
        new RestaurantOnlineCartLineRequest
        {
          ProductId = 10,
          MenuSectionId = 3,
          Quantity = 1
        }
      ]
    };

  private static RestaurantOnlinePayPalOrderCreateRequest CreateOrderRequest()
    => new()
    {
      QuoteToken = "QUOTE-17",
      ClientAttemptId = Guid.Parse("2943A3F8-64C4-445B-9B81-AF20B47DF279"),
      CustomerName = "Cliente Bruno",
      CustomerEmail = "cliente@example.test",
      CustomerPhone = "7491103026",
      TermsAccepted = true,
      TermsVersion = "2026-09-14",
      PrivacyVersion = "2026-09-14"
    };

  private static RestaurantOnlinePayPalCaptureRequest CaptureRequest()
    => new()
    {
      QuoteToken = "QUOTE-17",
      ClientAttemptId = Guid.Parse("2943A3F8-64C4-445B-9B81-AF20B47DF279"),
      TrackingToken = "TRACK-17"
    };

  private sealed class FakeWebsiteContext : IPublicWebsiteInstanceContext
  {
    internal static readonly PublicSiteBinding Binding = new(
      21,
      "brunos-main",
      7,
      "BRUNOS260707L26",
      "BRUNOS260707L26",
      null,
      17,
      "brunos-01",
      "Bruno's Garden",
      TimeZoneInfo.Utc.Id,
      PlatformModuleCodes.Restaurant,
      1,
      "brunosgarden.com",
      1,
      1,
      1);

    public PublicWebsiteInstanceDefinition Instance { get; } = new(
      "brunos-main",
      "BRUNOS260707L26",
      "brunos-01",
      PlatformModuleCodes.Restaurant,
      "brunosgarden.com",
      5017);

    public string CurrentRfc => Binding.CompanyRfc;

    public Task<PublicSiteBinding> ResolveRequiredAsync(CancellationToken ct = default)
      => Task.FromResult(Binding);
  }

  private sealed class FakeMembershipService : IRestaurantMembershipService
  {
    public LoyaltyMemberProfileDto? Profile { get; init; }
    public string? LastRfc { get; private set; }
    public long? LastPublicSiteId { get; private set; }
    public string? LastIdentityUserId { get; private set; }

    public Task<LoyaltyMemberProfileDto?> GetMemberProfileByIdentityAsync(
      string rfc,
      long publicSiteId,
      string identityUserId,
      CancellationToken ct = default)
    {
      LastRfc = rfc;
      LastPublicSiteId = publicSiteId;
      LastIdentityUserId = identityUserId;
      return Task.FromResult(Profile);
    }

    public Task<LoyaltyQrTokenDto> CreateQrTokenAsync(
      string rfc,
      long publicSiteId,
      Guid memberId,
      CancellationToken ct = default)
      => Task.FromResult(new LoyaltyQrTokenDto());

    public Task<RestaurantCommandResult> UpdateConsentsAsync(
      LoyaltyConsentUpdateRequest request,
      CancellationToken ct = default)
      => Task.FromResult(RestaurantCommandResult.Ok("updated"));

    public Task<RestaurantCommandResult> RequestClosureAsync(
      LoyaltyClosureRequest request,
      CancellationToken ct = default)
      => Task.FromResult(RestaurantCommandResult.Ok("closed"));
  }

  private sealed class FakeCheckoutService : IOnlineRestaurantCheckoutService
  {
    public int QuoteCallCount { get; private set; }
    public Guid? LastMemberId { get; private set; }
    public PublicSiteBinding? LastBinding { get; private set; }
    public RestaurantPayPalWebhookRequest? LastWebhookRequest { get; private set; }
    public int WebhookCallCount { get; private set; }

    public RestaurantOnlinePayPalOrderResult CreateResult { get; init; } = new()
    {
      Succeeded = true,
      Code = "paypal_order_created",
      PayPalOrderId = "PAYPAL-17",
      TrackingToken = "TRACK-17",
      CheckoutStatus = RestaurantOnlineCheckoutStatuses.PayPalCreated
    };

    public RestaurantOnlinePayPalCaptureResult CaptureResult { get; init; } = new()
    {
      Succeeded = true,
      Code = "payment_received",
      CheckoutStatus = RestaurantOnlineCheckoutStatuses.Captured,
      TrackingToken = "TRACK-17",
      PaymentCaptured = true,
      IsPending = true
    };

    public RestaurantPayPalWebhookResult WebhookResult { get; init; } = new(true, true, "WH-17");

    public Task<RestaurantOnlineOrderingConfigurationDto> GetConfigurationAsync(
      PublicSiteBinding binding,
      CancellationToken ct = default)
      => Task.FromResult(new RestaurantOnlineOrderingConfigurationDto());

    public Task<RestaurantOnlineQuoteResult> QuoteAsync(
      PublicSiteBinding binding,
      RestaurantOnlineQuoteRequest request,
      Guid? memberId,
      CancellationToken ct = default)
    {
      QuoteCallCount++;
      Record(binding, memberId);
      return Task.FromResult(new RestaurantOnlineQuoteResult
      {
        Succeeded = true,
        Code = "quoted",
        QuoteToken = "QUOTE-17",
        Fingerprint = "FINGERPRINT-17",
        Total = 125m
      });
    }

    public Task<RestaurantOnlinePayPalOrderResult> CreatePayPalOrderAsync(
      PublicSiteBinding binding,
      RestaurantOnlinePayPalOrderCreateRequest request,
      Guid? memberId,
      CancellationToken ct = default)
    {
      Record(binding, memberId);
      return Task.FromResult(CreateResult);
    }

    public Task<RestaurantOnlinePayPalCaptureResult> CapturePayPalOrderAsync(
      PublicSiteBinding binding,
      string payPalOrderId,
      RestaurantOnlinePayPalCaptureRequest request,
      Guid? memberId,
      CancellationToken ct = default)
    {
      Record(binding, memberId);
      return Task.FromResult(CaptureResult);
    }

    public Task<RestaurantOnlineCheckoutStatusDto?> GetStatusAsync(
      PublicSiteBinding binding,
      string trackingToken,
      CancellationToken ct = default)
      => Task.FromResult<RestaurantOnlineCheckoutStatusDto?>(new RestaurantOnlineCheckoutStatusDto
      {
        CheckoutStatus = RestaurantOnlineCheckoutStatuses.Captured
      });

    public Task<RestaurantPayPalWebhookResult> ProcessPayPalWebhookAsync(
      PublicSiteBinding binding,
      RestaurantPayPalWebhookRequest request,
      CancellationToken ct = default)
    {
      LastBinding = binding;
      LastWebhookRequest = request;
      WebhookCallCount++;
      return Task.FromResult(WebhookResult);
    }

    private void Record(PublicSiteBinding binding, Guid? memberId)
    {
      LastBinding = binding;
      LastMemberId = memberId;
    }
  }

  private sealed class UnknownLengthByteContent(byte[] content) : HttpContent
  {
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
      => stream.WriteAsync(content).AsTask();

    protected override bool TryComputeLength(out long length)
    {
      length = 0;
      return false;
    }
  }
}
