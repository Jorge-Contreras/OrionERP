namespace OrionERP.Application.Features.Payments.PayPal;

public interface IPayPalOrdersClient
{
  Task<PayPalOrderResult> CreateOrderAsync(
    PayPalCreateOrderRequest request,
    string idempotencyKey,
    CancellationToken ct = default);

  Task<PayPalOrderResult> GetOrderAsync(
    string orderId,
    CancellationToken ct = default);

  Task<PayPalCaptureResult> CaptureOrderAsync(
    string orderId,
    string idempotencyKey,
    CancellationToken ct = default);

  Task<PayPalRefundResult> RefundCaptureAsync(
    string captureId,
    PayPalRefundRequest request,
    string idempotencyKey,
    CancellationToken ct = default);

  Task<PayPalRefundResult> GetRefundAsync(
    string refundId,
    CancellationToken ct = default);

  Task<PayPalWebhookVerificationResult> VerifyWebhookSignatureAsync(
    PayPalWebhookSignature signature,
    string webhookEventJson,
    CancellationToken ct = default);
}
