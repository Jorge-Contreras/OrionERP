using System.Security.Cryptography;
using System.Text;

namespace OrionERP.Application.Features.Payments.PayPal;

public static class PayPalRequestId
{
  public const int MaximumLength = 38;

  public static string From(string idempotencyKey)
  {
    if (string.IsNullOrWhiteSpace(idempotencyKey))
    {
      throw new ArgumentException(
        "A stable PayPal idempotency key is required.",
        nameof(idempotencyKey));
    }

    var normalized = idempotencyKey.Trim();
    if (normalized.Length <= MaximumLength)
    {
      return normalized;
    }

    return Convert
      .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
      .ToLowerInvariant()[..MaximumLength];
  }
}

public sealed class PayPalCreateOrderRequest
{
  public string Intent { get; set; } = "CAPTURE";
  public PayPalExperienceContext? ExperienceContext { get; set; }
  public IReadOnlyList<PayPalPurchaseUnitRequest> PurchaseUnits { get; set; }
    = Array.Empty<PayPalPurchaseUnitRequest>();
}

public sealed class PayPalExperienceContext
{
  public string Locale { get; set; } = string.Empty;
  public string ShippingPreference { get; set; } = string.Empty;
  public string UserAction { get; set; } = string.Empty;
}

public sealed class PayPalPurchaseUnitRequest
{
  public string ReferenceId { get; set; } = string.Empty;
  public string Description { get; set; } = string.Empty;
  public string CustomId { get; set; } = string.Empty;
  public string InvoiceId { get; set; } = string.Empty;
  public PayPalMoney Amount { get; set; } = new();
}

public sealed class PayPalRefundRequest
{
  /// <summary>Null requests a full refund of the remaining captured amount.</summary>
  public PayPalMoney? Amount { get; set; }
  public string InvoiceId { get; set; } = string.Empty;
  public string NoteToPayer { get; set; } = string.Empty;
}

public sealed class PayPalWebhookSignature
{
  public string TransmissionId { get; set; } = string.Empty;
  public string TransmissionTime { get; set; } = string.Empty;
  public string TransmissionSignature { get; set; } = string.Empty;
  public string CertificateUrl { get; set; } = string.Empty;
  public string AuthenticationAlgorithm { get; set; } = string.Empty;
}

public sealed class PayPalOrderResult
{
  public string OrderId { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
  public PayPalPayer Payer { get; set; } = new();
  public IReadOnlyList<PayPalPurchaseUnitResult> PurchaseUnits { get; set; }
    = Array.Empty<PayPalPurchaseUnitResult>();

  public IReadOnlyList<PayPalCaptureResult> Captures
    => PurchaseUnits.SelectMany(unit => unit.Captures).ToArray();
}

public sealed class PayPalPurchaseUnitResult
{
  public string ReferenceId { get; set; } = string.Empty;
  public string CustomId { get; set; } = string.Empty;
  public string InvoiceId { get; set; } = string.Empty;
  public PayPalMoney Amount { get; set; } = new();
  public IReadOnlyList<PayPalCaptureResult> Captures { get; set; }
    = Array.Empty<PayPalCaptureResult>();
}

public sealed class PayPalCaptureResult
{
  public string OrderId { get; set; } = string.Empty;
  public string OrderStatus { get; set; } = string.Empty;
  public string CaptureId { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
  public string StatusReason { get; set; } = string.Empty;
  public string ReferenceId { get; set; } = string.Empty;
  public string CustomId { get; set; } = string.Empty;
  public string InvoiceId { get; set; } = string.Empty;
  public PayPalMoney Amount { get; set; } = new();
  public PayPalSellerReceivableBreakdown? SellerReceivableBreakdown { get; set; }
  public PayPalPayer Payer { get; set; } = new();
  public bool IsCompleted => string.Equals(Status, "COMPLETED", StringComparison.OrdinalIgnoreCase);
}

public sealed class PayPalRefundResult
{
  public string RefundId { get; set; } = string.Empty;
  public string CaptureId { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
  public PayPalMoney Amount { get; set; } = new();
  public PayPalSellerPayableBreakdown? SellerPayableBreakdown { get; set; }
  public bool IsCompleted => string.Equals(Status, "COMPLETED", StringComparison.OrdinalIgnoreCase);
}

public sealed class PayPalMoney
{
  public decimal Value { get; set; }
  public string Currency { get; set; } = "MXN";
}

public sealed class PayPalSellerReceivableBreakdown
{
  public PayPalMoney GrossAmount { get; set; } = new();
  public PayPalMoney PayPalFee { get; set; } = new();
  public PayPalMoney NetAmount { get; set; } = new();
}

public sealed class PayPalSellerPayableBreakdown
{
  public PayPalMoney GrossAmount { get; set; } = new();
  public PayPalMoney PayPalFee { get; set; } = new();
  public PayPalMoney NetAmount { get; set; } = new();
  public PayPalMoney TotalRefundedAmount { get; set; } = new();
}

public sealed class PayPalPayer
{
  public string Name { get; set; } = string.Empty;
  public string Email { get; set; } = string.Empty;
  public string Phone { get; set; } = string.Empty;
}

public sealed class PayPalWebhookVerificationResult
{
  public string VerificationStatus { get; set; } = string.Empty;
  public bool IsVerified
    => string.Equals(VerificationStatus, "SUCCESS", StringComparison.OrdinalIgnoreCase);
}

public sealed class PayPalClientException : Exception
{
  public PayPalClientException(
    string operation,
    string providerErrorCode,
    int? statusCode,
    string? debugId,
    bool isAlreadyCaptured,
    bool isTransient,
    Exception? innerException = null,
    IReadOnlyList<string>? providerIssueCodes = null)
    : base(BuildMessage(operation, providerErrorCode, statusCode), innerException)
  {
    Operation = operation;
    ProviderErrorCode = providerErrorCode;
    StatusCode = statusCode;
    DebugId = debugId;
    IsAlreadyCaptured = isAlreadyCaptured;
    IsTransient = isTransient;
    ProviderIssueCodes = providerIssueCodes ?? Array.Empty<string>();
  }

  public string Operation { get; }
  public string ProviderErrorCode { get; }
  public int? StatusCode { get; }
  public string? DebugId { get; }
  public bool IsAlreadyCaptured { get; }
  public bool IsTransient { get; }
  public IReadOnlyList<string> ProviderIssueCodes { get; }

  private static string BuildMessage(string operation, string providerErrorCode, int? statusCode)
    => $"PayPal {operation} failed ({providerErrorCode}, HTTP {statusCode?.ToString() ?? "n/a"}).";
}
