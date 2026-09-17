using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Payments.Clip;

namespace OrionERP.Infrastructure.Features.Payments.Clip;

/// <summary>
/// Transporte REST de la API de pagos de Clip. La pertenencia del intento y el
/// empate con la cotización los impone el adaptador de dominio que llama, no
/// esta clase.
/// </summary>
public sealed class ClipPaymentsClient<TOptions> : IClipPaymentsClient
  where TOptions : ClipClientOptions
{
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
  {
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
  };

  /// <summary>
  /// Códigos con los que Clip rechaza una solicitud antes de tocar la tarjeta.
  /// Sólo con estos podemos afirmar que no hubo cargo; cualquier otro desenlace
  /// es desconocido y se resuelve consultando.
  /// </summary>
  private static readonly HttpStatusCode[] DefinitiveRejections =
  [
    HttpStatusCode.BadRequest,
    HttpStatusCode.Unauthorized,
    HttpStatusCode.Forbidden,
    HttpStatusCode.UnprocessableEntity
  ];

  private readonly HttpClient _httpClient;
  private readonly TOptions _options;
  private readonly ILogger<ClipPaymentsClient<TOptions>> _logger;

  public ClipPaymentsClient(
    HttpClient httpClient,
    IOptions<TOptions> options,
    ILogger<ClipPaymentsClient<TOptions>> logger)
  {
    _httpClient = httpClient;
    _options = options.Value;
    _logger = logger;
  }

  public async Task<ClipPaymentResult> CreatePaymentAsync(
    ClipPaymentRequest request,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    EnsureConfigured("create_payment");
    if (string.IsNullOrWhiteSpace(request.CardTokenId))
      throw new ArgumentException("El token de tarjeta es obligatorio.", nameof(request));
    if (request.Amount <= 0 || decimal.Round(request.Amount, 2) != request.Amount)
      throw new ArgumentException("El monto debe ser positivo y con dos decimales.", nameof(request));
    if (string.IsNullOrWhiteSpace(request.ExternalReference)
        || request.ExternalReference.Length > ClipExternalReference.MaximumLength)
      throw new ArgumentException("La referencia externa es obligatoria y admite 36 caracteres.", nameof(request));

    using var message = CreateAuthorizedRequest(HttpMethod.Post, "/payments");
    message.Content = JsonContent.Create(new
    {
      amount = request.Amount,
      currency = request.Currency.Trim().ToUpperInvariant(),
      description = EmptyToNull(Truncate(request.Description, 255)),
      external_reference = request.ExternalReference.Trim(),
      capture_method = EmptyToNull(request.CaptureMethod)?.ToLowerInvariant() ?? "automatic",
      installments = request.Installments <= 1 ? (int?)null : request.Installments,
      binary_mode = request.BinaryMode,
      webhook_url = EmptyToNull(request.WebhookUrl),
      payment_method = new { token = request.CardTokenId.Trim() },
      customer = new
      {
        first_name = EmptyToNull(Truncate(request.Customer.FirstName, 100)),
        last_name = EmptyToNull(Truncate(request.Customer.LastName, 100)),
        email = request.Customer.Email.Trim(),
        phone = request.Customer.Phone.Trim()
      }
    }, options: JsonOptions);

    // Sin reintentos y sin llave de idempotencia: repetir este POST cobraria dos
    // veces. Un desenlace desconocido se marca como tal y lo resuelve la
    // reconciliacion por external_reference.
    var root = await SendForJsonAsync(message, "create_payment", treatFailureAsUnknown: true, ct);
    return MapPayment(root);
  }

  public async Task<ClipPaymentResult> GetPaymentAsync(
    string paymentId,
    CancellationToken ct = default)
  {
    var normalized = RequireProviderId(paymentId, nameof(paymentId));
    EnsureConfigured("get_payment");
    using var message = CreateAuthorizedRequest(
      HttpMethod.Get,
      $"/payments/{Uri.EscapeDataString(normalized)}");
    var root = await SendForJsonAsync(message, "get_payment", treatFailureAsUnknown: false, ct);
    return MapPayment(root, normalized);
  }

  public async Task<IReadOnlyList<ClipPaymentResult>> ListPaymentsAsync(
    DateTimeOffset fromUtc,
    DateTimeOffset toUtc,
    CancellationToken ct = default)
  {
    if (toUtc <= fromUtc)
      throw new ArgumentException("El rango de fechas es inválido.", nameof(toUtc));
    EnsureConfigured("list_payments");

    var query = $"?from={Uri.EscapeDataString(FormatInstant(fromUtc))}&to={Uri.EscapeDataString(FormatInstant(toUtc))}";
    using var message = CreateAuthorizedRequest(HttpMethod.Get, $"/payments{query}");
    var root = await SendForJsonAsync(message, "list_payments", treatFailureAsUnknown: false, ct);

    var items = root.ValueKind switch
    {
      JsonValueKind.Array => root,
      JsonValueKind.Object => FirstArrayProperty(root),
      _ => default
    };
    if (items.ValueKind != JsonValueKind.Array)
      return Array.Empty<ClipPaymentResult>();

    var results = new List<ClipPaymentResult>();
    foreach (var item in items.EnumerateArray())
    {
      if (item.ValueKind == JsonValueKind.Object)
        results.Add(MapPayment(item));
    }
    return results;
  }

  public async Task<ClipRefundResult> RefundPaymentAsync(
    ClipRefundRequest request,
    string idempotencyKey,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    EnsureRefundsConfigured("refund_payment");
    var paymentId = RequireProviderId(request.PaymentId, nameof(request));
    if (request.Amount <= 0 || decimal.Round(request.Amount, 2) != request.Amount)
      throw new ArgumentException("El monto a reembolsar debe ser positivo y con dos decimales.", nameof(request));
    if (string.IsNullOrWhiteSpace(request.Reason))
      throw new ArgumentException("El motivo del reembolso es obligatorio.", nameof(request));

    using var message = CreateAuthorizedRequest(HttpMethod.Post, "/refunds", useBasicAuth: true);
    if (!string.IsNullOrWhiteSpace(idempotencyKey))
    {
      // La llave de Clip solo vive un minuto, asi que protege reintentos
      // inmediatos y nada mas; la proteccion durable es consultar AmountRefunded.
      message.Headers.TryAddWithoutValidation("idempotency-key", idempotencyKey.Trim());
    }
    message.Content = JsonContent.Create(new
    {
      amount = request.Amount,
      reason = Truncate(request.Reason.Trim(), 255),
      reference = new { type = "payment", id = paymentId }
    }, options: JsonOptions);

    var root = await SendForJsonAsync(message, "refund_payment", treatFailureAsUnknown: true, ct);
    return MapRefund(root, paymentId);
  }

  public async Task<ClipRefundResult> GetRefundAsync(
    string refundId,
    CancellationToken ct = default)
  {
    var normalized = RequireProviderId(refundId, nameof(refundId));
    EnsureRefundsConfigured("get_refund");
    using var message = CreateAuthorizedRequest(
      HttpMethod.Get,
      $"/refunds/{Uri.EscapeDataString(normalized)}",
      useBasicAuth: true);
    var root = await SendForJsonAsync(message, "get_refund", treatFailureAsUnknown: false, ct);
    return MapRefund(root, paymentId: null);
  }

  /// <summary>
  /// El esquema de autenticación depende del endpoint, y no es cosmético.
  /// <para>
  /// La API de pagos acepta los dos: <c>Bearer &lt;clave&gt;</c> y
  /// <c>Basic base64(clave:secreto)</c>. La de reembolsos <b>sólo acepta
  /// Basic</b>: con Bearer devuelve <c>401 Unauthorized</c> aun con credenciales
  /// Live. Verificado contra producción el 2026-09-17 con una lectura inocua —
  /// <c>GET /refunds/{id}</c> de un id inexistente devolvió 401 con Bearer y
  /// <c>404 CL1301 "Payment not found"</c> con Basic, o sea que Basic sí pasa el
  /// control de acceso y sólo falla por el recurso.
  /// </para>
  /// <para>
  /// El sandbox despistó: ahí ambos esquemas daban 401 en reembolsos, lo que
  /// hacía parecer que el problema eran las credenciales de prueba y no el
  /// esquema. Un 401 no distingue entre "credencial sin permiso" y "esquema
  /// equivocado"; hay que separarlos con una petición que pueda dar 404.
  /// </para>
  /// </summary>
  private HttpRequestMessage CreateAuthorizedRequest(
    HttpMethod method,
    string relativeUrl,
    bool useBasicAuth = false)
  {
    var message = new HttpRequestMessage(method, new Uri(_options.ClipBaseUri, relativeUrl));
    message.Headers.Authorization = useBasicAuth
      ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
          Encoding.UTF8.GetBytes($"{_options.ClipApiKey.Trim()}:{_options.ClipApiSecret.Trim()}")))
      : new AuthenticationHeaderValue("Bearer", _options.ClipApiKey.Trim());
    message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    return message;
  }

  /// <summary>
  /// Reembolsar necesita el secreto, no sólo la clave. Sin él, Basic saldría
  /// malformado y Clip respondería 401, que es indistinguible de un problema de
  /// permisos: mejor fallar aquí con una causa legible.
  /// </summary>
  private void EnsureRefundsConfigured(string operation)
  {
    EnsureConfigured(operation);
    if (!_options.AreRefundsConfigured)
    {
      throw new ClipClientException(
        operation,
        "REFUNDS_NOT_CONFIGURED",
        statusCode: null,
        isTransient: false,
        isOutcomeUnknown: false);
    }
  }

  private void EnsureConfigured(string operation)
  {
    if (!_options.IsClipConfigured)
    {
      throw new ClipClientException(
        operation,
        "NOT_CONFIGURED",
        statusCode: null,
        isTransient: false,
        isOutcomeUnknown: false);
    }
  }

  private async Task<JsonElement> SendForJsonAsync(
    HttpRequestMessage message,
    string operation,
    bool treatFailureAsUnknown,
    CancellationToken ct)
  {
    HttpResponseMessage response;
    try
    {
      response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
    {
      // La solicitud pudo haber llegado. Para un cobro eso es un desenlace
      // desconocido, nunca una falla limpia.
      throw new ClipClientException(
        operation,
        "TRANSPORT_FAILURE",
        statusCode: null,
        isTransient: !treatFailureAsUnknown,
        isOutcomeUnknown: treatFailureAsUnknown,
        exception);
    }

    using (response)
    {
      var payload = await response.Content.ReadAsStringAsync(ct);
      if (!response.IsSuccessStatusCode)
      {
        var providerCode = ReadProviderErrorCode(payload);
        var isDefinitive = DefinitiveRejections.Contains(response.StatusCode);
        _logger.LogWarning(
          "Clip {Operation} respondió {StatusCode} con código {ProviderErrorCode}.",
          operation,
          (int)response.StatusCode,
          providerCode);
        throw new ClipClientException(
          operation,
          providerCode,
          (int)response.StatusCode,
          isTransient: !treatFailureAsUnknown && !isDefinitive,
          isOutcomeUnknown: treatFailureAsUnknown && !isDefinitive);
      }

      try
      {
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 64 });
        return document.RootElement.Clone();
      }
      catch (JsonException exception)
      {
        throw new ClipClientException(
          operation,
          "INVALID_RESPONSE",
          (int)response.StatusCode,
          isTransient: false,
          isOutcomeUnknown: treatFailureAsUnknown,
          exception);
      }
    }
  }

  private static ClipPaymentResult MapPayment(JsonElement root, string? fallbackPaymentId = null)
  {
    var statusDetail = Child(root, "status_detail");
    var paymentMethod = Child(root, "payment_method");
    var card = Child(paymentMethod, "card");
    var pendingAction = Child(root, "pending_action");
    var pendingUrl = Text(pendingAction, "url");

    return new ClipPaymentResult
    {
      PaymentId = EmptyToNull(Text(root, "id")) ?? fallbackPaymentId ?? string.Empty,
      Status = Text(root, "status"),
      StatusCode = Text(statusDetail, "code"),
      StatusMessage = Text(statusDetail, "message"),
      Amount = Number(root, "amount"),
      AmountRefunded = Number(root, "amount_refunded"),
      Currency = EmptyToNull(Text(root, "currency")) ?? "MXN",
      ExternalReference = Text(root, "external_reference"),
      ReceiptNumber = Text(root, "receipt_no"),
      Installments = Integer(root, "installments") is var installments && installments > 0 ? installments : 1,
      Card = new ClipCard
      {
        Brand = Text(paymentMethod, "id"),
        Type = Text(paymentMethod, "type"),
        Issuer = Text(card, "issuer"),
        LastDigits = Text(card, "last_digits"),
        Country = Text(card, "country")
      },
      PendingAction = string.IsNullOrWhiteSpace(pendingUrl)
        ? null
        : new ClipPendingAction { Type = Text(pendingAction, "type"), Url = pendingUrl },
      CreatedAtUtc = Instant(root, "created_at"),
      ApprovedAtUtc = Instant(root, "approved_at")
    };
  }

  private static ClipRefundResult MapRefund(JsonElement root, string? paymentId)
  {
    var reference = Child(root, "reference");
    return new ClipRefundResult
    {
      RefundId = Text(root, "id"),
      PaymentId = EmptyToNull(Text(reference, "id")) ?? paymentId ?? string.Empty,
      Status = Text(root, "status"),
      StatusMessage = Text(root, "status_message"),
      Amount = Number(root, "amount"),
      Currency = EmptyToNull(Text(root, "currency")) ?? "MXN",
      ReceiptNumber = Text(root, "receipt_no"),
      CreatedAtUtc = Instant(root, "created_at")
    };
  }

  private static string ReadProviderErrorCode(string payload)
  {
    if (string.IsNullOrWhiteSpace(payload)) return "UNKNOWN";
    try
    {
      using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
      var root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Object) return "UNKNOWN";
      foreach (var name in new[] { "code", "error_code", "status_code" })
      {
        var value = Text(root, name);
        if (!string.IsNullOrWhiteSpace(value)) return value;
      }
      var error = Child(root, "error");
      var nested = Text(error, "code");
      return string.IsNullOrWhiteSpace(nested) ? "UNKNOWN" : nested;
    }
    catch (JsonException)
    {
      return "UNKNOWN";
    }
  }

  private static JsonElement FirstArrayProperty(JsonElement root)
  {
    foreach (var name in new[] { "data", "payments", "items", "results" })
    {
      if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
        return value;
    }
    return default;
  }

  private static JsonElement Child(JsonElement parent, string name)
    => parent.ValueKind == JsonValueKind.Object
      && parent.TryGetProperty(name, out var value)
      && value.ValueKind == JsonValueKind.Object
        ? value
        : default;

  private static string Text(JsonElement parent, string name)
    => parent.ValueKind == JsonValueKind.Object
      && parent.TryGetProperty(name, out var value)
      && value.ValueKind == JsonValueKind.String
        ? value.GetString()?.Trim() ?? string.Empty
        : string.Empty;

  private static decimal Number(JsonElement parent, string name)
  {
    if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
      return 0m;
    return value.ValueKind switch
    {
      JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
      JsonValueKind.String when decimal.TryParse(
        value.GetString(),
        NumberStyles.Number,
        CultureInfo.InvariantCulture,
        out var parsed) => parsed,
      _ => 0m
    };
  }

  private static int Integer(JsonElement parent, string name)
  {
    if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
      return 0;
    return value.ValueKind switch
    {
      JsonValueKind.Number when value.TryGetInt32(out var number) => number,
      JsonValueKind.String when int.TryParse(
        value.GetString(),
        NumberStyles.Integer,
        CultureInfo.InvariantCulture,
        out var parsed) => parsed,
      _ => 0
    };
  }

  private static DateTimeOffset? Instant(JsonElement parent, string name)
  {
    var text = Text(parent, name);
    return DateTimeOffset.TryParse(
      text,
      CultureInfo.InvariantCulture,
      DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
      out var parsed)
        ? parsed
        : null;
  }

  private static string FormatInstant(DateTimeOffset value)
    => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);

  private static string RequireProviderId(string? value, string parameterName)
  {
    var normalized = value?.Trim();
    if (string.IsNullOrEmpty(normalized) || normalized.Length > 64)
      throw new ArgumentException("El identificador de Clip es inválido.", parameterName);
    return normalized;
  }

  private static string? EmptyToNull(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private static string Truncate(string? value, int maximumLength)
  {
    var normalized = value?.Trim() ?? string.Empty;
    return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
  }
}
