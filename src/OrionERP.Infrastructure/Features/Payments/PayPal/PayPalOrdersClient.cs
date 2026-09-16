using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Payments.PayPal;

namespace OrionERP.Infrastructure.Features.Payments.PayPal;

/// <summary>
/// PayPal Orders/Payments REST transport. Tenant ownership and quote matching
/// are deliberately enforced by the calling domain adapter, not by this class.
/// </summary>
public sealed class PayPalOrdersClient<TOptions> : IPayPalOrdersClient
  where TOptions : PayPalClientOptions
{
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
  {
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
  };

  private readonly HttpClient _httpClient;
  private readonly TOptions _options;
  private readonly ILogger<PayPalOrdersClient<TOptions>> _logger;
  private readonly SemaphoreSlim _accessTokenGate = new(1, 1);
  private string? _accessToken;
  private DateTimeOffset _accessTokenExpiresAtUtc;

  public PayPalOrdersClient(
    HttpClient httpClient,
    IOptions<TOptions> options,
    ILogger<PayPalOrdersClient<TOptions>> logger)
  {
    _httpClient = httpClient;
    _options = options.Value;
    _logger = logger;
  }

  public async Task<PayPalOrderResult> CreateOrderAsync(
    PayPalCreateOrderRequest order,
    string idempotencyKey,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(order);
    if (order.PurchaseUnits.Count == 0)
    {
      throw new ArgumentException("At least one PayPal purchase unit is required.", nameof(order));
    }

    foreach (var unit in order.PurchaseUnits)
    {
      ValidateMoney(unit.Amount, requirePositive: true);
    }

    var requestId = PayPalRequestId.From(idempotencyKey);
    var token = await GetAccessTokenAsync(ct);
    using var request = CreateAuthorizedRequest(HttpMethod.Post, "/v2/checkout/orders", token);
    request.Headers.TryAddWithoutValidation("PayPal-Request-Id", requestId);
    request.Headers.TryAddWithoutValidation("Prefer", "return=representation");
    request.Content = JsonContent.Create(new
    {
      intent = string.IsNullOrWhiteSpace(order.Intent) ? "CAPTURE" : order.Intent.Trim().ToUpperInvariant(),
      payment_source = order.ExperienceContext is null
        ? null
        : new
        {
          paypal = new
          {
            experience_context = new
            {
              locale = EmptyToNull(order.ExperienceContext.Locale),
              shipping_preference = EmptyToNull(order.ExperienceContext.ShippingPreference)?.ToUpperInvariant(),
              user_action = EmptyToNull(order.ExperienceContext.UserAction)?.ToUpperInvariant(),
              landing_page = EmptyToNull(order.ExperienceContext.LandingPage)?.ToUpperInvariant()
            }
          }
        },
      purchase_units = order.PurchaseUnits.Select(unit => new
      {
        reference_id = EmptyToNull(unit.ReferenceId),
        description = EmptyToNull(unit.Description),
        custom_id = EmptyToNull(unit.CustomId),
        invoice_id = EmptyToNull(unit.InvoiceId),
        amount = new
        {
          currency_code = unit.Amount.Currency.Trim().ToUpperInvariant(),
          value = FormatAmount(unit.Amount.Value)
        }
      }).ToArray()
    }, options: JsonOptions);

    var root = await SendForJsonAsync(request, "create_order", ct);
    return MapOrder(root);
  }

  public async Task<PayPalOrderResult> GetOrderAsync(
    string orderId,
    CancellationToken ct = default)
  {
    var normalizedOrderId = RequireProviderId(orderId, nameof(orderId));
    var token = await GetAccessTokenAsync(ct);
    using var request = CreateAuthorizedRequest(
      HttpMethod.Get,
      $"/v2/checkout/orders/{Uri.EscapeDataString(normalizedOrderId)}",
      token);
    var root = await SendForJsonAsync(request, "get_order", ct);
    return MapOrder(root, normalizedOrderId);
  }

  public async Task<PayPalCaptureResult> CaptureOrderAsync(
    string orderId,
    string idempotencyKey,
    CancellationToken ct = default)
  {
    var normalizedOrderId = RequireProviderId(orderId, nameof(orderId));
    var requestId = PayPalRequestId.From(idempotencyKey);
    var token = await GetAccessTokenAsync(ct);
    using var request = CreateAuthorizedRequest(
      HttpMethod.Post,
      $"/v2/checkout/orders/{Uri.EscapeDataString(normalizedOrderId)}/capture",
      token);
    request.Headers.TryAddWithoutValidation("PayPal-Request-Id", requestId);
    request.Headers.TryAddWithoutValidation("Prefer", "return=representation");
    request.Content = JsonContent.Create(new { }, options: JsonOptions);

    var root = await SendForJsonAsync(request, "capture_order", ct);
    var mapped = MapOrder(root, normalizedOrderId);
    var capture = mapped.Captures.FirstOrDefault();
    if (capture is null)
    {
      throw CreateMalformedResponseException("capture_order", "CAPTURE_MISSING");
    }

    return capture;
  }

  public async Task<PayPalRefundResult> RefundCaptureAsync(
    string captureId,
    PayPalRefundRequest refund,
    string idempotencyKey,
    CancellationToken ct = default)
  {
    var normalizedCaptureId = RequireProviderId(captureId, nameof(captureId));
    ArgumentNullException.ThrowIfNull(refund);
    if (refund.Amount is not null)
    {
      ValidateMoney(refund.Amount, requirePositive: true);
    }

    var requestId = PayPalRequestId.From(idempotencyKey);
    var token = await GetAccessTokenAsync(ct);
    using var request = CreateAuthorizedRequest(
      HttpMethod.Post,
      $"/v2/payments/captures/{Uri.EscapeDataString(normalizedCaptureId)}/refund",
      token);
    request.Headers.TryAddWithoutValidation("PayPal-Request-Id", requestId);
    request.Headers.TryAddWithoutValidation("Prefer", "return=representation");
    request.Content = JsonContent.Create(new
    {
      amount = refund.Amount is null
        ? null
        : new
        {
          currency_code = refund.Amount.Currency.Trim().ToUpperInvariant(),
          value = FormatAmount(refund.Amount.Value)
        },
      invoice_id = EmptyToNull(refund.InvoiceId),
      note_to_payer = EmptyToNull(refund.NoteToPayer)
    }, options: JsonOptions);

    var root = await SendForJsonAsync(request, "refund_capture", ct);
    return MapRefund(root);
  }

  public async Task<PayPalRefundResult> GetRefundAsync(
    string refundId,
    CancellationToken ct = default)
  {
    var normalizedRefundId = RequireProviderId(refundId, nameof(refundId));
    var token = await GetAccessTokenAsync(ct);
    using var request = CreateAuthorizedRequest(
      HttpMethod.Get,
      $"/v2/payments/refunds/{Uri.EscapeDataString(normalizedRefundId)}",
      token);
    var root = await SendForJsonAsync(request, "get_refund", ct);
    var refund = MapRefund(root);
    if (string.IsNullOrWhiteSpace(refund.RefundId))
    {
      refund.RefundId = normalizedRefundId;
    }
    return refund;
  }

  public async Task<PayPalWebhookVerificationResult> VerifyWebhookSignatureAsync(
    PayPalWebhookSignature signature,
    string webhookEventJson,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(signature);
    EnsureWebhookConfigured();
    ValidateWebhookSignature(signature);
    if (string.IsNullOrWhiteSpace(webhookEventJson))
    {
      throw new ArgumentException("The original PayPal webhook body is required.", nameof(webhookEventJson));
    }

    JsonElement webhookEvent;
    try
    {
      using var eventDocument = JsonDocument.Parse(webhookEventJson);
      webhookEvent = eventDocument.RootElement.Clone();
    }
    catch (JsonException exception)
    {
      throw new ArgumentException("The PayPal webhook body is not valid JSON.", nameof(webhookEventJson), exception);
    }

    var token = await GetAccessTokenAsync(ct);
    using var request = CreateAuthorizedRequest(
      HttpMethod.Post,
      "/v1/notifications/verify-webhook-signature",
      token);
    request.Content = JsonContent.Create(new
    {
      auth_algo = signature.AuthenticationAlgorithm.Trim(),
      cert_url = signature.CertificateUrl.Trim(),
      transmission_id = signature.TransmissionId.Trim(),
      transmission_sig = signature.TransmissionSignature.Trim(),
      transmission_time = signature.TransmissionTime.Trim(),
      webhook_id = _options.PayPalWebhookId.Trim(),
      webhook_event = webhookEvent
    }, options: JsonOptions);

    var root = await SendForJsonAsync(request, "verify_webhook", ct);
    return new PayPalWebhookVerificationResult
    {
      VerificationStatus = ReadString(root, "verification_status")
    };
  }

  private async Task<string> GetAccessTokenAsync(CancellationToken ct)
  {
    EnsureConfigured("oauth");
    if (HasFreshAccessToken())
    {
      return _accessToken!;
    }

    await _accessTokenGate.WaitAsync(ct);
    try
    {
      if (HasFreshAccessToken())
      {
        return _accessToken!;
      }

      using var request = new HttpRequestMessage(
        HttpMethod.Post,
        new Uri(_options.PayPalBaseUri, "/v1/oauth2/token"));
      var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes(
        $"{_options.PayPalClientId}:{_options.PayPalClientSecret}"));
      request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
      request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
      {
        ["grant_type"] = "client_credentials"
      });

      var root = await SendForJsonAsync(request, "oauth", ct);
      _accessToken = ReadString(root, "access_token");
      if (string.IsNullOrWhiteSpace(_accessToken))
      {
        throw CreateMalformedResponseException("oauth", "ACCESS_TOKEN_MISSING");
      }

      var expiresIn = root.TryGetProperty("expires_in", out var expires)
        && expires.TryGetInt32(out var parsedExpires)
          ? parsedExpires
          : 300;
      _accessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(expiresIn, 60));
      return _accessToken;
    }
    finally
    {
      _accessTokenGate.Release();
    }
  }

  private async Task<JsonElement> SendForJsonAsync(
    HttpRequestMessage request,
    string operation,
    CancellationToken ct)
  {
    try
    {
      using var response = await _httpClient.SendAsync(request, ct);
      var body = await response.Content.ReadAsStringAsync(ct);
      if (!response.IsSuccessStatusCode)
      {
        throw CreateApiException(operation, (int)response.StatusCode, body);
      }

      try
      {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
      }
      catch (JsonException exception)
      {
        _logger.LogWarning(
          "PayPal {Operation} returned malformed JSON with HTTP status {StatusCode}.",
          operation,
          (int)response.StatusCode);
        throw new PayPalClientException(
          operation,
          "MALFORMED_RESPONSE",
          (int)response.StatusCode,
          debugId: null,
          isAlreadyCaptured: false,
          isTransient: false,
          exception);
      }
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
      throw;
    }
    catch (OperationCanceledException exception)
    {
      _logger.LogWarning("PayPal {Operation} timed out.", operation);
      throw new PayPalClientException(
        operation,
        "TIMEOUT",
        statusCode: null,
        debugId: null,
        isAlreadyCaptured: false,
        isTransient: true,
        exception);
    }
    catch (PayPalClientException)
    {
      throw;
    }
    catch (HttpRequestException exception)
    {
      _logger.LogWarning(
        "PayPal {Operation} transport failed. HTTP status {StatusCode}.",
        operation,
        exception.StatusCode is null ? null : (int)exception.StatusCode);
      throw new PayPalClientException(
        operation,
        "TRANSPORT_FAILURE",
        exception.StatusCode is null ? null : (int)exception.StatusCode,
        debugId: null,
        isAlreadyCaptured: false,
        isTransient: true,
        exception);
    }
  }

  private PayPalClientException CreateApiException(string operation, int statusCode, string body)
  {
    var (providerCode, debugId, issues) = ReadProviderError(body);
    var isAlreadyCaptured = issues.Any(IsAlreadyCapturedIssue)
      || IsAlreadyCapturedIssue(providerCode);
    var isTransient = statusCode is 408 or 409 or 425 or 429 || statusCode >= 500;

    _logger.LogWarning(
      "PayPal {Operation} failed with HTTP status {StatusCode}, provider code {ProviderCode}, issues {Issues}, debug id {DebugId}.",
      operation,
      statusCode,
      providerCode,
      issues.Count == 0 ? "<none>" : string.Join(',', issues),
      string.IsNullOrWhiteSpace(debugId) ? "<none>" : debugId);

    return new PayPalClientException(
      operation,
      providerCode,
      statusCode,
      debugId,
      isAlreadyCaptured,
      isTransient,
      providerIssueCodes: issues);
  }

  private HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string path, string token)
  {
    var request = new HttpRequestMessage(method, new Uri(_options.PayPalBaseUri, path));
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    return request;
  }

  private bool HasFreshAccessToken()
    => !string.IsNullOrWhiteSpace(_accessToken)
      && _accessTokenExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1);

  private void EnsureConfigured(string operation)
  {
    if (_options.IsPayPalConfigured)
    {
      return;
    }

    throw new PayPalClientException(
      operation,
      "NOT_CONFIGURED",
      statusCode: null,
      debugId: null,
      isAlreadyCaptured: false,
      isTransient: false);
  }

  private void EnsureWebhookConfigured()
  {
    EnsureConfigured("verify_webhook");
    if (_options.IsWebhookVerificationConfigured)
    {
      return;
    }

    throw new PayPalClientException(
      "verify_webhook",
      "WEBHOOK_NOT_CONFIGURED",
      statusCode: null,
      debugId: null,
      isAlreadyCaptured: false,
      isTransient: false);
  }

  private static PayPalOrderResult MapOrder(JsonElement root, string fallbackOrderId = "")
  {
    var orderId = ReadString(root, "id");
    if (string.IsNullOrWhiteSpace(orderId))
    {
      orderId = fallbackOrderId;
    }

    var orderStatus = ReadString(root, "status");
    var payer = MapPayer(root);
    var units = new List<PayPalPurchaseUnitResult>();
    if (root.TryGetProperty("purchase_units", out var purchaseUnits)
        && purchaseUnits.ValueKind == JsonValueKind.Array)
    {
      foreach (var unitElement in purchaseUnits.EnumerateArray())
      {
        var referenceId = ReadString(unitElement, "reference_id");
        var customId = ReadString(unitElement, "custom_id");
        var invoiceId = ReadString(unitElement, "invoice_id");
        var captures = new List<PayPalCaptureResult>();
        if (unitElement.TryGetProperty("payments", out var payments)
            && payments.TryGetProperty("captures", out var captureElements)
            && captureElements.ValueKind == JsonValueKind.Array)
        {
          foreach (var captureElement in captureElements.EnumerateArray())
          {
            var captureId = ReadString(captureElement, "id");
            if (string.IsNullOrWhiteSpace(captureId))
            {
              continue;
            }

            captures.Add(new PayPalCaptureResult
            {
              OrderId = orderId,
              OrderStatus = orderStatus,
              CaptureId = captureId,
              Status = ReadString(captureElement, "status"),
              StatusReason = ReadNestedString(captureElement, "status_details", "reason"),
              ReferenceId = referenceId,
              CustomId = customId,
              InvoiceId = invoiceId,
              Amount = MapMoneyProperty(captureElement, "amount"),
              SellerReceivableBreakdown = MapReceivableBreakdown(captureElement),
              Payer = payer
            });
          }
        }

        units.Add(new PayPalPurchaseUnitResult
        {
          ReferenceId = referenceId,
          CustomId = customId,
          InvoiceId = invoiceId,
          Amount = MapMoneyProperty(unitElement, "amount"),
          Captures = captures
        });
      }
    }

    return new PayPalOrderResult
    {
      OrderId = orderId,
      Status = orderStatus,
      Payer = payer,
      PurchaseUnits = units
    };
  }

  private static PayPalRefundResult MapRefund(JsonElement root)
    => new()
    {
      RefundId = ReadString(root, "id"),
      CaptureId = ReadRefundCaptureId(root),
      Status = ReadString(root, "status"),
      Amount = MapMoneyProperty(root, "amount"),
      SellerPayableBreakdown = MapPayableBreakdown(root)
    };

  private static string ReadRefundCaptureId(JsonElement refund)
  {
    if (refund.TryGetProperty("supplementary_data", out var supplementary)
        && supplementary.ValueKind == JsonValueKind.Object
        && supplementary.TryGetProperty("related_ids", out var relatedIds)
        && relatedIds.ValueKind == JsonValueKind.Object)
    {
      var captureId = ReadString(relatedIds, "capture_id");
      if (!string.IsNullOrWhiteSpace(captureId))
      {
        return captureId;
      }
    }

    if (!refund.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array)
    {
      return string.Empty;
    }

    foreach (var link in links.EnumerateArray())
    {
      if (!string.Equals(ReadString(link, "rel"), "up", StringComparison.OrdinalIgnoreCase)
          || !Uri.TryCreate(ReadString(link, "href"), UriKind.Absolute, out var uri))
      {
        continue;
      }

      const string capturePath = "/v2/payments/captures/";
      var index = uri.AbsolutePath.IndexOf(capturePath, StringComparison.OrdinalIgnoreCase);
      if (index < 0)
      {
        continue;
      }

      var encodedId = uri.AbsolutePath[(index + capturePath.Length)..].Trim('/');
      if (!string.IsNullOrWhiteSpace(encodedId) && !encodedId.Contains('/'))
      {
        return Uri.UnescapeDataString(encodedId);
      }
    }

    return string.Empty;
  }

  private static PayPalSellerReceivableBreakdown? MapReceivableBreakdown(JsonElement capture)
  {
    if (!capture.TryGetProperty("seller_receivable_breakdown", out var breakdown)
        || breakdown.ValueKind != JsonValueKind.Object)
    {
      return null;
    }

    return new PayPalSellerReceivableBreakdown
    {
      GrossAmount = MapMoneyProperty(breakdown, "gross_amount"),
      PayPalFee = MapMoneyProperty(breakdown, "paypal_fee"),
      NetAmount = MapMoneyProperty(breakdown, "net_amount")
    };
  }

  private static PayPalSellerPayableBreakdown? MapPayableBreakdown(JsonElement refund)
  {
    if (!refund.TryGetProperty("seller_payable_breakdown", out var breakdown)
        || breakdown.ValueKind != JsonValueKind.Object)
    {
      return null;
    }

    return new PayPalSellerPayableBreakdown
    {
      GrossAmount = MapMoneyProperty(breakdown, "gross_amount"),
      PayPalFee = MapMoneyProperty(breakdown, "paypal_fee"),
      NetAmount = MapMoneyProperty(breakdown, "net_amount"),
      TotalRefundedAmount = MapMoneyProperty(breakdown, "total_refunded_amount")
    };
  }

  private static PayPalPayer MapPayer(JsonElement root)
  {
    if (TryGetPaymentSourcePayPal(root, out var paypal))
    {
      return new PayPalPayer
      {
        Name = ReadName(paypal),
        Email = ReadString(paypal, "email_address"),
        Phone = ReadPhone(paypal)
      };
    }

    if (root.TryGetProperty("payer", out var payer))
    {
      return new PayPalPayer
      {
        Name = ReadName(payer),
        Email = ReadString(payer, "email_address"),
        Phone = ReadPhone(payer)
      };
    }

    return new PayPalPayer { Name = ReadShippingName(root) };
  }

  private static PayPalMoney MapMoneyProperty(JsonElement parent, string propertyName)
  {
    if (!parent.TryGetProperty(propertyName, out var money)
        || money.ValueKind != JsonValueKind.Object)
    {
      return new PayPalMoney();
    }

    var rawValue = ReadString(money, "value");
    return new PayPalMoney
    {
      Currency = ReadString(money, "currency_code"),
      Value = decimal.TryParse(
        rawValue,
        NumberStyles.Number,
        CultureInfo.InvariantCulture,
        out var parsed)
          ? parsed
          : 0m
    };
  }

  private static (string ProviderCode, string? DebugId, IReadOnlyList<string> Issues) ReadProviderError(string body)
  {
    const string fallbackCode = "HTTP_ERROR";
    if (string.IsNullOrWhiteSpace(body))
    {
      return (fallbackCode, null, Array.Empty<string>());
    }

    try
    {
      using var document = JsonDocument.Parse(body);
      var root = document.RootElement;
      var code = ReadString(root, "name");
      if (string.IsNullOrWhiteSpace(code))
      {
        code = ReadString(root, "error");
      }

      var issues = new List<string>();
      if (root.TryGetProperty("details", out var details)
          && details.ValueKind == JsonValueKind.Array)
      {
        foreach (var detail in details.EnumerateArray())
        {
          var issue = ReadString(detail, "issue");
          if (!string.IsNullOrWhiteSpace(issue))
          {
            issues.Add(issue);
          }
        }
      }

      return (
        string.IsNullOrWhiteSpace(code) ? fallbackCode : code,
        EmptyToNull(ReadString(root, "debug_id")),
        issues);
    }
    catch (JsonException)
    {
      return (fallbackCode, null, Array.Empty<string>());
    }
  }

  private static string ReadName(JsonElement parent)
  {
    if (!parent.TryGetProperty("name", out var name))
    {
      return string.Empty;
    }

    if (name.ValueKind == JsonValueKind.String)
    {
      return name.GetString()?.Trim() ?? string.Empty;
    }

    var fullName = ReadString(name, "full_name");
    if (!string.IsNullOrWhiteSpace(fullName))
    {
      return fullName;
    }

    return string.Join(
      " ",
      new[] { ReadString(name, "given_name"), ReadString(name, "surname") }
        .Where(part => !string.IsNullOrWhiteSpace(part)));
  }

  private static string ReadPhone(JsonElement parent)
  {
    if (parent.TryGetProperty("phone_number", out var directPhone))
    {
      var direct = ReadPhoneValue(directPhone);
      if (!string.IsNullOrWhiteSpace(direct))
      {
        return direct;
      }
    }

    if (!parent.TryGetProperty("phone", out var phone))
    {
      return string.Empty;
    }

    var value = ReadPhoneValue(phone);
    return !string.IsNullOrWhiteSpace(value) || !phone.TryGetProperty("phone_number", out var nested)
      ? value
      : ReadPhoneValue(nested);
  }

  private static string ReadPhoneValue(JsonElement phone)
  {
    if (phone.ValueKind == JsonValueKind.String)
    {
      return phone.GetString()?.Trim() ?? string.Empty;
    }

    if (phone.ValueKind != JsonValueKind.Object)
    {
      return string.Empty;
    }

    var number = ReadString(phone, "national_number");
    if (string.IsNullOrWhiteSpace(number))
    {
      number = ReadString(phone, "number");
    }

    if (string.IsNullOrWhiteSpace(number) || number.StartsWith('+'))
    {
      return number;
    }

    var countryCode = ReadString(phone, "country_code").TrimStart('+');
    return string.IsNullOrWhiteSpace(countryCode) ? number : $"+{countryCode} {number}";
  }

  private static string ReadShippingName(JsonElement root)
  {
    if (!root.TryGetProperty("purchase_units", out var units)
        || units.ValueKind != JsonValueKind.Array)
    {
      return string.Empty;
    }

    foreach (var unit in units.EnumerateArray())
    {
      if (unit.TryGetProperty("shipping", out var shipping))
      {
        var name = ReadName(shipping);
        if (!string.IsNullOrWhiteSpace(name))
        {
          return name;
        }
      }
    }

    return string.Empty;
  }

  private static bool TryGetPaymentSourcePayPal(JsonElement root, out JsonElement paypal)
  {
    paypal = default;
    return root.TryGetProperty("payment_source", out var paymentSource)
      && paymentSource.TryGetProperty("paypal", out paypal);
  }

  private static string ReadString(JsonElement parent, string propertyName)
    => parent.ValueKind == JsonValueKind.Object
      && parent.TryGetProperty(propertyName, out var property)
      && property.ValueKind == JsonValueKind.String
        ? property.GetString()?.Trim() ?? string.Empty
        : string.Empty;

  private static string ReadNestedString(JsonElement parent, string objectName, string propertyName)
    => parent.TryGetProperty(objectName, out var nested)
      ? ReadString(nested, propertyName)
      : string.Empty;

  private static string RequireProviderId(string value, string parameterName)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      throw new ArgumentException("A PayPal provider identifier is required.", parameterName);
    }

    return value.Trim();
  }

  private static void ValidateMoney(PayPalMoney money, bool requirePositive)
  {
    ArgumentNullException.ThrowIfNull(money);
    if (string.IsNullOrWhiteSpace(money.Currency) || money.Currency.Trim().Length != 3)
    {
      throw new ArgumentException("PayPal currency must be a three-letter code.", nameof(money));
    }

    if (requirePositive && money.Value <= 0m)
    {
      throw new ArgumentOutOfRangeException(nameof(money), "PayPal amount must be greater than zero.");
    }
  }

  private static void ValidateWebhookSignature(PayPalWebhookSignature signature)
  {
    if (string.IsNullOrWhiteSpace(signature.TransmissionId)
        || string.IsNullOrWhiteSpace(signature.TransmissionTime)
        || string.IsNullOrWhiteSpace(signature.TransmissionSignature)
        || string.IsNullOrWhiteSpace(signature.CertificateUrl)
        || string.IsNullOrWhiteSpace(signature.AuthenticationAlgorithm))
    {
      throw new ArgumentException("All PayPal webhook signature headers are required.", nameof(signature));
    }
  }

  private static bool IsAlreadyCapturedIssue(string? value)
    => string.Equals(value, "PAYMENT_ALREADY_DONE", StringComparison.OrdinalIgnoreCase)
      || string.Equals(value, "ORDER_ALREADY_CAPTURED", StringComparison.OrdinalIgnoreCase);

  private static PayPalClientException CreateMalformedResponseException(string operation, string code)
    => new(
      operation,
      code,
      statusCode: null,
      debugId: null,
      isAlreadyCaptured: false,
      isTransient: false);

  private static string FormatAmount(decimal value)
    => value.ToString("0.00", CultureInfo.InvariantCulture);

  private static string? EmptyToNull(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
