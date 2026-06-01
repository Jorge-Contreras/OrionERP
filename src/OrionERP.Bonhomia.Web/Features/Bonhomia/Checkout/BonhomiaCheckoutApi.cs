using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Bonhomia.PublicBooking;
using OrionERP.Infrastructure.Features.Reservaciones.ListaReservaciones.Pdf;

namespace OrionERP.Bonhomia.Web.Features.Bonhomia.Checkout;

public static class BonhomiaCheckoutApi
{
  public static IEndpointRouteBuilder MapBonhomiaCheckoutApi(this IEndpointRouteBuilder endpoints)
  {
    endpoints.MapPost("/api/bonhomia/checkout/orders", CreatePayPalOrderAsync).AllowAnonymous();
    endpoints.MapPost("/api/bonhomia/checkout/orders/{orderId}", ConfirmPayPalOrderAsync).AllowAnonymous();
    endpoints.MapGet("/api/bonhomia/checkout/reservations/{reservationId:int}/pdf", DownloadReservationPdfAsync).AllowAnonymous();
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
    IBonhomiaReservationPdfTokenService pdfTokenService,
    IOptions<BonhomiaCheckoutOptions> options,
    ILoggerFactory loggerFactory,
    HttpContext httpContext,
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
      var customer = BuildCustomerFromPayPal(request.Customer, capture);
      var result = await bookingService.CreatePaidReservationAsync(liveQuote, customer, capture, ct);

      return Results.Ok(new BonhomiaConfirmPayPalOrderResponse
      {
        ReservationId = result.ReservationId,
        TransaccionId = result.TransaccionId,
        ClientName = result.ClientName,
        CustomerEmail = customer.Email.Trim(),
        CustomerPhone = customer.Phone.Trim(),
        RoomName = liveQuote.RoomName,
        RoomImage = liveQuote.RoomImage,
        CheckIn = liveQuote.CheckIn,
        CheckOut = liveQuote.CheckOut,
        Nights = liveQuote.Nights,
        Guests = liveQuote.Guests,
        SuiteSubtotal = liveQuote.SuiteSubtotal,
        ExtrasSubtotal = liveQuote.ExtrasSubtotal,
        ExperiencesSubtotal = liveQuote.ExperiencesSubtotal,
        SubTotal = liveQuote.SubTotal,
        Tax = liveQuote.Tax,
        Ish = liveQuote.Ish,
        Total = result.Total,
        Currency = liveQuote.Currency,
        Lines = liveQuote.Lines,
        PayPalOrderId = string.IsNullOrWhiteSpace(capture.OrderId) ? orderId.Trim() : capture.OrderId,
        PayPalCaptureId = capture.CaptureId,
        PayPalOrderStatus = capture.OrderStatus,
        PayPalStatus = capture.Status,
        PayPalStatusReason = capture.StatusReason,
        PayPalPayerEmail = capture.PayerEmail,
        PayPalAmount = capture.Amount,
        PayPalCurrency = capture.Currency,
        ConfirmedAtUtc = DateTimeOffset.UtcNow,
        PdfUrl = BuildReservationPdfUrl(
          httpContext,
          options.Value,
          result.ReservationId,
          pdfTokenService.CreateToken(result.ReservationId))
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

  private static BonhomiaCustomerInfo BuildCustomerFromPayPal(
    BonhomiaCustomerInfo? fallbackCustomer,
    BonhomiaPayPalCaptureResult capture)
  {
    var email = FirstPresent(capture.PayerEmail, fallbackCustomer?.Email);
    return new BonhomiaCustomerInfo
    {
      FullName = FirstPresent(capture.PayerName, fallbackCustomer?.FullName, email, "Cliente PayPal"),
      Email = email,
      Phone = FirstPresent(capture.PayerPhone, fallbackCustomer?.Phone)
    };
  }

  private static string FirstPresent(params string?[] values)
    => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

  private static async Task<IResult> DownloadReservationPdfAsync(
    int reservationId,
    string? token,
    IBonhomiaReservationPdfTokenService pdfTokenService,
    IBonhomiaPublicBookingService bookingService,
    IReservacionPdfDocumentFactory pdfDocumentFactory,
    IReservacionPdfService pdfService,
    CancellationToken ct)
  {
    if (!pdfTokenService.TryValidate(reservationId, token, out var errorMessage))
    {
      return Results.Problem(title: "Acceso no autorizado", detail: errorMessage, statusCode: StatusCodes.Status401Unauthorized);
    }

    var detail = await bookingService.GetReservationDetailAsync(reservationId, ct);
    if (detail is null)
    {
      return Results.NotFound();
    }

    var document = pdfDocumentFactory.CreateFromDetail(detail);
    var bytes = pdfService.Generate(document);
    var fileName = $"bonhomia-reservacion-{reservationId:D6}.pdf";

    return Results.File(bytes, "application/pdf", fileName);
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

  private static string BuildReservationPdfUrl(HttpContext httpContext, BonhomiaCheckoutOptions options, int reservationId, string token)
  {
    var path = $"/api/bonhomia/checkout/reservations/{reservationId}/pdf?token={Uri.EscapeDataString(token)}";
    var configuredBaseUrl = options.PublicBaseUrl?.Trim();
    if (!string.IsNullOrWhiteSpace(configuredBaseUrl)
        && Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var baseUri))
    {
      return new Uri(baseUri, path).ToString();
    }

    return $"{httpContext.Request.Scheme}://{httpContext.Request.Host}{path}";
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
  public string CustomerEmail { get; set; } = string.Empty;
  public string CustomerPhone { get; set; } = string.Empty;
  public string RoomName { get; set; } = string.Empty;
  public string RoomImage { get; set; } = string.Empty;
  public DateOnly CheckIn { get; set; }
  public DateOnly CheckOut { get; set; }
  public int Nights { get; set; }
  public int Guests { get; set; }
  public decimal SuiteSubtotal { get; set; }
  public decimal ExtrasSubtotal { get; set; }
  public decimal ExperiencesSubtotal { get; set; }
  public decimal SubTotal { get; set; }
  public decimal Tax { get; set; }
  public decimal Ish { get; set; }
  public decimal Total { get; set; }
  public string Currency { get; set; } = "MXN";
  public IReadOnlyList<BonhomiaQuoteLineDto> Lines { get; set; } = Array.Empty<BonhomiaQuoteLineDto>();
  public string PayPalOrderId { get; set; } = string.Empty;
  public string PayPalCaptureId { get; set; } = string.Empty;
  public string PayPalOrderStatus { get; set; } = string.Empty;
  public string PayPalStatus { get; set; } = string.Empty;
  public string PayPalStatusReason { get; set; } = string.Empty;
  public string PayPalPayerEmail { get; set; } = string.Empty;
  public decimal PayPalAmount { get; set; }
  public string PayPalCurrency { get; set; } = "MXN";
  public DateTimeOffset ConfirmedAtUtc { get; set; }
  public string PdfUrl { get; set; } = string.Empty;
}
