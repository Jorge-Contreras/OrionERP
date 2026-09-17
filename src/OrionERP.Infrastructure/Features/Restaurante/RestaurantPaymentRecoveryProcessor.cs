using System.Data;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Payments.Clip;
using OrionERP.Application.Features.Platform;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

/// <summary>
/// Resolves charges whose outcome the browser never learned and executes
/// requested refunds. It never creates POS orders or marks local POS payments
/// refunded.
///
/// This is the half of the design that makes the missing idempotency key on
/// Clip's POST /payments survivable: a charge is never repeated, only looked up.
/// When the charge left no payment id at all, the only way to find it is to
/// sweep the date window and match on external_reference, because Clip's
/// GET /payments filters by date and nothing else.
/// </summary>
public sealed class RestaurantPaymentRecoveryProcessor : IPaymentRecoveryProcessor
{
  private static readonly TimeSpan ManualReviewRetryDelay = TimeSpan.FromMinutes(30);
  /// <summary>Holgura hacia atrás al barrer, por diferencias de reloj con Clip.</summary>
  private static readonly TimeSpan SweepLeadIn = TimeSpan.FromMinutes(5);

  private readonly IPublicWebsiteInstanceContext _website;
  private readonly IOrionSqlSessionFactory _sessions;
  private readonly IClipPaymentsClient _clip;
  private readonly RestaurantCheckoutOptions _options;
  private readonly ILogger<RestaurantPaymentRecoveryProcessor> _logger;
  private readonly TimeProvider _clock;

  public RestaurantPaymentRecoveryProcessor(
    IPublicWebsiteInstanceContext website,
    IOrionSqlSessionFactory sessions,
    IClipPaymentsClient clip,
    IOptions<RestaurantCheckoutOptions> options,
    ILogger<RestaurantPaymentRecoveryProcessor> logger,
    TimeProvider? clock = null)
  {
    _website = website;
    _sessions = sessions;
    _clip = clip;
    _options = options.Value;
    _logger = logger;
    _clock = clock ?? TimeProvider.System;
  }

  public async Task<int> ProcessPendingAsync(int batchSize = 10, CancellationToken ct = default)
  {
    var binding = await _website.ResolveRequiredAsync(ct);
    if (!string.Equals(binding.ModuleCode, PlatformModuleCodes.Restaurant, StringComparison.Ordinal))
      throw new UnauthorizedAccessException("La recuperación de pagos requiere un PublicSite Restaurant verificado.");
    var limit = Math.Clamp(batchSize, 1, 50);
    var processed = 0;
    while (processed < limit)
    {
      var foundWork = false;
      var recovery = await ClaimRecoveryAsync(binding, ct);
      if (recovery is not null)
      {
        foundWork = true;
        await ProcessRecoveryAsync(binding, recovery, ct);
        processed++;
      }
      if (processed >= limit) break;

      var refund = await ClaimRefundAsync(binding, ct);
      if (refund is not null)
      {
        foundWork = true;
        await ProcessRefundAsync(binding, refund, ct);
        processed++;
      }
      if (!foundWork) break;
    }
    return processed;
  }

  private async Task<RecoveryRow?> ClaimRecoveryAsync(PublicSiteBinding binding, CancellationToken ct)
  {
    await using var connection = await OpenAsync(binding, ct);
    return await connection.QuerySingleOrDefaultAsync<RecoveryRow>(new CommandDefinition(
      "restaurante.PaymentRecoveryClaim",
      new { LeaseId = Guid.NewGuid(), LeaseSeconds = 90 },
      commandType: CommandType.StoredProcedure,
      cancellationToken: ct));
  }

  private async Task<RefundRow?> ClaimRefundAsync(PublicSiteBinding binding, CancellationToken ct)
  {
    await using var connection = await OpenAsync(binding, ct);
    return await connection.QuerySingleOrDefaultAsync<RefundRow>(new CommandDefinition(
      "restaurante.PaymentGatewayRefundClaim",
      new { LeaseId = Guid.NewGuid(), LeaseSeconds = 90 },
      commandType: CommandType.StoredProcedure,
      cancellationToken: ct));
  }

  private async Task ProcessRecoveryAsync(
    PublicSiteBinding binding,
    RecoveryRow row,
    CancellationToken ct)
  {
    try
    {
      var payment = await FindPaymentAsync(row, ct);
      if (payment is null)
      {
        await HandleMissingPaymentAsync(binding, row, ct);
        return;
      }

      // Un pago que no trae nuestra referencia, o que no cuadra en importe, no
      // se puede aceptar como cobro de este pedido bajo ninguna circunstancia.
      if (!BelongsToAttempt(payment, row))
      {
        _logger.LogError(
          "Clip payment {PaymentId} does not belong to checkout {CheckoutAttemptId}: reference {ExternalReference}, amount {Amount} {Currency}.",
          payment.PaymentId,
          row.CheckoutAttemptId,
          payment.ExternalReference,
          payment.Amount,
          payment.Currency);
        await FinishRecoveryAsync(
          binding,
          row,
          "Failed",
          "CLIP_PAYMENT_MISMATCH",
          "El pago de Clip no corresponde a este checkout.",
          ct,
          ManualReviewRetryDelay);
        return;
      }

      if (IsAwaitingCharge(row.State))
      {
        await ApplyChargeOutcomeAsync(binding, row, payment, ct);
        return;
      }

      // El cobro ya estaba resuelto. Lo unico que puede haber cambiado es un
      // reembolso hecho fuera de nuestro panel, por ejemplo desde la app de Clip.
      if (payment.AmountRefunded > 0 && !IsRefundState(row.State))
      {
        await FinishRecoveryAsync(
          binding,
          row,
          "Failed",
          "CLIP_EXTERNAL_REFUND_REQUIRES_RECONCILIATION",
          $"Clip reporta {payment.AmountRefunded} {payment.Currency} reembolsados sin una solicitud registrada.",
          ct,
          ManualReviewRetryDelay);
        return;
      }

      await FinishRecoveryAsync(binding, row, "Ignored", null, null, ct);
    }
    catch (ClipClientException exception)
    {
      LogProviderFailure("recovery", row.CheckoutAttemptId, exception);
      await FinishRecoveryAsync(
        binding,
        row,
        exception.IsTransient || exception.IsOutcomeUnknown ? "Pending" : "Failed",
        exception.ProviderErrorCode,
        "No fue posible consultar el pago con Clip.",
        ct);
    }
  }

  /// <summary>
  /// Locates the Clip payment behind a recovery row. A charge that never
  /// returned a payment id can only be found by sweeping the window in which it
  /// was started, because Clip offers no lookup by external reference.
  /// </summary>
  private async Task<ClipPaymentResult?> FindPaymentAsync(RecoveryRow row, CancellationToken ct)
  {
    var paymentId = NullIfWhiteSpace(row.ProviderOrderId)
      ?? (string.Equals(row.WorkType, "Event", StringComparison.Ordinal) ? NullIfWhiteSpace(row.ResourceId) : null);
    if (paymentId is not null)
      return await _clip.GetPaymentAsync(paymentId, ct);
    if (!IsAwaitingCharge(row.State))
      return null;

    var startedAt = row.ChargeStartedAtUtc ?? row.CreatedAtUtc;
    var from = new DateTimeOffset(DateTime.SpecifyKind(startedAt, DateTimeKind.Utc)) - SweepLeadIn;
    var payments = await _clip.ListPaymentsAsync(from, _clock.GetUtcNow(), ct);
    return payments.FirstOrDefault(payment =>
      ClipExternalReference.TryParse(payment.ExternalReference, out var attemptId)
      && attemptId == row.CheckoutAttemptId);
  }

  private async Task HandleMissingPaymentAsync(
    PublicSiteBinding binding,
    RecoveryRow row,
    CancellationToken ct)
  {
    if (!IsAwaitingCharge(row.State))
    {
      await FinishRecoveryAsync(binding, row, "Ignored", null, null, ct);
      return;
    }

    var startedAt = row.ChargeStartedAtUtc ?? row.CreatedAtUtc;
    var elapsed = _clock.GetUtcNow() - new DateTimeOffset(DateTime.SpecifyKind(startedAt, DateTimeKind.Utc));
    if (elapsed <= TimeSpan.FromMinutes(_options.ChargeReconciliationWindowMinutes))
    {
      // Todavia puede aparecer: Clip pudo haber recibido el cargo y tardar en
      // reflejarlo. Nunca se vuelve a cobrar para averiguarlo.
      await FinishRecoveryAsync(
        binding,
        row,
        "Pending",
        "CLIP_PAYMENT_NOT_FOUND_YET",
        "El cargo todavía no aparece en Clip.",
        ct);
      return;
    }

    // Pasada la ventana, el cargo nunca llego a Clip. Liberar el intento deja al
    // cliente volver a pagar, que es lo unico util que puede hacer.
    _logger.LogWarning(
      "Checkout {CheckoutAttemptId} had no Clip payment after {Minutes} minutes; releasing it for another attempt.",
      row.CheckoutAttemptId,
      _options.ChargeReconciliationWindowMinutes);
    if (!await TryRecordChargeAsync(binding, row, "Denied",
          failureCode: "CLIP_CHARGE_NOT_FOUND",
          failureMessage: "El cobro no llegó a procesarse. No se hizo ningún cargo.",
          ct: ct))
    {
      await FinishRecoveryAsync(
        binding,
        row,
        "Failed",
        "CLIP_CHARGE_RELEASE_FAILED",
        "No fue posible liberar el intento sin cargo.",
        ct,
        ManualReviewRetryDelay);
      return;
    }
    await FinishRecoveryAsync(binding, row, "Processed", null, null, ct);
  }

  private async Task ApplyChargeOutcomeAsync(
    PublicSiteBinding binding,
    RecoveryRow row,
    ClipPaymentResult payment,
    CancellationToken ct)
  {
    if (payment.IsApproved)
    {
      var recorded = await TryRecordChargeAsync(binding, row, "Approved",
        providerPaymentId: payment.PaymentId,
        externalReference: payment.ExternalReference,
        grossAmount: payment.Amount,
        currencyCode: payment.Currency,
        capturedAtUtc: payment.ApprovedAtUtc?.UtcDateTime,
        ct: ct);
      await FinishRecoveryAsync(
        binding,
        row,
        recorded ? "Processed" : "Failed",
        recorded ? null : "CLIP_CHARGE_RECORD_FAILED",
        recorded ? null : "No fue posible registrar el cobro aprobado.",
        ct,
        recorded ? null : ManualReviewRetryDelay);
      return;
    }

    if (payment.IsTerminalFailure)
    {
      var recorded = await TryRecordChargeAsync(binding, row, "Denied",
        failureCode: NullIfWhiteSpace(payment.StatusCode) ?? "CLIP_REJECTED",
        failureMessage: ClipStatusMessages.ForCode(payment.StatusCode),
        ct: ct);
      await FinishRecoveryAsync(
        binding,
        row,
        recorded ? "Processed" : "Failed",
        recorded ? null : "CLIP_CHARGE_RECORD_FAILED",
        recorded ? null : "No fue posible registrar el rechazo del cobro.",
        ct,
        recorded ? null : ManualReviewRetryDelay);
      return;
    }

    if (payment.IsPending)
    {
      // Sigue vivo en Clip, tipicamente esperando 3DS. No se puede cancelar ni
      // volver a cobrar: solo esperar a que Clip lo resuelva.
      await FinishRecoveryAsync(
        binding,
        row,
        "Pending",
        NullIfWhiteSpace(payment.StatusCode) ?? "CLIP_PAYMENT_PENDING",
        "Clip todavía no resuelve este pago.",
        ct);
      return;
    }

    // Autorizado sin captura o reembolsado antes de registrarse: ninguno deberia
    // ocurrir con capture_method automatico, y ambos mueven dinero.
    _logger.LogWarning(
      "Clip payment {PaymentId} for checkout {CheckoutAttemptId} is in unexpected status {Status}/{StatusCode}.",
      payment.PaymentId,
      row.CheckoutAttemptId,
      payment.Status,
      payment.StatusCode);
    await FinishRecoveryAsync(
      binding,
      row,
      "Failed",
      "CLIP_PAYMENT_STATE_REQUIRES_RECONCILIATION",
      $"Clip reportó el estado {payment.Status} que requiere conciliación.",
      ct,
      ManualReviewRetryDelay);
  }

  private async Task ProcessRefundAsync(
    PublicSiteBinding binding,
    RefundRow row,
    CancellationToken ct)
  {
    try
    {
      if (!string.IsNullOrWhiteSpace(row.ProviderRefundId))
      {
        var existing = await _clip.GetRefundAsync(row.ProviderRefundId, ct);
        await FinishRefundFromProviderAsync(binding, row, existing, ct);
        return;
      }

      // La llave de idempotencia de Clip vive un minuto, asi que no protege
      // entre reintentos. Lo que si protege es mirar cuanto lleva reembolsado el
      // pago antes de volver a pedirlo.
      var payment = await _clip.GetPaymentAsync(row.ProviderCaptureId, ct);
      if (payment.AmountRefunded >= payment.Amount && payment.Amount > 0)
      {
        await FinishRefundAsync(
          binding,
          row,
          "Failed",
          providerRefundId: null,
          "CLIP_REFUND_ALREADY_SETTLED",
          $"Clip ya reporta {payment.AmountRefunded} {payment.Currency} reembolsados de este pago.",
          ct);
        return;
      }
      if (payment.AmountRefunded + row.Amount > payment.Amount)
      {
        await FinishRefundAsync(
          binding,
          row,
          "Failed",
          providerRefundId: null,
          "CLIP_REFUND_EXCEEDS_PAYMENT",
          "El reembolso solicitado supera el saldo reembolsable del pago.",
          ct);
        return;
      }

      var refund = await _clip.RefundPaymentAsync(new ClipRefundRequest
      {
        Amount = row.Amount,
        Reason = "Reembolso de pedido para recoger",
        PaymentId = row.ProviderCaptureId
      }, row.IdempotencyKey, ct);
      await FinishRefundFromProviderAsync(binding, row, refund, ct);
    }
    catch (ClipClientException exception)
    {
      LogProviderFailure("refund", row.CheckoutAttemptId, exception);
      if (exception.IsOutcomeUnknown)
      {
        // No sabemos si el reembolso entro. La siguiente pasada lo averigua
        // mirando AmountRefunded, nunca repitiendolo a ciegas.
        await FinishRefundAsync(
          binding,
          row,
          "Pending",
          providerRefundId: null,
          exception.ProviderErrorCode,
          "El reembolso no devolvió respuesta y está en verificación.",
          ct);
        return;
      }
      await FinishRefundAsync(
        binding,
        row,
        exception.IsTransient ? "Pending" : "Failed",
        providerRefundId: NullIfWhiteSpace(row.ProviderRefundId),
        exception.ProviderErrorCode,
        "No fue posible completar el reembolso con Clip.",
        ct);
    }
  }

  private async Task FinishRefundFromProviderAsync(
    PublicSiteBinding binding,
    RefundRow row,
    ClipRefundResult refund,
    CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(refund.RefundId) || refund.Amount != row.Amount
        || !string.Equals(refund.Currency, row.CurrencyCode, StringComparison.OrdinalIgnoreCase))
    {
      _logger.LogWarning(
        "Clip refund {RefundId} did not match request {LocalRefundId}: {Amount} {Currency} vs {ExpectedAmount} {ExpectedCurrency}.",
        refund.RefundId,
        row.Id,
        refund.Amount,
        refund.Currency,
        row.Amount,
        row.CurrencyCode);
      await FinishRefundAsync(
        binding,
        row,
        "Failed",
        NullIfWhiteSpace(refund.RefundId),
        "CLIP_REFUND_MISMATCH",
        "La respuesta de Clip no coincide con el reembolso solicitado.",
        ct);
      return;
    }

    await FinishRefundAsync(
      binding,
      row,
      refund.IsApproved ? "Completed" : refund.IsDeclined ? "Failed" : "Pending",
      refund.RefundId,
      refund.IsDeclined ? "CLIP_REFUND_DECLINED" : null,
      refund.IsDeclined
        ? NullIfWhiteSpace(refund.StatusMessage) ?? "Clip no aprobó el reembolso."
        : null,
      ct);
  }

  /// <summary>
  /// Writes a charge outcome for a recovery row. Returns false when the row
  /// carries no charge seal or the durable state moved on, so the caller can
  /// route it to manual review instead of assuming success.
  /// </summary>
  private async Task<bool> TryRecordChargeAsync(
    PublicSiteBinding binding,
    RecoveryRow row,
    string outcome,
    string? providerPaymentId = null,
    string? externalReference = null,
    decimal? grossAmount = null,
    string? currencyCode = null,
    string? failureCode = null,
    string? failureMessage = null,
    DateTime? capturedAtUtc = null,
    CancellationToken ct = default)
  {
    if (row.ChargeRequestId is null)
    {
      _logger.LogWarning(
        "Checkout {CheckoutAttemptId} is awaiting a charge without a charge seal; it cannot be resolved automatically.",
        row.CheckoutAttemptId);
      return false;
    }

    try
    {
      await using var connection = await OpenAsync(binding, ct);
      using var result = await connection.QueryMultipleAsync(new CommandDefinition(
        "restaurante.OnlineCheckoutChargeResult",
        new
        {
          Id = row.CheckoutAttemptId,
          ChargeRequestId = row.ChargeRequestId.Value,
          Outcome = outcome,
          ProviderPaymentId = providerPaymentId,
          ExternalReference = externalReference,
          GrossAmount = grossAmount,
          CurrencyCode = currencyCode,
          FailureCode = failureCode,
          FailureMessage = failureMessage,
          CapturedAtUtc = capturedAtUtc
        },
        commandType: CommandType.StoredProcedure,
        cancellationToken: ct));
      _ = await result.ReadSingleAsync<dynamic>();
      _ = await result.ReadSingleOrDefaultAsync<dynamic>();
      return true;
    }
    catch (Microsoft.Data.SqlClient.SqlException exception)
      when (exception.Number is 54045 or 54046 or 54047)
    {
      // Otra petición del navegador o un aviso posterior resolvió el cargo
      // primero. Eso es un desenlace válido, no una falla de recuperación.
      _logger.LogInformation(
        "Charge outcome for checkout {CheckoutAttemptId} was superseded (SQL {Number}).",
        row.CheckoutAttemptId,
        exception.Number);
      return true;
    }
  }

  private async Task FinishRecoveryAsync(
    PublicSiteBinding binding,
    RecoveryRow row,
    string outcome,
    string? failureCode,
    string? failureMessage,
    CancellationToken ct,
    TimeSpan? retryDelay = null)
  {
    await using var connection = await OpenAsync(binding, ct);
    await connection.ExecuteAsync(new CommandDefinition(
      "restaurante.PaymentRecoveryResult",
      new
      {
        row.WorkType,
        row.CheckoutAttemptId,
        row.EventId,
        row.LeaseId,
        Outcome = outcome,
        FailureCode = failureCode,
        FailureMessage = failureMessage,
        NextRetryAtUtc = outcome is "Pending" or "Failed"
          ? _clock.GetUtcNow().Add(retryDelay ?? RetryDelay(row.RecoveryAttempts)).UtcDateTime
          : (DateTime?)null
      },
      commandType: CommandType.StoredProcedure,
      cancellationToken: ct));
  }

  /// <summary>
  /// Clip's refund object carries no fee breakdown, so a completed refund is
  /// settled at face value: the full amount leaves the balance and no fee is
  /// recorded against it. The provider columns are all-or-nothing, so they are
  /// only written once the refund is actually completed; a pending or failed one
  /// stays unreconciled. If Clip ever returns a real breakdown, or the Deposits
  /// API is wired up, that figure should replace this assumption.
  /// </summary>
  private async Task FinishRefundAsync(
    PublicSiteBinding binding,
    RefundRow row,
    string outcome,
    string? providerRefundId,
    string? failureCode,
    string? failureMessage,
    CancellationToken ct)
  {
    var isSettled = string.Equals(outcome, "Completed", StringComparison.Ordinal);
    await using var connection = await OpenAsync(binding, ct);
    await connection.ExecuteAsync(new CommandDefinition(
      "restaurante.PaymentGatewayRefundResult",
      new
      {
        row.Id,
        row.LeaseId,
        Outcome = outcome,
        ProviderRefundId = providerRefundId,
        ProviderGrossAmount = isSettled ? row.Amount : (decimal?)null,
        ProviderFeeAmount = isSettled ? 0m : (decimal?)null,
        ProviderNetAmount = isSettled ? row.Amount : (decimal?)null,
        ReconciledAtUtc = isSettled ? _clock.GetUtcNow().UtcDateTime : (DateTime?)null,
        FailureCode = failureCode,
        FailureMessage = failureMessage,
        NextRetryAtUtc = outcome is "Pending" or "Failed"
          ? _clock.GetUtcNow().Add(RetryDelay(row.Attempts)).UtcDateTime
          : (DateTime?)null
      },
      commandType: CommandType.StoredProcedure,
      cancellationToken: ct));
  }

  private Task<System.Data.Common.DbConnection> OpenAsync(PublicSiteBinding binding, CancellationToken ct)
    => _sessions.OpenAsync(PlatformExecutionScope.FromPublicSite(binding), ct);

  private static bool BelongsToAttempt(ClipPaymentResult payment, RecoveryRow row)
    => ClipExternalReference.TryParse(payment.ExternalReference, out var attemptId)
      && attemptId == row.CheckoutAttemptId
      && payment.Amount == row.Total
      && string.Equals(payment.Currency, row.CurrencyCode, StringComparison.OrdinalIgnoreCase);

  private static bool IsAwaitingCharge(string? state)
    => state is RestaurantOnlineCheckoutStatuses.ChargePending
      or RestaurantOnlineCheckoutStatuses.Authenticating3ds
      or RestaurantOnlineCheckoutStatuses.ChargeUnknown;

  private static bool IsRefundState(string? state)
    => state is RestaurantOnlineCheckoutStatuses.RefundRequested
      or RestaurantOnlineCheckoutStatuses.RefundPending
      or RestaurantOnlineCheckoutStatuses.Refunded;

  private static TimeSpan RetryDelay(int attempts)
    => TimeSpan.FromSeconds(Math.Min(300, 20 * Math.Max(1, attempts)));

  private void LogProviderFailure(string stage, Guid checkoutAttemptId, ClipClientException exception)
    => _logger.LogWarning(
      exception,
      "Clip {Stage} operation {Operation} failed for checkout {CheckoutAttemptId}: provider code {ProviderCode}, HTTP {StatusCode}, outcome unknown {OutcomeUnknown}.",
      stage,
      exception.Operation,
      checkoutAttemptId,
      exception.ProviderErrorCode,
      exception.StatusCode,
      exception.IsOutcomeUnknown);

  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private sealed class RecoveryRow
  {
    public string WorkType { get; set; } = string.Empty;
    public Guid CheckoutAttemptId { get; set; }
    public long? EventId { get; set; }
    public string? EventType { get; set; }
    public string? ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public string? RelatedOrderId { get; set; }
    public string? RelatedCaptureId { get; set; }
    public string? RelatedRefundId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string? ProviderOrderId { get; set; }
    public string? ProviderCaptureId { get; set; }
    public Guid? ChargeRequestId { get; set; }
    public DateTime? ChargeStartedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string MerchantProfileKey { get; set; } = string.Empty;
    public string QuoteFingerprint { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public string CurrencyCode { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public int RecoveryAttempts { get; set; }
    public Guid LeaseId { get; set; }
  }

  private sealed class RefundRow
  {
    public Guid Id { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public Guid LeaseId { get; set; }
    public string ProviderCaptureId { get; set; } = string.Empty;
    public string? ProviderRefundId { get; set; }
    public string MerchantProfileKey { get; set; } = string.Empty;
    public Guid CheckoutAttemptId { get; set; }
  }
}
