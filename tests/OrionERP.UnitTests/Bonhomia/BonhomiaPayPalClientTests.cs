using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Bonhomia.PublicBooking;
using OrionERP.Infrastructure.Features.Bonhomia.PublicBooking;

namespace OrionERP.UnitTests.Bonhomia;

public class BonhomiaPayPalClientTests
{
  [Fact]
  public async Task CreateOrder_UsesLivePayPalBaseUri_WhenEnvironmentIsLive()
  {
    var handler = new SequencedHttpMessageHandler(
      new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = JsonContent("""
          {
            "access_token": "live-token",
            "expires_in": 3600
          }
          """)
      },
      new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = JsonContent("""
          {
            "id": "LIVE-PAYPAL-ORDER-1",
            "status": "CREATED"
          }
          """)
      });
    var client = CreateClient(
      handler,
      new BonhomiaCheckoutOptions
      {
        Environment = "Live",
        Currency = "MXN",
        PayPalClientId = "live-client-id",
        PayPalClientSecret = "live-client-secret"
      });

    var result = await client.CreateOrderAsync(
      new BonhomiaQuoteDto
      {
        QuoteId = Guid.Parse("b962a5d8-f60f-4e7a-9bb8-8599679df0c2"),
        RoomName = "Suite Paris",
        Fingerprint = "quote-fingerprint",
        Currency = "MXN",
        Total = 1250m
      },
      "ord-live");

    Assert.Equal("LIVE-PAYPAL-ORDER-1", result.OrderId);
    Assert.Equal("CREATED", result.Status);
    Assert.Equal(
      [
        "POST https://api-m.paypal.com/v1/oauth2/token",
        "POST https://api-m.paypal.com/v2/checkout/orders"
      ],
      handler.Requests);
  }

  [Fact]
  public async Task CaptureOrder_WhenPaymentAlreadyDone_LoadsCapturedOrderDetails()
  {
    var handler = new SequencedHttpMessageHandler(
      new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = JsonContent("""
          {
            "access_token": "sandbox-token",
            "expires_in": 3600
          }
          """)
      },
      new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = JsonContent("""
          {
            "id": "PAYPAL-ORDER-1",
            "status": "APPROVED",
            "purchase_units": [
              {
                "reference_id": "quote-1"
              }
            ]
          }
          """)
      },
      new HttpResponseMessage((HttpStatusCode)422)
      {
        Content = JsonContent("""
          {
            "name": "UNPROCESSABLE_ENTITY",
            "details": [
              {
                "issue": "PAYMENT_ALREADY_DONE",
                "description": "Payment already done for this order."
              }
            ]
          }
          """)
      },
      new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = JsonContent("""
          {
            "id": "PAYPAL-ORDER-1",
            "status": "COMPLETED",
            "payment_source": {
              "paypal": {
                "email_address": "buyer@example.com",
                "name": {
                  "given_name": "Buyer",
                  "surname": "Bonhomia"
                },
                "phone_number": {
                  "national_number": "7491234567"
                }
              }
            },
            "purchase_units": [
              {
                "payments": {
                  "captures": [
                    {
                      "id": "CAPTURE-1",
                      "status": "COMPLETED",
                      "amount": {
                        "currency_code": "MXN",
                        "value": "4227.00"
                      }
                    }
                  ]
                }
              }
            ]
          }
          """)
      });
    var client = CreateClient(handler);

    var result = await client.CaptureOrderAsync("PAYPAL-ORDER-1", "cap-quote");

    Assert.Equal("PAYPAL-ORDER-1", result.OrderId);
    Assert.Equal("CAPTURE-1", result.CaptureId);
    Assert.Equal("COMPLETED", result.Status);
    Assert.Equal("MXN", result.Currency);
    Assert.Equal(4227m, result.Amount);
    Assert.Equal("Buyer Bonhomia", result.PayerName);
    Assert.Equal("buyer@example.com", result.PayerEmail);
    Assert.Equal("7491234567", result.PayerPhone);
    Assert.Equal(
      [
        "POST https://api-m.sandbox.paypal.com/v1/oauth2/token",
        "GET https://api-m.sandbox.paypal.com/v2/checkout/orders/PAYPAL-ORDER-1",
        "POST https://api-m.sandbox.paypal.com/v2/checkout/orders/PAYPAL-ORDER-1/capture",
        "GET https://api-m.sandbox.paypal.com/v2/checkout/orders/PAYPAL-ORDER-1"
      ],
      handler.Requests);
  }

  [Fact]
  public async Task CaptureOrder_WhenPayerPhoneIncludesCountryCode_KeepsCountryCode()
  {
    var handler = new SequencedHttpMessageHandler(
      new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = JsonContent("""
          {
            "access_token": "sandbox-token",
            "expires_in": 3600
          }
          """)
      },
      new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = JsonContent("""
          {
            "id": "PAYPAL-ORDER-COUNTRY-CODE",
            "status": "COMPLETED",
            "payment_source": {
              "paypal": {
                "email_address": "buyer@example.com",
                "name": {
                  "given_name": "Buyer",
                  "surname": "Bonhomia"
                },
                "phone_number": {
                  "country_code": "52",
                  "national_number": "7491234567"
                }
              }
            },
            "purchase_units": [
              {
                "payments": {
                  "captures": [
                    {
                      "id": "CAPTURE-COUNTRY-CODE",
                      "status": "COMPLETED",
                      "amount": {
                        "currency_code": "MXN",
                        "value": "4227.00"
                      }
                    }
                  ]
                }
              }
            ]
          }
          """)
      });
    var client = CreateClient(handler);

    var result = await client.CaptureOrderAsync("PAYPAL-ORDER-COUNTRY-CODE", "cap-quote");

    Assert.Equal("+52 7491234567", result.PayerPhone);
  }

  [Fact]
  public async Task CaptureOrder_WhenOrderAlreadyHasCompletedCapture_DoesNotCaptureAgain()
  {
    var handler = new SequencedHttpMessageHandler(
      new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = JsonContent("""
          {
            "access_token": "sandbox-token",
            "expires_in": 3600
          }
          """)
      },
      new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = JsonContent("""
          {
            "id": "PAYPAL-ORDER-2",
            "status": "COMPLETED",
            "payer": {
              "email_address": "guest@example.com",
              "name": {
                "full_name": "Guest Bonhomia"
              },
              "phone": {
                "phone_number": {
                  "national_number": "5550102"
                }
              }
            },
            "purchase_units": [
              {
                "payments": {
                  "captures": [
                    {
                      "id": "CAPTURE-2",
                      "status": "COMPLETED",
                      "amount": {
                        "currency_code": "MXN",
                        "value": "1200.50"
                      }
                    }
                  ]
                }
              }
            ]
          }
          """)
      });
    var client = CreateClient(handler);

    var result = await client.CaptureOrderAsync("PAYPAL-ORDER-2", "cap-quote");

    Assert.Equal("PAYPAL-ORDER-2", result.OrderId);
    Assert.Equal("CAPTURE-2", result.CaptureId);
    Assert.Equal("COMPLETED", result.Status);
    Assert.Equal("MXN", result.Currency);
    Assert.Equal(1200.50m, result.Amount);
    Assert.Equal("Guest Bonhomia", result.PayerName);
    Assert.Equal("guest@example.com", result.PayerEmail);
    Assert.Equal("5550102", result.PayerPhone);
    Assert.Equal(
      [
        "POST https://api-m.sandbox.paypal.com/v1/oauth2/token",
        "GET https://api-m.sandbox.paypal.com/v2/checkout/orders/PAYPAL-ORDER-2"
      ],
      handler.Requests);
  }

  private static BonhomiaPayPalClient CreateClient(HttpMessageHandler handler, BonhomiaCheckoutOptions? options = null)
    => new(
      new HttpClient(handler),
      Options.Create(options ?? new BonhomiaCheckoutOptions
      {
        Environment = "Sandbox",
        Currency = "MXN",
        PayPalClientId = "client-id",
        PayPalClientSecret = "client-secret"
      }),
      NullLogger<BonhomiaPayPalClient>.Instance);

  private static StringContent JsonContent(string json)
    => new(json, Encoding.UTF8, "application/json");

  private sealed class SequencedHttpMessageHandler : HttpMessageHandler
  {
    private readonly Queue<HttpResponseMessage> _responses;

    public SequencedHttpMessageHandler(params HttpResponseMessage[] responses)
    {
      _responses = new Queue<HttpResponseMessage>(responses);
    }

    public List<string> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      Requests.Add($"{request.Method.Method} {request.RequestUri}");

      if (_responses.Count == 0)
      {
        throw new InvalidOperationException("No fake response configured.");
      }

      return Task.FromResult(_responses.Dequeue());
    }
  }
}
