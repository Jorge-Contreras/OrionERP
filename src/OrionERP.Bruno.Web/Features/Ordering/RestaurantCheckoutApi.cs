using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using OrionERP.Application.Features.Platform;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Bruno.Web.Features.Ordering;

public static class RestaurantCheckoutApi
{
  private const int MaximumBrowserRequestBodyBytes = 262_144;
  private const int MaximumWebhookBodyBytes = 1_048_576;
  private const int MaximumTransmissionIdLength = 256;
  private const int MaximumTransmissionTimeLength = 64;
  private const int MaximumCertificateUrlLength = 2_048;
  private const int MaximumAuthenticationAlgorithmLength = 128;
  private const int MaximumTransmissionSignatureLength = 8_192;
  private const int MaximumProviderEventIdLength = 100;
  private const int MaximumProviderEventTypeLength = 100;
  private static readonly UTF8Encoding StrictUtf8 = new(
    encoderShouldEmitUTF8Identifier: false,
    throwOnInvalidBytes: true);

  public static IEndpointRouteBuilder MapRestaurantCheckoutApi(this IEndpointRouteBuilder endpoints)
  {
    var group = endpoints
      .MapGroup("/api/restaurant/checkout")
      .AllowAnonymous()
      .RequireRateLimiting("checkout");

    group.MapPost("/quote", QuoteAsync)
      .WithMetadata(new RequestSizeLimitAttribute(MaximumBrowserRequestBodyBytes));
    group.MapPost("/paypal-orders", CreatePayPalOrderAsync)
      .WithMetadata(new RequestSizeLimitAttribute(MaximumBrowserRequestBodyBytes));
    group.MapPost("/paypal-orders/{orderId}/capture", CapturePayPalOrderAsync)
      .WithMetadata(new RequestSizeLimitAttribute(MaximumBrowserRequestBodyBytes));
    group.MapGet("/status/{trackingToken}", GetStatusAsync);
    group.MapPost("/paypal-webhook", ProcessPayPalWebhookAsync)
      .DisableAntiforgery()
      .WithMetadata(new RequestSizeLimitAttribute(MaximumWebhookBodyBytes))
      .RequireRateLimiting("webhook");

    return endpoints;
  }

  private static async Task<IResult> QuoteAsync(
    RestaurantOnlineQuoteRequest request,
    HttpContext context,
    IAntiforgery antiforgery,
    IPublicWebsiteInstanceContext website,
    IRestaurantMembershipService membership,
    IOnlineRestaurantCheckoutService checkout,
    CancellationToken ct)
  {
    if (!await HasValidAntiforgeryAsync(antiforgery, context))
      return InvalidAntiforgery();
    var binding = await website.ResolveRequiredAsync(ct);
    var memberId = await ResolveMemberIdAsync(context.User, binding, membership, ct);
    var result = await checkout.QuoteAsync(binding, request, memberId, ct);
    return MapResult(result.Succeeded, result.Code, result.Message, result);
  }

  private static async Task<IResult> CreatePayPalOrderAsync(
    RestaurantOnlinePayPalOrderCreateRequest request,
    HttpContext context,
    IAntiforgery antiforgery,
    IPublicWebsiteInstanceContext website,
    IRestaurantMembershipService membership,
    IOnlineRestaurantCheckoutService checkout,
    CancellationToken ct)
  {
    if (!await HasValidAntiforgeryAsync(antiforgery, context))
      return InvalidAntiforgery();
    var binding = await website.ResolveRequiredAsync(ct);
    var memberId = await ResolveMemberIdAsync(context.User, binding, membership, ct);
    var result = await checkout.CreatePayPalOrderAsync(binding, request, memberId, ct);
    return MapResult(result.Succeeded, result.Code, result.Message, result);
  }

  private static async Task<IResult> CapturePayPalOrderAsync(
    string orderId,
    RestaurantOnlinePayPalCaptureRequest request,
    HttpContext context,
    IAntiforgery antiforgery,
    IPublicWebsiteInstanceContext website,
    IRestaurantMembershipService membership,
    IOnlineRestaurantCheckoutService checkout,
    CancellationToken ct)
  {
    if (!await HasValidAntiforgeryAsync(antiforgery, context))
      return InvalidAntiforgery();
    if (string.IsNullOrWhiteSpace(orderId))
      return Problem("invalid_paypal_order", "La orden de PayPal es obligatoria.", StatusCodes.Status400BadRequest);

    var binding = await website.ResolveRequiredAsync(ct);
    var memberId = await ResolveMemberIdAsync(context.User, binding, membership, ct);
    var result = await checkout.CapturePayPalOrderAsync(binding, orderId.Trim(), request, memberId, ct);
    return MapResult(result.Succeeded, result.Code, result.Message, result);
  }

  private static async Task<IResult> GetStatusAsync(
    string trackingToken,
    IPublicWebsiteInstanceContext website,
    IOnlineRestaurantCheckoutService checkout,
    CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(trackingToken) || trackingToken.Length > 2048)
      return Results.NotFound();

    var binding = await website.ResolveRequiredAsync(ct);
    var result = await checkout.GetStatusAsync(binding, trackingToken, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
  }

  private static async Task<IResult> ProcessPayPalWebhookAsync(
    HttpRequest request,
    IPublicWebsiteInstanceContext website,
    IOnlineRestaurantCheckoutService checkout,
    CancellationToken ct)
  {
    if (request.ContentLength is > MaximumWebhookBodyBytes)
      return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

    var body = await ReadWebhookBodyAsync(request.Body, request.ContentLength, ct);
    if (body.IsTooLarge)
      return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    if (body.IsInvalid || string.IsNullOrWhiteSpace(body.Value))
      return Results.BadRequest();

    var webhookRequest = new RestaurantPayPalWebhookRequest
    {
      RawBody = body.Value,
      TransmissionId = Header(request, "PAYPAL-TRANSMISSION-ID"),
      TransmissionTime = Header(request, "PAYPAL-TRANSMISSION-TIME"),
      CertificateUrl = Header(request, "PAYPAL-CERT-URL"),
      AuthenticationAlgorithm = Header(request, "PAYPAL-AUTH-ALGO"),
      TransmissionSignature = Header(request, "PAYPAL-TRANSMISSION-SIG")
    };
    if (!HasRequiredWebhookHeaders(webhookRequest) || !HasValidWebhookShape(body.Value))
      return Results.BadRequest();

    var binding = await website.ResolveRequiredAsync(ct);
    var result = await checkout.ProcessPayPalWebhookAsync(binding, webhookRequest, ct);

    return result.Accepted ? Results.Ok() : Results.BadRequest();
  }

  private static async Task<WebhookBodyReadResult> ReadWebhookBodyAsync(
    Stream requestBody,
    long? contentLength,
    CancellationToken ct)
  {
    if (contentLength is > MaximumWebhookBodyBytes)
      return WebhookBodyReadResult.TooLarge;

    var initialCapacity = contentLength is > 0
      ? (int)Math.Min(contentLength.Value, MaximumWebhookBodyBytes)
      : 0;
    using var buffered = new MemoryStream(initialCapacity);
    var rented = ArrayPool<byte>.Shared.Rent(16 * 1024);
    try
    {
      while (true)
      {
        var bytesRemaining = MaximumWebhookBodyBytes - (int)buffered.Length;
        var readLength = Math.Min(rented.Length, bytesRemaining + 1);
        var read = await requestBody.ReadAsync(rented.AsMemory(0, readLength), ct);
        if (read == 0) break;
        if (read > bytesRemaining)
          return WebhookBodyReadResult.TooLarge;
        buffered.Write(rented, 0, read);
      }

      try
      {
        return new WebhookBodyReadResult(
          StrictUtf8.GetString(buffered.GetBuffer(), 0, (int)buffered.Length),
          IsTooLarge: false,
          IsInvalid: false);
      }
      catch (DecoderFallbackException)
      {
        return WebhookBodyReadResult.Invalid;
      }
    }
    finally
    {
      ArrayPool<byte>.Shared.Return(rented);
    }
  }

  private static bool HasRequiredWebhookHeaders(RestaurantPayPalWebhookRequest request)
    => IsBoundedHeader(request.TransmissionId, MaximumTransmissionIdLength)
      && IsBoundedHeader(request.TransmissionTime, MaximumTransmissionTimeLength)
      && IsBoundedHeader(request.CertificateUrl, MaximumCertificateUrlLength)
      && IsBoundedHeader(request.AuthenticationAlgorithm, MaximumAuthenticationAlgorithmLength)
      && IsBoundedHeader(request.TransmissionSignature, MaximumTransmissionSignatureLength);

  private static bool IsBoundedHeader(string value, int maximumLength)
    => !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength;

  private static bool HasValidWebhookShape(string rawBody)
  {
    try
    {
      using var document = JsonDocument.Parse(rawBody, new JsonDocumentOptions { MaxDepth = 64 });
      var root = document.RootElement;
      return root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("id", out var eventId)
        && eventId.ValueKind == JsonValueKind.String
        && IsBoundedJsonString(eventId, MaximumProviderEventIdLength)
        && root.TryGetProperty("event_type", out var eventType)
        && eventType.ValueKind == JsonValueKind.String
        && IsBoundedJsonString(eventType, MaximumProviderEventTypeLength);
    }
    catch (JsonException)
    {
      return false;
    }
  }

  private static bool IsBoundedJsonString(JsonElement value, int maximumLength)
  {
    var text = value.GetString();
    return !string.IsNullOrWhiteSpace(text) && text.Length <= maximumLength;
  }

  private static async Task<Guid?> ResolveMemberIdAsync(
    ClaimsPrincipal principal,
    PublicSiteBinding binding,
    IRestaurantMembershipService membership,
    CancellationToken ct)
  {
    if (principal.Identity?.IsAuthenticated != true)
      return null;

    var identityUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(identityUserId))
      return null;

    var profile = await membership.GetMemberProfileByIdentityAsync(
      binding.CompanyRfc,
      binding.PublicSiteId,
      identityUserId,
      ct);
    return profile?.Status == LoyaltyMemberStatuses.Active ? profile.Id : null;
  }

  private static IResult MapResult<T>(bool succeeded, string code, string message, T result)
    => succeeded
      ? Results.Ok(result)
      : Problem(code, message, StatusFor(code));

  private static IResult Problem(string code, string message, int status)
    => Results.Problem(
      title: "No se pudo completar el pedido",
      detail: message,
      statusCode: status,
      extensions: new Dictionary<string, object?> { ["errorCode"] = code });

  private static async Task<bool> HasValidAntiforgeryAsync(
    IAntiforgery antiforgery,
    HttpContext context)
  {
    try
    {
      await antiforgery.ValidateRequestAsync(context);
      return true;
    }
    catch (AntiforgeryValidationException)
    {
      return false;
    }
  }

  private static IResult InvalidAntiforgery()
    => Problem(
      "invalid_antiforgery",
      "La sesión de pago venció. Actualiza la página e inténtalo de nuevo.",
      StatusCodes.Status400BadRequest);

  private static int StatusFor(string? code) => code?.Trim().ToLowerInvariant() switch
  {
    "ordering_disabled" or "ordering_paused" or "ordering_closed" or "processor_unavailable" or "paypal_not_configured"
      => StatusCodes.Status503ServiceUnavailable,
    "quote_changed" or "quote_expired" or "requote_required" or "not_available" or "sold_out" or "maximum_exceeded"
      => StatusCodes.Status409Conflict,
    "paypal_auth_failed" or "paypal_create_failed" or "paypal_capture_failed" or "paypal_order_validation_failed"
      => StatusCodes.Status502BadGateway,
    "not_found" => StatusCodes.Status404NotFound,
    _ => StatusCodes.Status400BadRequest
  };

  private static string Header(HttpRequest request, string name)
    => request.Headers[name].ToString().Trim();

  private sealed record WebhookBodyReadResult(string Value, bool IsTooLarge, bool IsInvalid)
  {
    public static WebhookBodyReadResult TooLarge { get; } = new(string.Empty, true, false);
    public static WebhookBodyReadResult Invalid { get; } = new(string.Empty, false, true);
  }
}
