using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Hospitality.PublicBooking;
using OrionERP.Application.Features.Payments.PayPal;

namespace OrionERP.Infrastructure.Features.Hospitality.PublicBooking;

/// <summary>
/// Hospitality policy adapter over the shared PayPal REST transport. This
/// class owns the protected-quote binding and preserves the hospitality public
/// error contract; it does not own PayPal protocol serialization.
/// </summary>
public sealed class HospitalityPayPalClient : IHospitalityPayPalClient
{
  private readonly IPayPalOrdersClient _payPal;
  private readonly HospitalityCheckoutOptions _options;
  private readonly ILogger<HospitalityPayPalClient> _logger;

  public HospitalityPayPalClient(
    IPayPalOrdersClient payPal,
    IOptions<HospitalityCheckoutOptions> options,
    ILogger<HospitalityPayPalClient> logger)
  {
    _payPal = payPal;
    _options = options.Value;
    _logger = logger;
  }

  public async Task<HospitalityPayPalOrderResult> CreateOrderAsync(
    HospitalityQuoteDto quote,
    string idempotencyKey,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(quote);
    EnsureConfigured();

    try
    {
      var result = await _payPal.CreateOrderAsync(
        new PayPalCreateOrderRequest
        {
          Intent = "CAPTURE",
          PurchaseUnits =
          [
            new PayPalPurchaseUnitRequest
            {
              ReferenceId = HospitalityPayPalOrderPolicy.CreateReferenceId(quote),
              Description = $"{_options.PublicName} - {quote.RoomName}",
              CustomId = quote.Fingerprint,
              Amount = new PayPalMoney
              {
                Currency = quote.Currency,
                Value = quote.Total
              }
            }
          ]
        },
        idempotencyKey,
        ct);

      return new HospitalityPayPalOrderResult
      {
        OrderId = result.OrderId,
        Status = result.Status
      };
    }
    catch (PayPalClientException exception)
    {
      LogProviderFailure("create order", exception);
      throw new HospitalityPublicBookingException(
        "paypal_create_failed",
        "PayPal no pudo crear la orden de pago.");
    }
  }

  public async Task<HospitalityPayPalCaptureResult> CaptureOrderAsync(
    string orderId,
    HospitalityQuoteDto quote,
    string idempotencyKey,
    CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(orderId))
    {
      throw new HospitalityPublicBookingException("paypal_order_required", "La orden PayPal es obligatoria.");
    }

    ArgumentNullException.ThrowIfNull(quote);
    EnsureConfigured();

    var verifiedOrder = await GetVerifiedOrderAsync(orderId, quote, ct);
    var existingCapture = FirstCapture(verifiedOrder);
    if (existingCapture is not null)
    {
      var existingResult = MapCapture(existingCapture, verifiedOrder, quote);
      if (!existingResult.IsCompleted)
      {
        var refreshed = await WaitForCompletedCaptureAsync(orderId, quote, ct);
        if (refreshed is not null)
        {
          return refreshed;
        }
      }

      _logger.LogInformation(
        "PayPal order {OrderId} already has capture {CaptureId} with status {CaptureStatus}; using existing capture details.",
        existingResult.OrderId,
        existingResult.CaptureId,
        existingResult.Status);
      return existingResult;
    }

    PayPalCaptureResult capture;
    try
    {
      capture = await _payPal.CaptureOrderAsync(orderId, idempotencyKey, ct);
    }
    catch (PayPalClientException exception) when (exception.IsAlreadyCaptured)
    {
      var recoveredOrder = await GetVerifiedOrderAsync(orderId, quote, ct);
      var recoveredCapture = FirstCapture(recoveredOrder);
      if (recoveredCapture is not null)
      {
        _logger.LogInformation(
          "PayPal order {OrderId} was already captured; using captured order details for reservation recovery.",
          orderId);
        return MapCapture(recoveredCapture, recoveredOrder, quote);
      }

      LogProviderFailure("capture recovery", exception, orderId);
      throw CaptureFailed();
    }
    catch (PayPalClientException exception)
    {
      LogProviderFailure("capture", exception, orderId);
      throw CaptureFailed();
    }

    var result = MapCapture(capture, verifiedOrder, quote);
    if (!result.IsCompleted)
    {
      var refreshed = await WaitForCompletedCaptureAsync(orderId, quote, ct);
      if (refreshed is not null)
      {
        return refreshed;
      }

      _logger.LogWarning(
        "PayPal capture for order {OrderId} returned non-completed status {CaptureStatus}. Reason {Reason}; order status {OrderStatus}.",
        result.OrderId,
        result.Status,
        result.StatusReason,
        result.OrderStatus);
    }

    return result;
  }

  private async Task<PayPalOrderResult> GetVerifiedOrderAsync(
    string orderId,
    HospitalityQuoteDto quote,
    CancellationToken ct)
  {
    PayPalOrderResult order;
    try
    {
      order = await _payPal.GetOrderAsync(orderId, ct);
    }
    catch (PayPalClientException exception)
    {
      LogProviderFailure("order validation", exception, orderId);
      throw new HospitalityPublicBookingException(
        "paypal_order_validation_failed",
        "PayPal no permitio verificar que la orden corresponda a esta cotizacion.");
    }

    EnsureOrderBelongsToQuote(order, quote);
    return order;
  }

  private async Task<HospitalityPayPalCaptureResult?> WaitForCompletedCaptureAsync(
    string orderId,
    HospitalityQuoteDto quote,
    CancellationToken ct)
  {
    for (var attempt = 0; attempt < 2; attempt++)
    {
      await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct);
      var refreshedOrder = await GetVerifiedOrderAsync(orderId, quote, ct);
      var refreshedCapture = FirstCapture(refreshedOrder);
      if (refreshedCapture?.IsCompleted == true)
      {
        return MapCapture(refreshedCapture, refreshedOrder, quote);
      }
    }

    return null;
  }

  private static void EnsureOrderBelongsToQuote(
    PayPalOrderResult order,
    HospitalityQuoteDto quote)
  {
    if (order.PurchaseUnits.Count != 1)
    {
      HospitalityPayPalOrderPolicy.EnsureOrderBelongsToQuote(null, null, quote);
      return;
    }

    var unit = order.PurchaseUnits[0];
    HospitalityPayPalOrderPolicy.EnsureOrderBelongsToQuote(
      unit.CustomId,
      unit.ReferenceId,
      quote);
  }

  private static PayPalCaptureResult? FirstCapture(PayPalOrderResult order)
    => order.Captures.FirstOrDefault();

  private static HospitalityPayPalCaptureResult MapCapture(
    PayPalCaptureResult capture,
    PayPalOrderResult verifiedOrder,
    HospitalityQuoteDto quote)
  {
    var unit = verifiedOrder.PurchaseUnits.Single();
    var payer = HasPayerData(capture.Payer) ? capture.Payer : verifiedOrder.Payer;
    var result = new HospitalityPayPalCaptureResult
    {
      OrderId = string.IsNullOrWhiteSpace(capture.OrderId) ? verifiedOrder.OrderId : capture.OrderId,
      OrderStatus = string.IsNullOrWhiteSpace(capture.OrderStatus) ? verifiedOrder.Status : capture.OrderStatus,
      CaptureId = capture.CaptureId,
      Status = capture.Status,
      StatusReason = capture.StatusReason,
      CustomId = string.IsNullOrWhiteSpace(capture.CustomId) ? unit.CustomId : capture.CustomId,
      ReferenceId = string.IsNullOrWhiteSpace(capture.ReferenceId) ? unit.ReferenceId : capture.ReferenceId,
      Currency = capture.Amount.Currency,
      Amount = capture.Amount.Value,
      PayerName = payer.Name,
      PayerEmail = payer.Email,
      PayerPhone = payer.Phone
    };

    // The order was verified immediately before capture. Capture responses may
    // omit purchase-unit metadata, but may never replace it with mismatched data.
    HospitalityPayPalOrderPolicy.EnsureCaptureBelongsToQuote(result, quote);
    result.CustomId = quote.Fingerprint;
    result.ReferenceId = HospitalityPayPalOrderPolicy.CreateReferenceId(quote);
    return result;
  }

  private static bool HasPayerData(PayPalPayer payer)
    => !string.IsNullOrWhiteSpace(payer.Name)
      || !string.IsNullOrWhiteSpace(payer.Email)
      || !string.IsNullOrWhiteSpace(payer.Phone);

  private void EnsureConfigured()
  {
    if (!_options.IsPayPalConfigured)
    {
      throw new HospitalityPublicBookingException(
        "paypal_not_configured",
        "PayPal todavia no esta configurado para recibir pagos.");
    }
  }

  private void LogProviderFailure(
    string operation,
    PayPalClientException exception,
    string? orderId = null)
    => _logger.LogWarning(
      "PayPal hospitality {Operation} failed for order {OrderId}. Provider code {ProviderCode}; HTTP status {StatusCode}; debug id {DebugId}.",
      operation,
      string.IsNullOrWhiteSpace(orderId) ? "<none>" : orderId,
      exception.ProviderErrorCode,
      exception.StatusCode,
      string.IsNullOrWhiteSpace(exception.DebugId) ? "<none>" : exception.DebugId);

  private static HospitalityPublicBookingException CaptureFailed()
    => new("paypal_capture_failed", "PayPal no pudo confirmar el pago.");
}
