using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Bonhomia.PublicBooking;

namespace OrionERP.Infrastructure.Features.Bonhomia.PublicBooking;

public sealed class BonhomiaPayPalClient : IBonhomiaPayPalClient
{
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

  private readonly HttpClient _httpClient;
  private readonly BonhomiaCheckoutOptions _options;
  private readonly ILogger<BonhomiaPayPalClient> _logger;
  private string? _accessToken;
  private DateTimeOffset _accessTokenExpiresAtUtc;

  public BonhomiaPayPalClient(
    HttpClient httpClient,
    IOptions<BonhomiaCheckoutOptions> options,
    ILogger<BonhomiaPayPalClient> logger)
  {
    _httpClient = httpClient;
    _options = options.Value;
    _logger = logger;
  }

  public async Task<BonhomiaPayPalOrderResult> CreateOrderAsync(
    BonhomiaQuoteDto quote,
    string idempotencyKey,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(quote);
    EnsureConfigured();

    var token = await GetAccessTokenAsync(ct);
    using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.PayPalBaseUri, "/v2/checkout/orders"));
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    request.Headers.TryAddWithoutValidation("PayPal-Request-Id", NormalizeRequestId(idempotencyKey));
    request.Content = JsonContent.Create(new
    {
      intent = "CAPTURE",
      purchase_units = new[]
      {
        new
        {
          reference_id = quote.QuoteId.ToString("N"),
          description = $"Bonhomia Suites - {quote.RoomName}",
          custom_id = quote.Fingerprint,
          amount = new
          {
            currency_code = quote.Currency,
            value = FormatAmount(quote.Total)
          }
        }
      }
    }, options: JsonOptions);

    using var response = await _httpClient.SendAsync(request, ct);
    var body = await response.Content.ReadAsStringAsync(ct);
    if (!response.IsSuccessStatusCode)
    {
      _logger.LogWarning("PayPal create order failed with status {Status}. Body: {Body}", response.StatusCode, body);
      throw new BonhomiaPublicBookingException("paypal_create_failed", "PayPal no pudo crear la orden de pago.");
    }

    using var document = JsonDocument.Parse(body);
    var root = document.RootElement;
    return new BonhomiaPayPalOrderResult
    {
      OrderId = root.GetProperty("id").GetString() ?? string.Empty,
      Status = root.TryGetProperty("status", out var status) ? status.GetString() ?? string.Empty : string.Empty
    };
  }

  public async Task<BonhomiaPayPalCaptureResult> CaptureOrderAsync(
    string orderId,
    string idempotencyKey,
    CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(orderId))
    {
      throw new BonhomiaPublicBookingException("paypal_order_required", "La orden PayPal es obligatoria.");
    }

    EnsureConfigured();

    var token = await GetAccessTokenAsync(ct);
    var existingCapture = await GetCapturedOrderAsync(orderId, token, ct);
    if (existingCapture is not null)
    {
      if (!existingCapture.IsCompleted)
      {
        var refreshed = await WaitForCompletedCaptureAsync(orderId, token, ct);
        if (refreshed is not null)
        {
          return refreshed;
        }
      }

      _logger.LogInformation(
        "PayPal order {OrderId} already has capture {CaptureId} with status {CaptureStatus}; using existing capture details.",
        existingCapture.OrderId,
        existingCapture.CaptureId,
        existingCapture.Status);
      return existingCapture;
    }

    using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.PayPalBaseUri, $"/v2/checkout/orders/{Uri.EscapeDataString(orderId.Trim())}/capture"));
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    request.Headers.TryAddWithoutValidation("PayPal-Request-Id", NormalizeRequestId(idempotencyKey));
    request.Content = JsonContent.Create(new { }, options: JsonOptions);

    using var response = await _httpClient.SendAsync(request, ct);
    var body = await response.Content.ReadAsStringAsync(ct);
    if (!response.IsSuccessStatusCode)
    {
      if (IsPayPalAlreadyCapturedResponse(body))
      {
        var capturedOrder = await GetCapturedOrderAsync(orderId, token, ct);
        if (capturedOrder is not null)
        {
          _logger.LogInformation(
            "PayPal order {OrderId} was already captured; using captured order details for reservation recovery.",
            orderId);
          return capturedOrder;
        }
      }

      _logger.LogWarning("PayPal capture failed for order {OrderId} with status {Status}. Body: {Body}", orderId, response.StatusCode, body);
      throw new BonhomiaPublicBookingException("paypal_capture_failed", "PayPal no pudo confirmar el pago.");
    }

    using var document = JsonDocument.Parse(body);
    if (TryMapCaptureResult(document.RootElement, orderId, out var result))
    {
      if (!result.IsCompleted)
      {
        var refreshed = await WaitForCompletedCaptureAsync(orderId, token, ct);
        if (refreshed is not null)
        {
          return refreshed;
        }

        _logger.LogWarning(
          "PayPal capture for order {OrderId} returned non-completed status {CaptureStatus}. Reason: {Reason}. Order status: {OrderStatus}.",
          result.OrderId,
          result.Status,
          result.StatusReason,
          result.OrderStatus);
      }

      return result;
    }

    _logger.LogWarning("PayPal capture response for order {OrderId} did not include a capture. Body: {Body}", orderId, body);
    throw new BonhomiaPublicBookingException("paypal_capture_failed", "PayPal no devolvio la confirmacion del pago.");
  }

  private async Task<BonhomiaPayPalCaptureResult?> GetCapturedOrderAsync(
    string orderId,
    string token,
    CancellationToken ct)
  {
    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_options.PayPalBaseUri, $"/v2/checkout/orders/{Uri.EscapeDataString(orderId.Trim())}"));
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    using var response = await _httpClient.SendAsync(request, ct);
    var body = await response.Content.ReadAsStringAsync(ct);
    if (!response.IsSuccessStatusCode)
    {
      _logger.LogWarning("PayPal get order failed for {OrderId} with status {Status}. Body: {Body}", orderId, response.StatusCode, body);
      return null;
    }

    using var document = JsonDocument.Parse(body);
    return TryMapCaptureResult(document.RootElement, orderId, out var result)
      ? result
      : null;
  }

  private async Task<BonhomiaPayPalCaptureResult?> WaitForCompletedCaptureAsync(
    string orderId,
    string token,
    CancellationToken ct)
  {
    for (var attempt = 0; attempt < 2; attempt++)
    {
      await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct);
      var refreshed = await GetCapturedOrderAsync(orderId, token, ct);
      if (refreshed?.IsCompleted == true)
      {
        return refreshed;
      }
    }

    return null;
  }

  private async Task<string> GetAccessTokenAsync(CancellationToken ct)
  {
    if (!string.IsNullOrWhiteSpace(_accessToken) && _accessTokenExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
    {
      return _accessToken;
    }

    using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.PayPalBaseUri, "/v1/oauth2/token"));
    var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_options.PayPalClientId}:{_options.PayPalClientSecret}"));
    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
    {
      ["grant_type"] = "client_credentials"
    });

    using var response = await _httpClient.SendAsync(request, ct);
    var body = await response.Content.ReadAsStringAsync(ct);
    if (!response.IsSuccessStatusCode)
    {
      _logger.LogWarning("PayPal OAuth failed with status {Status}. Body: {Body}", response.StatusCode, body);
      throw new BonhomiaPublicBookingException("paypal_auth_failed", "PayPal no pudo autenticar la cuenta configurada.");
    }

    using var document = JsonDocument.Parse(body);
    var root = document.RootElement;
    _accessToken = root.GetProperty("access_token").GetString();
    var expiresIn = root.TryGetProperty("expires_in", out var expires)
      ? expires.GetInt32()
      : 300;
    _accessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(expiresIn, 60));

    return _accessToken ?? throw new BonhomiaPublicBookingException("paypal_auth_failed", "PayPal no devolvio token de acceso.");
  }

  private void EnsureConfigured()
  {
    if (!_options.IsPayPalConfigured)
    {
      throw new BonhomiaPublicBookingException("paypal_not_configured", "PayPal todavia no esta configurado para recibir pagos.");
    }
  }

  private static string FormatAmount(decimal value)
    => value.ToString("0.00", CultureInfo.InvariantCulture);

  private static bool IsPayPalAlreadyCapturedResponse(string body)
  {
    if (string.IsNullOrWhiteSpace(body))
    {
      return false;
    }

    try
    {
      using var document = JsonDocument.Parse(body);
      if (document.RootElement.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
      {
        foreach (var detail in details.EnumerateArray())
        {
          if (detail.TryGetProperty("issue", out var issue)
              && IsAlreadyCapturedIssue(issue.GetString()))
          {
            return true;
          }
        }
      }

      if (document.RootElement.TryGetProperty("name", out var name)
          && IsAlreadyCapturedIssue(name.GetString()))
      {
        return true;
      }
    }
    catch (JsonException)
    {
      return ContainsAlreadyCapturedIssue(body);
    }

    return ContainsAlreadyCapturedIssue(body);
  }

  private static bool IsAlreadyCapturedIssue(string? value)
    => string.Equals(value, "PAYMENT_ALREADY_DONE", StringComparison.OrdinalIgnoreCase)
       || string.Equals(value, "ORDER_ALREADY_CAPTURED", StringComparison.OrdinalIgnoreCase);

  private static bool ContainsAlreadyCapturedIssue(string body)
    => body.Contains("PAYMENT_ALREADY_DONE", StringComparison.OrdinalIgnoreCase)
       || body.Contains("ORDER_ALREADY_CAPTURED", StringComparison.OrdinalIgnoreCase);

  private static bool TryMapCaptureResult(
    JsonElement root,
    string fallbackOrderId,
    out BonhomiaPayPalCaptureResult result)
  {
    result = new BonhomiaPayPalCaptureResult();
    if (!root.TryGetProperty("purchase_units", out var purchaseUnits) || purchaseUnits.ValueKind != JsonValueKind.Array)
    {
      return false;
    }

    foreach (var purchaseUnit in purchaseUnits.EnumerateArray())
    {
      if (!purchaseUnit.TryGetProperty("payments", out var payments)
          || !payments.TryGetProperty("captures", out var captures)
          || captures.ValueKind != JsonValueKind.Array)
      {
        continue;
      }

      foreach (var capture in captures.EnumerateArray())
      {
        if (!capture.TryGetProperty("amount", out var amount))
        {
          continue;
        }

        result = new BonhomiaPayPalCaptureResult
        {
          OrderId = root.TryGetProperty("id", out var orderId) ? orderId.GetString() ?? fallbackOrderId : fallbackOrderId,
          OrderStatus = root.TryGetProperty("status", out var rootStatus) ? rootStatus.GetString() ?? string.Empty : string.Empty,
          Status = capture.TryGetProperty("status", out var captureStatus)
            ? captureStatus.GetString() ?? string.Empty
            : root.TryGetProperty("status", out var fallbackRootStatus) ? fallbackRootStatus.GetString() ?? string.Empty : string.Empty,
          StatusReason = GetCaptureStatusReason(capture),
          CaptureId = capture.TryGetProperty("id", out var captureId) ? captureId.GetString() ?? string.Empty : string.Empty,
          Currency = amount.TryGetProperty("currency_code", out var currency) ? currency.GetString() ?? string.Empty : string.Empty,
          Amount = amount.TryGetProperty("value", out var value)
            && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
              ? parsed
              : 0m,
          PayerName = GetPayerName(root),
          PayerEmail = GetPayerEmail(root),
          PayerPhone = GetPayerPhone(root)
        };

        return !string.IsNullOrWhiteSpace(result.CaptureId);
      }
    }

    return false;
  }

  private static string GetCaptureStatusReason(JsonElement capture)
  {
    if (capture.TryGetProperty("status_details", out var statusDetails)
        && statusDetails.TryGetProperty("reason", out var reason))
    {
      return reason.GetString() ?? string.Empty;
    }

    return string.Empty;
  }

  private static string GetPayerName(JsonElement root)
  {
    if (TryGetPaymentSourcePayPal(root, out var paypal)
        && TryGetName(paypal, out var sourceName))
    {
      return sourceName;
    }

    if (root.TryGetProperty("payer", out var payer)
        && TryGetName(payer, out var payerName))
    {
      return payerName;
    }

    if (TryGetShippingName(root, out var shippingName))
    {
      return shippingName;
    }

    return string.Empty;
  }

  private static string GetPayerEmail(JsonElement root)
  {
    if (TryGetPaymentSourcePayPal(root, out var paypal)
        && paypal.TryGetProperty("email_address", out var sourceEmail))
    {
      return sourceEmail.GetString() ?? string.Empty;
    }

    return root.TryGetProperty("payer", out var payer) && payer.TryGetProperty("email_address", out var payerEmail)
      ? payerEmail.GetString() ?? string.Empty
      : string.Empty;
  }

  private static string GetPayerPhone(JsonElement root)
  {
    if (TryGetPaymentSourcePayPal(root, out var paypal)
        && TryGetPhoneNumber(paypal, out var sourcePhone))
    {
      return sourcePhone;
    }

    if (root.TryGetProperty("payer", out var payer)
        && TryGetPhoneNumber(payer, out var payerPhone))
    {
      return payerPhone;
    }

    return string.Empty;
  }

  private static bool TryGetPaymentSourcePayPal(JsonElement root, out JsonElement paypal)
  {
    paypal = default;
    return root.TryGetProperty("payment_source", out var paymentSource)
      && paymentSource.TryGetProperty("paypal", out paypal);
  }

  private static bool TryGetName(JsonElement parent, out string value)
  {
    value = string.Empty;
    if (!parent.TryGetProperty("name", out var name))
    {
      return false;
    }

    if (name.ValueKind == JsonValueKind.String)
    {
      value = name.GetString()?.Trim() ?? string.Empty;
      return !string.IsNullOrWhiteSpace(value);
    }

    if (name.ValueKind != JsonValueKind.Object)
    {
      return false;
    }

    if (name.TryGetProperty("full_name", out var fullName))
    {
      value = fullName.GetString()?.Trim() ?? string.Empty;
      return !string.IsNullOrWhiteSpace(value);
    }

    var givenName = name.TryGetProperty("given_name", out var given)
      ? given.GetString()
      : string.Empty;
    var surname = name.TryGetProperty("surname", out var family)
      ? family.GetString()
      : string.Empty;
    value = string.Join(" ", new[] { givenName, surname }.Where(part => !string.IsNullOrWhiteSpace(part))).Trim();
    return !string.IsNullOrWhiteSpace(value);
  }

  private static bool TryGetPhoneNumber(JsonElement parent, out string value)
  {
    value = string.Empty;
    if (parent.TryGetProperty("phone_number", out var phoneNumber)
        && TryReadPhoneNumber(phoneNumber, out value))
    {
      return true;
    }

    return parent.TryGetProperty("phone", out var phone)
      && (TryReadPhoneNumber(phone, out value)
          || phone.TryGetProperty("phone_number", out var nestedPhone) && TryReadPhoneNumber(nestedPhone, out value));
  }

  private static bool TryReadPhoneNumber(JsonElement phoneNumber, out string value)
  {
    value = string.Empty;
    if (phoneNumber.ValueKind == JsonValueKind.String)
    {
      value = phoneNumber.GetString()?.Trim() ?? string.Empty;
      return !string.IsNullOrWhiteSpace(value);
    }

    if (phoneNumber.ValueKind != JsonValueKind.Object)
    {
      return false;
    }

    var localNumber = string.Empty;
    if (phoneNumber.TryGetProperty("national_number", out var nationalNumber))
    {
      localNumber = nationalNumber.GetString()?.Trim() ?? string.Empty;
    }
    else if (phoneNumber.TryGetProperty("number", out var number))
    {
      localNumber = number.GetString()?.Trim() ?? string.Empty;
    }

    if (string.IsNullOrWhiteSpace(localNumber))
    {
      return false;
    }

    if (localNumber.StartsWith("+", StringComparison.Ordinal))
    {
      value = localNumber;
      return true;
    }

    if (phoneNumber.TryGetProperty("country_code", out var countryCodeElement))
    {
      var countryCode = countryCodeElement.GetString()?.Trim().TrimStart('+');
      if (!string.IsNullOrWhiteSpace(countryCode))
      {
        value = $"+{countryCode} {localNumber}";
        return true;
      }
    }

    value = localNumber;
    return true;
  }

  private static bool TryGetShippingName(JsonElement root, out string value)
  {
    value = string.Empty;
    if (!root.TryGetProperty("purchase_units", out var purchaseUnits) || purchaseUnits.ValueKind != JsonValueKind.Array)
    {
      return false;
    }

    foreach (var purchaseUnit in purchaseUnits.EnumerateArray())
    {
      if (purchaseUnit.TryGetProperty("shipping", out var shipping)
          && TryGetName(shipping, out value))
      {
        return true;
      }
    }

    return false;
  }

  private static string NormalizeRequestId(string idempotencyKey)
  {
    var normalized = string.IsNullOrWhiteSpace(idempotencyKey)
      ? Guid.NewGuid().ToString("N")
      : idempotencyKey.Trim();

    return normalized.Length <= 38
      ? normalized
      : normalized[..38];
  }
}
