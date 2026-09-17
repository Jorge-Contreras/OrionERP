namespace OrionERP.Application.Features.Payments.Clip;

/// <summary>Valores del campo <c>status</c> de un pago de Clip.</summary>
public static class ClipPaymentStatuses
{
  public const string Approved = "approved";
  public const string Authorized = "authorized";
  public const string Pending = "pending";
  public const string Rejected = "rejected";
  public const string Cancelled = "cancelled";
  public const string Refunded = "refunded";
}

/// <summary>
/// Códigos de <c>status_detail</c> que el flujo distingue. El resto se trata por
/// el <c>status</c> que los acompaña.
/// </summary>
public static class ClipStatusDetailCodes
{
  public const string Paid = "AP-PAI01";
  public const string PartiallyRefunded = "AP-REF01";
  public const string Refunded = "RE-REF01";
  public const string PendingCapture = "AU-CAP01";
  public const string WaitingThreeDSecure = "PE-3DS01";
  public const string FailedThreeDSecure = "RE-3DS01";
}

/// <summary>
/// El <c>external_reference</c> de Clip admite 36 caracteres y sólo guión medio
/// y guión bajo, así que un GUID en formato "D" cabe exacto y es el único
/// identificador que necesitamos para reconciliar un cargo huérfano.
/// </summary>
public static class ClipExternalReference
{
  public const int MaximumLength = 36;

  public static string From(Guid checkoutAttemptId)
  {
    if (checkoutAttemptId == Guid.Empty)
      throw new ArgumentException("El intento de checkout es obligatorio.", nameof(checkoutAttemptId));
    return checkoutAttemptId.ToString("D");
  }

  public static bool TryParse(string? externalReference, out Guid checkoutAttemptId)
  {
    checkoutAttemptId = Guid.Empty;
    var normalized = externalReference?.Trim();
    return !string.IsNullOrEmpty(normalized)
      && normalized.Length == MaximumLength
      && Guid.TryParseExact(normalized, "D", out checkoutAttemptId);
  }
}

public sealed class ClipPaymentRequest
{
  public decimal Amount { get; set; }
  public string Currency { get; set; } = "MXN";
  public string Description { get; set; } = string.Empty;
  /// <summary>Token de un solo uso del SDK; vive 15 minutos.</summary>
  public string CardTokenId { get; set; } = string.Empty;
  public string ExternalReference { get; set; } = string.Empty;
  /// <summary>Destino de los avisos de cambio de estatus. Tiene que ser HTTPS absoluto.</summary>
  public string WebhookUrl { get; set; } = string.Empty;
  /// <summary><c>automatic</c> cobra de inmediato; <c>manual</c> sólo autoriza.</summary>
  public string CaptureMethod { get; set; } = "automatic";
  /// <summary>1 significa sin diferir. Los pagos diferidos no son reembolsables por API.</summary>
  public int Installments { get; set; } = 1;
  /// <summary>true rechaza en automático cualquier pago que requiera 3DS.</summary>
  public bool BinaryMode { get; set; }
  public ClipCustomer Customer { get; set; } = new();
}

public sealed class ClipCustomer
{
  public string FirstName { get; set; } = string.Empty;
  public string LastName { get; set; } = string.Empty;
  public string Email { get; set; } = string.Empty;
  public string Phone { get; set; } = string.Empty;
}

public sealed class ClipPaymentResult
{
  public string PaymentId { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
  public string StatusCode { get; set; } = string.Empty;
  public string StatusMessage { get; set; } = string.Empty;
  public decimal Amount { get; set; }
  public decimal AmountRefunded { get; set; }
  public string Currency { get; set; } = "MXN";
  public string ExternalReference { get; set; } = string.Empty;
  public string ReceiptNumber { get; set; } = string.Empty;
  public int Installments { get; set; } = 1;
  public ClipCard Card { get; set; } = new();
  public ClipPendingAction? PendingAction { get; set; }
  public DateTimeOffset? CreatedAtUtc { get; set; }
  public DateTimeOffset? ApprovedAtUtc { get; set; }

  public bool IsApproved
    => string.Equals(Status, ClipPaymentStatuses.Approved, StringComparison.OrdinalIgnoreCase);
  public bool IsRejected
    => string.Equals(Status, ClipPaymentStatuses.Rejected, StringComparison.OrdinalIgnoreCase);
  public bool IsCancelled
    => string.Equals(Status, ClipPaymentStatuses.Cancelled, StringComparison.OrdinalIgnoreCase);
  public bool IsRefunded
    => string.Equals(Status, ClipPaymentStatuses.Refunded, StringComparison.OrdinalIgnoreCase);
  public bool IsPending
    => string.Equals(Status, ClipPaymentStatuses.Pending, StringComparison.OrdinalIgnoreCase);

  /// <summary>
  /// Un pago pendiente de 3DS trae la URL que el navegador tiene que abrir en un
  /// iframe. Sin URL no hay nada que el cliente pueda hacer para avanzarlo.
  /// </summary>
  public bool RequiresThreeDSecure
    => IsPending
      && string.Equals(StatusCode, ClipStatusDetailCodes.WaitingThreeDSecure, StringComparison.OrdinalIgnoreCase)
      && !string.IsNullOrWhiteSpace(PendingAction?.Url);

  /// <summary>Un cargo que ya no puede prosperar por sí solo.</summary>
  public bool IsTerminalFailure => IsRejected || IsCancelled;
}

public sealed class ClipPendingAction
{
  /// <summary>El único valor que Clip documenta es <c>open_modal</c>.</summary>
  public string Type { get; set; } = string.Empty;
  public string Url { get; set; } = string.Empty;
}

public sealed class ClipCard
{
  public string Brand { get; set; } = string.Empty;
  public string Type { get; set; } = string.Empty;
  public string Issuer { get; set; } = string.Empty;
  public string LastDigits { get; set; } = string.Empty;
  public string Country { get; set; } = string.Empty;
}

public sealed class ClipRefundRequest
{
  public decimal Amount { get; set; }
  public string Reason { get; set; } = string.Empty;
  public string PaymentId { get; set; } = string.Empty;
}

public sealed class ClipRefundResult
{
  public string RefundId { get; set; } = string.Empty;
  public string PaymentId { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
  public string StatusMessage { get; set; } = string.Empty;
  public decimal Amount { get; set; }
  public string Currency { get; set; } = "MXN";
  public string ReceiptNumber { get; set; } = string.Empty;
  public DateTimeOffset? CreatedAtUtc { get; set; }

  public bool IsApproved => string.Equals(Status, "approved", StringComparison.OrdinalIgnoreCase);
  public bool IsDeclined => string.Equals(Status, "declined", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Aviso de Clip. Llega sin firma y sólo trae el identificador, así que nada de
/// su contenido se toma como verdad: el estado real se consulta.
/// </summary>
public sealed class ClipWebhookNotification
{
  public string Id { get; set; } = string.Empty;
  /// <summary><c>payments-api</c> para el checkout transparente.</summary>
  public string Origin { get; set; } = string.Empty;
  /// <summary><c>INSERT</c> en un pago nuevo, <c>UPDATE</c> al cambiar de estatus.</summary>
  public string EventType { get; set; } = string.Empty;
}

public sealed class ClipClientException : Exception
{
  public ClipClientException(
    string operation,
    string providerErrorCode,
    int? statusCode,
    bool isTransient,
    bool isOutcomeUnknown,
    Exception? innerException = null)
    : base(BuildMessage(operation, providerErrorCode, statusCode), innerException)
  {
    Operation = operation;
    ProviderErrorCode = providerErrorCode;
    StatusCode = statusCode;
    IsTransient = isTransient;
    IsOutcomeUnknown = isOutcomeUnknown;
  }

  public string Operation { get; }
  public string ProviderErrorCode { get; }
  public int? StatusCode { get; }

  /// <summary>La operación se puede repetir sin efectos: sólo para lecturas.</summary>
  public bool IsTransient { get; }

  /// <summary>
  /// La llamada no dejó una respuesta utilizable, así que no sabemos si Clip cobró.
  /// El cargo tiene que resolverse consultando, nunca repitiéndolo:
  /// <c>POST /payments</c> no acepta llave de idempotencia.
  /// </summary>
  public bool IsOutcomeUnknown { get; }

  public bool IsNotConfigured
    => string.Equals(ProviderErrorCode, "NOT_CONFIGURED", StringComparison.Ordinal);

  private static string BuildMessage(string operation, string providerErrorCode, int? statusCode)
    => $"Clip {operation} failed ({providerErrorCode}, HTTP {statusCode?.ToString() ?? "n/a"}).";
}
