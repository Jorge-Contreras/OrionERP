using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Bonhomia.PublicBooking;

namespace OrionERP.Web.Features.Bonhomia.Checkout;

public static class BonhomiaCheckoutApi
{
  public static IEndpointRouteBuilder MapBonhomiaCheckoutApi(this IEndpointRouteBuilder endpoints)
  {
    endpoints.MapPost("/api/bonhomia/checkout/orders", CreatePayPalOrderAsync).AllowAnonymous();
    endpoints.MapPost("/api/bonhomia/checkout/orders/{orderId}", ConfirmPayPalOrderAsync).AllowAnonymous();
    return endpoints;
  }

  private static async Task<IResult> CreatePayPalOrderAsync(
    BonhomiaCreatePayPalOrderRequest request,
    IBonhomiaQuoteTokenService quoteTokenService,
    IBonhomiaPublicBookingService bookingService,
    IBonhomiaPayPalClient payPalClient,
    CancellationToken ct)
  {
    if (!TryReadQuote(request.QuoteToken, request.QuoteFingerprint, quoteTokenService, out var quote, out var errorResult))
    {
      return errorResult!;
    }

    try
    {
      var liveQuote = await bookingService.CreateQuoteAsync(quote!.Request, ct);
      if (!string.Equals(liveQuote.Fingerprint, quote.Fingerprint, StringComparison.Ordinal))
      {
        return Results.Problem(
          title: "Cotizacion actualizada",
          detail: "Las fechas, extras o precios cambiaron. Vuelve a generar la cotizacion.",
          statusCode: StatusCodes.Status409Conflict);
      }

      var order = await payPalClient.CreateOrderAsync(liveQuote, BuildPayPalRequestId("ord", request.PaymentAttemptId, quote.QuoteId), ct);
      return Results.Ok(new BonhomiaCreatePayPalOrderResponse
      {
        Id = order.OrderId,
        Status = order.Status
      });
    }
    catch (BonhomiaPublicBookingException ex)
    {
      return MapBookingException(ex);
    }
  }

  private static async Task<IResult> ConfirmPayPalOrderAsync(
    string orderId,
    BonhomiaConfirmPayPalOrderRequest request,
    IBonhomiaQuoteTokenService quoteTokenService,
    IBonhomiaPublicBookingService bookingService,
    IBonhomiaPayPalClient payPalClient,
    ILoggerFactory loggerFactory,
    CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(orderId))
    {
      return Results.Problem(
        title: "Orden PayPal invalida",
        detail: "La orden PayPal es obligatoria.",
        statusCode: StatusCodes.Status400BadRequest);
    }

    if (!TryReadQuote(request.QuoteToken, request.QuoteFingerprint, quoteTokenService, out var quote, out var errorResult))
    {
      return errorResult!;
    }

    try
    {
      var liveQuote = await bookingService.CreateQuoteAsync(quote!.Request, ct);
      if (!string.Equals(liveQuote.Fingerprint, quote.Fingerprint, StringComparison.Ordinal))
      {
        return Results.Problem(
          title: "Cotizacion actualizada",
          detail: "Las fechas, extras o precios cambiaron. Vuelve a generar la cotizacion.",
          statusCode: StatusCodes.Status409Conflict);
      }

      var capture = await payPalClient.CaptureOrderAsync(orderId, BuildPayPalRequestId("cap", request.PaymentAttemptId, quote.QuoteId), ct);
      var result = await bookingService.CreatePaidReservationAsync(liveQuote, request.Customer, capture, ct);

      return Results.Ok(new BonhomiaConfirmPayPalOrderResponse
      {
        ReservationId = result.ReservationId,
        TransaccionId = result.TransaccionId,
        ClientName = result.ClientName,
        Total = result.Total
      });
    }
    catch (BonhomiaPublicBookingException ex)
    {
      return MapBookingException(ex);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      loggerFactory
        .CreateLogger(nameof(BonhomiaCheckoutApi))
        .LogError(ex, "Bonhomia checkout confirmation failed after PayPal order {OrderId}.", orderId);

      return Results.Problem(
        title: "No se pudo crear la reservacion",
        detail: "PayPal ya devolvio respuesta para esta orden, pero OrionERP no pudo terminar la reservacion. Corrige el problema y usa Revisar pago en PayPal; no intentes pagar de nuevo.",
        statusCode: StatusCodes.Status500InternalServerError,
        extensions: new Dictionary<string, object?>
        {
          ["errorCode"] = "checkout_confirm_failed"
        });
    }
  }

  private static bool TryReadQuote(
    string? token,
    string? quoteFingerprint,
    IBonhomiaQuoteTokenService quoteTokenService,
    out BonhomiaQuoteDto? quote,
    out IResult? errorResult)
  {
    errorResult = null;
    if (!quoteTokenService.TryValidate(token, out quote, out var tokenError))
    {
      errorResult = Results.Problem(
        title: "Cotizacion invalida",
        detail: tokenError,
        statusCode: StatusCodes.Status400BadRequest);
      return false;
    }

    if (!string.Equals(quote!.Fingerprint, quoteFingerprint, StringComparison.Ordinal))
    {
      errorResult = Results.Problem(
        title: "Cotizacion vencida",
        detail: "La cotizacion visible cambio. Vuelve a generar el resumen.",
        statusCode: StatusCodes.Status409Conflict);
      quote = null;
      return false;
    }

    return true;
  }

  private static IResult MapBookingException(BonhomiaPublicBookingException ex)
  {
    var statusCode = ex.ErrorCode switch
    {
      "not_available" or "quote_changed" or "capacity_exceeded" => StatusCodes.Status409Conflict,
      "paypal_not_configured" => StatusCodes.Status503ServiceUnavailable,
      "paypal_create_failed" or "paypal_capture_failed" or "paypal_auth_failed" => StatusCodes.Status502BadGateway,
      _ => StatusCodes.Status400BadRequest
    };

    return Results.Problem(
      title: "No se pudo completar el checkout",
      detail: ex.Message,
      statusCode: statusCode,
      extensions: new Dictionary<string, object?>
      {
        ["errorCode"] = ex.ErrorCode
      });
  }

  private static string BuildPayPalRequestId(string prefix, string? paymentAttemptId, Guid fallbackQuoteId)
  {
    var raw = string.IsNullOrWhiteSpace(paymentAttemptId)
      ? fallbackQuoteId.ToString("N")
      : paymentAttemptId.Trim();

    var safe = new string(raw
      .Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')
      .ToArray());

    if (string.IsNullOrWhiteSpace(safe))
    {
      safe = fallbackQuoteId.ToString("N");
    }

    var requestId = $"{prefix}-{safe}";
    return requestId.Length <= 38 ? requestId : requestId[..38];
  }
}

public sealed class BonhomiaCreatePayPalOrderRequest
{
  public string QuoteToken { get; set; } = string.Empty;
  public string QuoteFingerprint { get; set; } = string.Empty;
  public string PaymentAttemptId { get; set; } = string.Empty;
}

public sealed class BonhomiaCreatePayPalOrderResponse
{
  public string Id { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
}

public sealed class BonhomiaConfirmPayPalOrderRequest
{
  public string QuoteToken { get; set; } = string.Empty;
  public string QuoteFingerprint { get; set; } = string.Empty;
  public string PaymentAttemptId { get; set; } = string.Empty;
  public BonhomiaCustomerInfo Customer { get; set; } = new();
}

public sealed class BonhomiaConfirmPayPalOrderResponse
{
  public int ReservationId { get; set; }
  public int TransaccionId { get; set; }
  public string ClientName { get; set; } = string.Empty;
  public decimal Total { get; set; }
}
