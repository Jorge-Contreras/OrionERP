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
  private const int MaximumFacadeRequestBodyBytes = 13 * 1024 * 1024;
  /// <summary>
  /// El aviso de Clip es un objeto de tres campos cortos. Cualquier cosa mayor
  /// no es un aviso legítimo.
  /// </summary>
  private const int MaximumWebhookBodyBytes = 4_096;
  private const int MaximumProviderEventIdLength = 64;
  private const int MaximumProviderEventTypeLength = 30;
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
    group.MapPost("/intents", BeginCheckoutAsync)
      .WithMetadata(new RequestSizeLimitAttribute(MaximumBrowserRequestBodyBytes));
    group.MapPost("/facade", UploadFacadeAsync)
      .WithMetadata(new RequestSizeLimitAttribute(MaximumFacadeRequestBodyBytes))
      .RequireRateLimiting("checkout-photo");
    group.MapPost("/charge", ChargeAsync)
      .WithMetadata(new RequestSizeLimitAttribute(MaximumBrowserRequestBodyBytes));
    group.MapPost("/charge/confirm", ConfirmChargeAsync)
      .WithMetadata(new RequestSizeLimitAttribute(MaximumBrowserRequestBodyBytes));
    group.MapGet("/status/{trackingToken}", GetStatusAsync);
    group.MapPost("/clip-webhook", ProcessClipWebhookAsync)
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

  private static async Task<IResult> BeginCheckoutAsync(
    RestaurantOnlineCheckoutBeginRequest request,
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
    var result = await checkout.BeginCheckoutAsync(binding, request, memberId, ct);
    return MapResult(result.Succeeded, result.Code, result.Message, result);
  }

  private static async Task<IResult> ChargeAsync(
    RestaurantOnlineChargeRequest request,
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
    var result = await checkout.ChargeAsync(binding, request, memberId, ct);
    return MapResult(result.Succeeded, result.Code, result.Message, result);
  }

  private static async Task<IResult> UploadFacadeAsync(
    HttpRequest request,
    HttpContext context,
    IAntiforgery antiforgery,
    IPublicWebsiteInstanceContext website,
    IOnlineRestaurantCheckoutService checkout,
    CancellationToken ct)
  {
    if (!await HasValidAntiforgeryAsync(antiforgery, context))
      return InvalidAntiforgery();
    if (!request.HasFormContentType || request.ContentLength is > MaximumFacadeRequestBodyBytes)
      return Problem("invalid_image", "Selecciona una foto válida de hasta 12 MB.", StatusCodes.Status400BadRequest);

    var form = await request.ReadFormAsync(ct);
    var photo = form.Files.GetFile("photo");
    var trackingToken = form["trackingToken"].ToString();
    var uploadToken = form["uploadToken"].ToString();
    if (photo is null || photo.Length is <= 0 or > 12 * 1024 * 1024)
      return Problem("invalid_image", "Selecciona una foto válida de hasta 12 MB.", StatusCodes.Status400BadRequest);

    await using var input = photo.OpenReadStream();
    using var content = new MemoryStream((int)photo.Length);
    await input.CopyToAsync(content, ct);
    var binding = await website.ResolveRequiredAsync(ct);
    var result = await checkout.UploadFacadeAsync(binding, new RestaurantOnlineFacadeUploadRequest
    {
      TrackingToken = trackingToken,
      UploadToken = uploadToken,
      FileName = photo.FileName,
      Content = content.ToArray()
    }, ct);
    return MapResult(result.Succeeded, result.Code, result.Message, result);
  }

  private static async Task<IResult> ConfirmChargeAsync(
    RestaurantOnlineChargeConfirmRequest request,
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
    var result = await checkout.ConfirmChargeAsync(binding, request, memberId, ct);
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

  /// <summary>
  /// Clip no firma sus avisos, así que aquí no hay nada que verificar
  /// criptográficamente: el cuerpo sólo se acepta por su forma y el estado real
  /// se consulta después contra la API de Clip. Un aviso falso cuesta una
  /// consulta y no puede mover dinero ni estado.
  /// </summary>
  private static async Task<IResult> ProcessClipWebhookAsync(
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
    if (body.IsInvalid || string.IsNullOrWhiteSpace(body.Value) || !HasValidWebhookShape(body.Value))
      return Results.BadRequest();

    var binding = await website.ResolveRequiredAsync(ct);
    var result = await checkout.ProcessClipWebhookAsync(
      binding,
      new RestaurantClipWebhookRequest { RawBody = body.Value },
      ct);

    // Un aviso de un pago que no es nuestro se acepta y se descarta: reintentarlo
    // no lo volvería nuestro.
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
    var rented = ArrayPool<byte>.Shared.Rent(8 * 1024);
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

  private static bool HasValidWebhookShape(string rawBody)
  {
    try
    {
      using var document = JsonDocument.Parse(rawBody, new JsonDocumentOptions { MaxDepth = 16 });
      var root = document.RootElement;
      return root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("id", out var paymentId)
        && paymentId.ValueKind == JsonValueKind.String
        && IsBoundedJsonString(paymentId, MaximumProviderEventIdLength)
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
    "ordering_disabled" or "ordering_paused" or "ordering_closed" or "processor_unavailable" or "clip_not_configured"
      => StatusCodes.Status503ServiceUnavailable,
    "quote_changed" or "quote_expired" or "requote_required" or "not_available" or "sold_out" or "maximum_exceeded"
      => StatusCodes.Status409Conflict,
    // Un cargo ya en vuelo no es un error del cliente: es el estado correcto y no
    // debe invitar a reintentar.
    "charge_in_flight" => StatusCodes.Status409Conflict,
    "not_found" => StatusCodes.Status404NotFound,
    _ => StatusCodes.Status400BadRequest
  };

  private sealed record WebhookBodyReadResult(string Value, bool IsTooLarge, bool IsInvalid)
  {
    public static WebhookBodyReadResult TooLarge { get; } = new(string.Empty, true, false);
    public static WebhookBodyReadResult Invalid { get; } = new(string.Empty, false, true);
  }
}
