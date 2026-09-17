using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Payments.Clip;
using OrionERP.Infrastructure.Features.Payments.Clip;

namespace OrionERP.UnitTests.Payments;

/// <summary>
/// Clip no usa el mismo esquema de autenticación en todos sus endpoints, y la
/// diferencia sólo se manifiesta como un 401 en producción. Verificado contra la
/// API real el 2026-09-17: <c>GET /refunds/{id}</c> de un id inexistente devuelve
/// 401 con Bearer y 404 "Payment not found" con Basic.
/// </summary>
public sealed class ClipPaymentsClientTests
{
  private const string ApiKey = "live-key-0123456789";
  private const string ApiSecret = "live-secret-9876543210";

  [Fact]
  public async Task RefundPaymentAsync_AuthenticatesWithBasicBecauseBearerGets401()
  {
    var handler = new RecordingHandler(JsonResponse(HttpStatusCode.OK, """
      { "id": "refund-1", "status": "approved", "amount": 35 }
      """));
    var client = CreateClient(handler);

    await client.RefundPaymentAsync(
      new ClipRefundRequest { Amount = 35m, Reason = "Prueba", PaymentId = "pay-1" },
      idempotencyKey: "clave-1");

    var esperado = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ApiKey}:{ApiSecret}"));
    Assert.Equal("/refunds", handler.Requests[0].Uri.AbsolutePath);
    Assert.Equal("Basic", handler.Requests[0].Scheme);
    Assert.Equal(esperado, handler.Requests[0].Parameter);
  }

  [Fact]
  public async Task GetRefundAsync_AlsoUsesBasic()
  {
    var handler = new RecordingHandler(JsonResponse(HttpStatusCode.OK, """
      { "id": "refund-1", "status": "approved", "amount": 35 }
      """));
    var client = CreateClient(handler);

    await client.GetRefundAsync("refund-1");

    Assert.Equal("Basic", handler.Requests[0].Scheme);
  }

  [Fact]
  public async Task PaymentEndpoints_KeepBearerSoTheSecretIsNotNeededToCharge()
  {
    var handler = new RecordingHandler(JsonResponse(HttpStatusCode.OK, """
      { "id": "pay-1", "status": "approved", "amount": 35 }
      """));
    var client = CreateClient(handler);

    await client.GetPaymentAsync("pay-1");

    Assert.Equal("Bearer", handler.Requests[0].Scheme);
    Assert.Equal(ApiKey, handler.Requests[0].Parameter);
  }

  [Fact]
  public async Task RefundPaymentAsync_FailsReadablyWhenTheSecretIsMissing()
  {
    // Sin secreto el Basic saldría malformado y Clip contestaría 401, que es
    // indistinguible de un problema de permisos. Mejor no salir a la red.
    var handler = new RecordingHandler();
    var client = CreateClient(handler, apiSecret: string.Empty);

    var error = await Assert.ThrowsAsync<ClipClientException>(
      () => client.RefundPaymentAsync(
        new ClipRefundRequest { Amount = 35m, Reason = "Prueba", PaymentId = "pay-1" },
        idempotencyKey: "clave-1"));

    Assert.Equal("REFUNDS_NOT_CONFIGURED", error.ProviderErrorCode);
    Assert.Empty(handler.Requests);
  }

  private static ClipPaymentsClient<ClipTestOptions> CreateClient(
    RecordingHandler handler,
    string apiSecret = ApiSecret)
    => new(
      new HttpClient(handler),
      Options.Create(new ClipTestOptions
      {
        Environment = "Live",
        ClipApiKey = ApiKey,
        ClipApiSecret = apiSecret
      }),
      NullLogger<ClipPaymentsClient<ClipTestOptions>>.Instance);

  private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
    => new(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

  public sealed class ClipTestOptions : ClipClientOptions;

  private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
  {
    private readonly Queue<HttpResponseMessage> _responses = new(responses);

    public List<RecordedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
      HttpRequestMessage request,
      CancellationToken cancellationToken)
    {
      Requests.Add(new RecordedRequest(
        request.RequestUri ?? throw new InvalidOperationException("Falta el URI."),
        request.Headers.Authorization?.Scheme,
        request.Headers.Authorization?.Parameter));

      if (_responses.Count == 0)
        throw new InvalidOperationException("No se configuró respuesta para esta petición.");

      await Task.Yield();
      return _responses.Dequeue();
    }
  }

  private sealed record RecordedRequest(Uri Uri, string? Scheme, string? Parameter);
}
