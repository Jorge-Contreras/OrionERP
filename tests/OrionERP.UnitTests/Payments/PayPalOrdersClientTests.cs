using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Payments.PayPal;
using OrionERP.Infrastructure.Features.Payments.PayPal;

namespace OrionERP.UnitTests.Payments;

public sealed class PayPalOrdersClientTests
{
  [Fact]
  public async Task CreateOrderAsync_MapsOptionalPayPalExperienceContext()
  {
    var handler = new SequencedHttpMessageHandler(
      JsonResponse(HttpStatusCode.OK, """
        {
          "access_token": "sandbox-token",
          "expires_in": 3600
        }
        """),
      JsonResponse(HttpStatusCode.Created, """
        {
          "id": "ORDER-CREATE",
          "status": "CREATED"
        }
        """));
    var client = CreateClient(handler);

    await client.CreateOrderAsync(
      new PayPalCreateOrderRequest
      {
        ExperienceContext = new PayPalExperienceContext
        {
          Locale = "es-MX",
          ShippingPreference = "NO_SHIPPING",
          UserAction = "PAY_NOW"
        },
        PurchaseUnits =
        [
          new PayPalPurchaseUnitRequest
          {
            Amount = new PayPalMoney { Currency = "MXN", Value = 125.50m }
          }
        ]
      },
      "create-key");

    var request = Assert.Single(
      handler.Requests,
      candidate => candidate.Uri.AbsolutePath == "/v2/checkout/orders");
    using var document = JsonDocument.Parse(Assert.IsType<string>(request.Body));
    var context = document.RootElement
      .GetProperty("payment_source")
      .GetProperty("paypal")
      .GetProperty("experience_context");
    Assert.Equal("es-MX", context.GetProperty("locale").GetString());
    Assert.Equal("NO_SHIPPING", context.GetProperty("shipping_preference").GetString());
    Assert.Equal("PAY_NOW", context.GetProperty("user_action").GetString());
  }

  [Fact]
  public async Task GetRefundAsync_UsesPaymentsRefundPath_AndMapsRepresentation()
  {
    var handler = new SequencedHttpMessageHandler(
      JsonResponse(HttpStatusCode.OK, """
        {
          "access_token": "sandbox-token",
          "expires_in": 3600
        }
        """),
      JsonResponse(HttpStatusCode.OK, """
        {
          "id": "REFUND-123",
          "status": "COMPLETED",
          "amount": {
            "currency_code": "MXN",
            "value": "125.50"
          },
          "seller_payable_breakdown": {
            "gross_amount": {
              "currency_code": "MXN",
              "value": "125.50"
            },
            "paypal_fee": {
              "currency_code": "MXN",
              "value": "4.50"
            },
            "net_amount": {
              "currency_code": "MXN",
              "value": "121.00"
            },
            "total_refunded_amount": {
              "currency_code": "MXN",
              "value": "125.50"
            }
          },
          "supplementary_data": {
            "related_ids": {
              "capture_id": "CAPTURE-123"
            }
          }
        }
        """));
    var client = CreateClient(handler);

    var result = await client.GetRefundAsync("REFUND-123");

    Assert.Equal("REFUND-123", result.RefundId);
    Assert.Equal("CAPTURE-123", result.CaptureId);
    Assert.Equal("COMPLETED", result.Status);
    Assert.True(result.IsCompleted);
    Assert.Equal(125.50m, result.Amount.Value);
    Assert.Equal("MXN", result.Amount.Currency);
    Assert.NotNull(result.SellerPayableBreakdown);
    Assert.Equal(125.50m, result.SellerPayableBreakdown.GrossAmount.Value);
    Assert.Equal(4.50m, result.SellerPayableBreakdown.PayPalFee.Value);
    Assert.Equal(121.00m, result.SellerPayableBreakdown.NetAmount.Value);
    Assert.Equal(125.50m, result.SellerPayableBreakdown.TotalRefundedAmount.Value);
    Assert.Equal(
      [
        "POST https://api-m.sandbox.paypal.com/v1/oauth2/token",
        "GET https://api-m.sandbox.paypal.com/v2/payments/refunds/REFUND-123"
      ],
      handler.Requests.Select(request => $"{request.Method} {request.Uri}"));
  }

  [Fact]
  public async Task GetRefundAsync_MapsCaptureIdFromUpLink_WhenSupplementaryDataIsAbsent()
  {
    var handler = new SequencedHttpMessageHandler(
      JsonResponse(HttpStatusCode.OK, """
        {
          "access_token": "sandbox-token",
          "expires_in": 3600
        }
        """),
      JsonResponse(HttpStatusCode.OK, """
        {
          "id": "REFUND-123",
          "status": "PENDING",
          "amount": {
            "currency_code": "MXN",
            "value": "25.00"
          },
          "links": [
            {
              "href": "https://api-m.sandbox.paypal.com/v2/payments/captures/CAPTURE-LINK-17",
              "rel": "up",
              "method": "GET"
            }
          ]
        }
        """));
    var client = CreateClient(handler);

    var result = await client.GetRefundAsync("REFUND-123");

    Assert.Equal("CAPTURE-LINK-17", result.CaptureId);
  }

  [Fact]
  public async Task MutationRequests_AskPayPalForRepresentations()
  {
    var handler = new SequencedHttpMessageHandler(
      JsonResponse(HttpStatusCode.OK, """
        {
          "access_token": "sandbox-token",
          "expires_in": 3600
        }
        """),
      JsonResponse(HttpStatusCode.Created, """
        {
          "id": "ORDER-CREATE",
          "status": "CREATED"
        }
        """),
      JsonResponse(HttpStatusCode.Created, """
        {
          "id": "ORDER-CAPTURE",
          "status": "COMPLETED",
          "purchase_units": [
            {
              "payments": {
                "captures": [
                  {
                    "id": "CAPTURE-123",
                    "status": "COMPLETED",
                    "amount": {
                      "currency_code": "MXN",
                      "value": "125.50"
                    }
                  }
                ]
              }
            }
          ]
        }
        """),
      JsonResponse(HttpStatusCode.Created, """
        {
          "id": "REFUND-123",
          "status": "COMPLETED",
          "amount": {
            "currency_code": "MXN",
            "value": "125.50"
          }
        }
        """));
    var client = CreateClient(handler);

    await client.CreateOrderAsync(
      new PayPalCreateOrderRequest
      {
        PurchaseUnits =
        [
          new PayPalPurchaseUnitRequest
          {
            Amount = new PayPalMoney { Currency = "MXN", Value = 125.50m }
          }
        ]
      },
      "create-key");
    await client.CaptureOrderAsync("ORDER-CAPTURE", "capture-key");
    await client.RefundCaptureAsync(
      "CAPTURE-123",
      new PayPalRefundRequest
      {
        Amount = new PayPalMoney { Currency = "MXN", Value = 125.50m }
      },
      "refund-key");

    var mutations = handler.Requests
      .Where(request => request.Uri.AbsolutePath is
        "/v2/checkout/orders" or
        "/v2/checkout/orders/ORDER-CAPTURE/capture" or
        "/v2/payments/captures/CAPTURE-123/refund")
      .ToArray();
    Assert.Equal(3, mutations.Length);
    Assert.All(
      mutations,
      request => Assert.Equal("return=representation", Assert.Single(request.PreferValues)));
  }

  [Fact]
  public async Task ProviderTimeout_IsReportedAsTransientPayPalFailure()
  {
    var handler = new SequencedHttpMessageHandler(
      JsonResponse(HttpStatusCode.OK, """
        {
          "access_token": "sandbox-token",
          "expires_in": 3600
        }
        """),
      new TaskCanceledException("Simulated HttpClient timeout."));
    var client = CreateClient(handler);

    var exception = await Assert.ThrowsAsync<PayPalClientException>(
      () => client.GetRefundAsync("REFUND-TIMEOUT"));

    Assert.Equal("get_refund", exception.Operation);
    Assert.Equal("TIMEOUT", exception.ProviderErrorCode);
    Assert.True(exception.IsTransient);
    Assert.Null(exception.StatusCode);
    Assert.IsType<TaskCanceledException>(exception.InnerException);
  }

  [Fact]
  public async Task ProviderFailure_PreservesEveryIssueCode()
  {
    var handler = new SequencedHttpMessageHandler(
      JsonResponse(HttpStatusCode.OK, """
        {
          "access_token": "sandbox-token",
          "expires_in": 3600
        }
        """),
      JsonResponse(HttpStatusCode.UnprocessableEntity, """
        {
          "name": "UNPROCESSABLE_ENTITY",
          "debug_id": "debug-123",
          "details": [
            {
              "issue": "PAYMENT_ALREADY_DONE",
              "description": "The order was already captured."
            },
            {
              "issue": "ORDER_ALREADY_CAPTURED",
              "description": "A second provider issue."
            }
          ]
        }
        """));
    var client = CreateClient(handler);

    var exception = await Assert.ThrowsAsync<PayPalClientException>(
      () => client.CaptureOrderAsync("ORDER-123", "capture-key"));

    Assert.Equal("capture_order", exception.Operation);
    Assert.Equal("UNPROCESSABLE_ENTITY", exception.ProviderErrorCode);
    Assert.Equal((int)HttpStatusCode.UnprocessableEntity, exception.StatusCode);
    Assert.Equal("debug-123", exception.DebugId);
    Assert.True(exception.IsAlreadyCaptured);
    Assert.False(exception.IsTransient);
    Assert.Equal(
      ["PAYMENT_ALREADY_DONE", "ORDER_ALREADY_CAPTURED"],
      exception.ProviderIssueCodes);
  }

  private static PayPalOrdersClient<PayPalClientOptions> CreateClient(HttpMessageHandler handler)
    => new(
      new HttpClient(handler),
      Options.Create(new PayPalClientOptions
      {
        Environment = "Sandbox",
        PayPalClientId = "client-id",
        PayPalClientSecret = "client-secret",
        PayPalWebhookId = "webhook-id"
      }),
      NullLogger<PayPalOrdersClient<PayPalClientOptions>>.Instance);

  private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
    => new(statusCode)
    {
      Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

  private sealed class SequencedHttpMessageHandler(params object[] responses) : HttpMessageHandler
  {
    private readonly Queue<object> _responses = new(responses);

    public List<RecordedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
      HttpRequestMessage request,
      CancellationToken cancellationToken)
    {
      var body = request.Content is null
        ? null
        : await request.Content.ReadAsStringAsync(cancellationToken);
      Requests.Add(new RecordedRequest(
        request.Method.Method,
        request.RequestUri ?? throw new InvalidOperationException("The request URI is required."),
        request.Headers.TryGetValues("Prefer", out var preferValues)
          ? preferValues.ToArray()
          : [],
        body));

      if (_responses.Count == 0)
        throw new InvalidOperationException("No response was configured for this request.");
      var response = _responses.Dequeue();
      if (response is Exception exception)
        throw exception;

      await Task.Yield();
      return (HttpResponseMessage)response;
    }
  }

  private sealed record RecordedRequest(
    string Method,
    Uri Uri,
    IReadOnlyList<string> PreferValues,
    string? Body);
}
