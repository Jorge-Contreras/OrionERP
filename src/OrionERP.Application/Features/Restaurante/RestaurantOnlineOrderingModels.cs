using System.ComponentModel.DataAnnotations;

namespace OrionERP.Application.Features.Restaurante;

public static class RestaurantOnlineCheckoutStatuses
{
  public const string Quoted = "Quoted";
  /// <summary>Un cargo está en vuelo. Es exclusivo: nadie más puede cobrar este intento.</summary>
  public const string ChargePending = "ChargePending";
  /// <summary>Clip pidió 3DS y el cliente está autenticándose.</summary>
  public const string Authenticating3ds = "Authenticating3ds";
  /// <summary>
  /// El cargo no dejó una respuesta utilizable. Sólo la recuperación lo resuelve,
  /// consultando: repetir el cargo cobraría dos veces.
  /// </summary>
  public const string ChargeUnknown = "ChargeUnknown";
  public const string Captured = "Captured";
  public const string PosCreated = "PosCreated";
  public const string RequoteRequired = "RequoteRequired";
  public const string PaymentDenied = "PaymentDenied";
  public const string CapturedNeedsOrder = "CapturedNeedsOrder";
  public const string RefundRequested = "RefundRequested";
  public const string RefundPending = "RefundPending";
  public const string Refunded = "Refunded";
  public const string Expired = "Expired";
  public const string Failed = "Failed";
}

public static class RestaurantPaymentGatewayProviders
{
  public const string Clip = "Clip";
  /// <summary>Sólo para filas históricas de sitios que cobraron con este proveedor.</summary>
  public const string PayPal = "PayPal";
}

public sealed class RestaurantOnlineOrderingConfigurationDto
{
  public long PublicSiteId { get; set; }
  public string PublicSiteKey { get; set; } = string.Empty;
  public bool IsEnabled { get; set; }
  public bool IsPaused { get; set; }
  public string? PauseMessage { get; set; }
  public bool CanAcceptOrders { get; set; }
  public string? UnavailableReason { get; set; }
  public bool AllowGuestCheckout { get; set; } = true;
  public bool PickupEnabled { get; set; } = true;
  public decimal MaximumOrderAmount { get; set; } = 2500m;
  public string OnlineHoursJson { get; set; } = "{}";
  public string Currency { get; set; } = "MXN";
  /// <summary>Clave pública del SDK de Clip. No es secreta: el navegador la necesita para tokenizar.</summary>
  public string ClipApiKey { get; set; } = string.Empty;
  public string ClipLocale { get; set; } = "es";
  public string TermsVersion { get; set; } = string.Empty;
  public string PrivacyVersion { get; set; } = string.Empty;
  public string AllergenDisclaimer { get; set; } = string.Empty;
  public DateTime? ProcessorHeartbeatAtUtc { get; set; }
}

public sealed class RestaurantOnlineQuoteRequest
{
  [MinLength(1)] public List<RestaurantOnlineCartLineRequest> Lines { get; set; } = [];
  [StringLength(32)] public string? PromotionCode { get; set; }
}

public sealed class RestaurantOnlineCartLineRequest
{
  [Range(1, long.MaxValue)] public long ProductId { get; set; }
  [Range(1, long.MaxValue)] public long MenuSectionId { get; set; }
  [Range(typeof(decimal), "0.0001", "999999")] public decimal Quantity { get; set; } = 1;
  [StringLength(500)] public string? Notes { get; set; }
  public List<long> ModifierOptionIds { get; set; } = [];
  public List<RestaurantOnlineComboSelectionRequest> ComboSelections { get; set; } = [];
}

public sealed class RestaurantOnlineComboSelectionRequest
{
  [Range(1, long.MaxValue)] public long ComboSlotId { get; set; }
  [Range(1, long.MaxValue)] public long ComboSlotOptionId { get; set; }
  public List<long> ModifierOptionIds { get; set; } = [];
  [StringLength(500)] public string? Notes { get; set; }
}

public sealed class RestaurantOnlineQuoteResult
{
  public bool Succeeded { get; set; }
  public string Code { get; set; } = string.Empty;
  public string Message { get; set; } = string.Empty;
  public string? QuoteToken { get; set; }
  public string? Fingerprint { get; set; }
  public DateTime? ExpiresAtUtc { get; set; }
  public string Currency { get; set; } = "MXN";
  public decimal Subtotal { get; set; }
  public decimal PromotionDiscount { get; set; }
  public decimal Tax { get; set; }
  public decimal Total { get; set; }
  public IReadOnlyList<RestaurantOnlineQuoteLineDto> Lines { get; set; } = Array.Empty<RestaurantOnlineQuoteLineDto>();
  public IReadOnlyList<RestaurantPromotionAdjustmentDto> Promotions { get; set; } = Array.Empty<RestaurantPromotionAdjustmentDto>();

  public static RestaurantOnlineQuoteResult Fail(string code, string message)
    => new() { Code = code, Message = message };
}

public sealed class RestaurantOnlineQuoteLineDto
{
  public int Index { get; set; }
  public long ProductId { get; set; }
  public long MenuSectionId { get; set; }
  public string ProductName { get; set; } = string.Empty;
  public decimal Quantity { get; set; }
  public decimal UnitPrice { get; set; }
  public decimal DiscountAmount { get; set; }
  public decimal TaxAmount { get; set; }
  public decimal Total { get; set; }
  public string? Notes { get; set; }
  public IReadOnlyList<string> Modifiers { get; set; } = Array.Empty<string>();
  public IReadOnlyList<string> ComboSelections { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Reserva el intento de cobro. No hay llamada a Clip en este paso: el pago no
/// existe hasta el cargo, así que lo único que se fija aquí es a quién pertenece
/// el intento y qué cotización acepta.
/// </summary>
public sealed class RestaurantOnlineCheckoutBeginRequest
{
  [Required] public string QuoteToken { get; set; } = string.Empty;
  public Guid ClientAttemptId { get; set; }
  [Required, StringLength(150)] public string CustomerName { get; set; } = string.Empty;
  [Required, EmailAddress, StringLength(256)] public string CustomerEmail { get; set; } = string.Empty;
  [Required, Phone, StringLength(30)] public string CustomerPhone { get; set; } = string.Empty;
  public bool TermsAccepted { get; set; }
  [Required, StringLength(30)] public string TermsVersion { get; set; } = string.Empty;
  [Required, StringLength(30)] public string PrivacyVersion { get; set; } = string.Empty;
}

public sealed class RestaurantOnlineCheckoutBeginResult
{
  public bool Succeeded { get; set; }
  public string Code { get; set; } = string.Empty;
  public string Message { get; set; } = string.Empty;
  public string? TrackingToken { get; set; }
  public string CheckoutStatus { get; set; } = RestaurantOnlineCheckoutStatuses.Quoted;
  public bool WasExisting { get; set; }

  public static RestaurantOnlineCheckoutBeginResult Fail(string code, string message)
    => new() { Code = code, Message = message };
}

public sealed class RestaurantOnlineChargeRequest
{
  [Required] public string QuoteToken { get; set; } = string.Empty;
  public Guid ClientAttemptId { get; set; }
  [Required] public string TrackingToken { get; set; } = string.Empty;
  /// <summary>Token del SDK de Clip. De un solo uso y con 15 minutos de vigencia.</summary>
  [Required, StringLength(100)] public string CardTokenId { get; set; } = string.Empty;
}

/// <summary>Cierre del cargo después de que el cliente completó la autenticación 3DS.</summary>
public sealed class RestaurantOnlineChargeConfirmRequest
{
  public Guid ClientAttemptId { get; set; }
  [Required] public string TrackingToken { get; set; } = string.Empty;
  [Required, StringLength(64)] public string PaymentId { get; set; } = string.Empty;
}

public sealed class RestaurantOnlineChargeResult
{
  public bool Succeeded { get; set; }
  public string Code { get; set; } = string.Empty;
  public string Message { get; set; } = string.Empty;
  public string CheckoutStatus { get; set; } = string.Empty;
  public string? TrackingToken { get; set; }
  public int? OrderFolio { get; set; }
  public bool PaymentCaptured { get; set; }
  public bool IsPending { get; set; }
  /// <summary>URL que el navegador debe abrir en un iframe para autenticar con 3DS.</summary>
  public string? ThreeDSecureUrl { get; set; }

  public static RestaurantOnlineChargeResult Fail(string code, string message)
    => new() { Code = code, Message = message };
}

public sealed class RestaurantOnlineCheckoutStatusDto
{
  public string CheckoutStatus { get; set; } = string.Empty;
  public string PaymentStatus { get; set; } = string.Empty;
  public string? OrderStatus { get; set; }
  public int? OrderFolio { get; set; }
  public decimal Total { get; set; }
  public string Currency { get; set; } = "MXN";
  public bool IsTerminal { get; set; }
  public string Message { get; set; } = string.Empty;
  public DateTime UpdatedAtUtc { get; set; }
  /// <summary>Status of the durable "order ready" email: Pending, Processing, Sent, Failed, or null before it is queued.</summary>
  public string? ReadyNotificationStatus { get; set; }
}

/// <summary>
/// Aviso de Clip. Llega sin firma y con un cuerpo mínimo, así que no se toma
/// nada de él como verdad: sólo el identificador, para ir a consultar el pago.
/// </summary>
public sealed class RestaurantClipWebhookRequest
{
  [Required] public string RawBody { get; set; } = string.Empty;
}

public sealed record RestaurantClipWebhookResult(bool Accepted, bool Matched, string? PaymentId = null);

public sealed class RestaurantOnlineOrderingAdminDto
{
  public long PublicSiteId { get; set; }
  public string PublicSiteKey { get; set; } = string.Empty;
  public string Rfc { get; set; } = string.Empty;
  public int SiteId { get; set; }
  public bool IsEnabled { get; set; }
  public bool IsPaused { get; set; }
  public string? PauseMessage { get; set; }
  public bool AllowGuestCheckout { get; set; } = true;
  public bool PickupEnabled { get; set; } = true;
  public decimal MaximumOrderAmount { get; set; } = 2500m;
  public string OnlineHoursJson { get; set; } = "{}";
  public string TermsVersion { get; set; } = string.Empty;
  public string PrivacyVersion { get; set; } = string.Empty;
  public DateTime? ProcessorHeartbeatAtUtc { get; set; }
  public long ConfigurationVersion { get; set; }
  public byte[] RowVersion { get; set; } = [];
  public bool IsReady { get; set; }
  public IReadOnlyList<string> ReadinessBlockers { get; set; } = Array.Empty<string>();
  public IReadOnlyList<RestaurantOnlineProductAdminDto> Products { get; set; } = Array.Empty<RestaurantOnlineProductAdminDto>();
  public IReadOnlyList<RestaurantOnlineRecoveryDto> RecoveryItems { get; set; } = Array.Empty<RestaurantOnlineRecoveryDto>();
  public IReadOnlyList<RestaurantPaymentGatewaySummaryDto> PaymentSummaries { get; set; } = Array.Empty<RestaurantPaymentGatewaySummaryDto>();
}

public sealed class RestaurantOnlineOrderingAdminSaveRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  [Range(1, long.MaxValue)] public long PublicSiteId { get; set; }
  [Range(1, int.MaxValue)] public int SiteId { get; set; }
  public bool IsEnabled { get; set; }
  public bool IsPaused { get; set; }
  [StringLength(300)] public string? PauseMessage { get; set; }
  public bool AllowGuestCheckout { get; set; } = true;
  public bool PickupEnabled { get; set; } = true;
  /// <summary>El tope de Clip por transacción en línea es $10,000 MXN.</summary>
  [Range(typeof(decimal), "0.01", "10000")] public decimal MaximumOrderAmount { get; set; } = 2500m;
  [Required] public string OnlineHoursJson { get; set; } = "{}";
  [Required, StringLength(30)] public string TermsVersion { get; set; } = string.Empty;
  [Required, StringLength(30)] public string PrivacyVersion { get; set; } = string.Empty;
  public List<long> EnabledProductIds { get; set; } = [];
  public long? ExpectedConfigurationVersion { get; set; }
}

public sealed class RestaurantOnlineProductAdminDto
{
  public long ProductId { get; set; }
  public string ProductName { get; set; } = string.Empty;
  public string Sku { get; set; } = string.Empty;
  public decimal Price { get; set; }
  public bool IsActive { get; set; }
  public bool IsSoldOut { get; set; }
  public bool IsOnlineEnabled { get; set; }
}

public sealed class RestaurantOnlineRecoveryDto
{
  public Guid CheckoutAttemptId { get; set; }
  public string CheckoutStatus { get; set; } = string.Empty;
  public string? ProviderOrderId { get; set; }
  public string? ProviderCaptureId { get; set; }
  public int? OrderFolio { get; set; }
  public decimal Total { get; set; }
  public string Currency { get; set; } = "MXN";
  public int RetryCount { get; set; }
  public string? LastErrorCode { get; set; }
  public string? LastErrorMessage { get; set; }
  public DateTime UpdatedAtUtc { get; set; }
}

public sealed class RestaurantPaymentGatewaySummaryDto
{
  public long PublicSiteId { get; set; }
  public string Rfc { get; set; } = string.Empty;
  public string MerchantProfileKey { get; set; } = string.Empty;
  public string Currency { get; set; } = "MXN";
  public int CaptureCount { get; set; }
  public decimal GrossAmount { get; set; }
  public decimal FeeAmount { get; set; }
  public decimal NetAmount { get; set; }
  public decimal RefundedAmount { get; set; }
  public decimal RefundGrossAmount { get; set; }
  public decimal RefundFeeAmount { get; set; }
  public decimal RefundNetAmount { get; set; }
  public decimal? NetAfterRefunds { get; set; }
  public int UnreconciledRefundCount { get; set; }
}

public sealed class RestaurantOnlineRefundRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public Guid OrderId { get; set; }
  [Range(typeof(decimal), "0.01", "999999999")] public decimal Amount { get; set; }
  [Required, StringLength(500)] public string Reason { get; set; } = string.Empty;
  [Required, StringLength(256)] public string RequestedByUserName { get; set; } = string.Empty;
  [Required, StringLength(256)] public string SupervisorUserName { get; set; } = string.Empty;
  [Required, StringLength(100)] public string IdempotencyKey { get; set; } = string.Empty;
}

public sealed record RestaurantOnlineProcessorResult(bool Succeeded, bool WasDuplicate, string Message);
