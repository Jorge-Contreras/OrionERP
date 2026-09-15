using System.Data;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Payments.PayPal;
using OrionERP.Application.Features.Platform;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

/// <summary>
/// Reconciles ambiguous PayPal captures/webhooks and executes requested refunds.
/// It never creates POS orders or marks local POS payments refunded.
/// </summary>
public sealed class RestaurantPayPalRecoveryProcessor : IPayPalRecoveryProcessor
{
  private readonly IPublicWebsiteInstanceContext _website;
  private readonly IOrionSqlSessionFactory _sessions;
  private readonly IRestaurantPayPalClientResolver _clients;
  private readonly RestaurantCheckoutOptions _options;
  private readonly ILogger<RestaurantPayPalRecoveryProcessor> _logger;
  private readonly TimeProvider _clock;

  public RestaurantPayPalRecoveryProcessor(
    IPublicWebsiteInstanceContext website,
    IOrionSqlSessionFactory sessions,
    IRestaurantPayPalClientResolver clients,
    IOptions<RestaurantCheckoutOptions> options,
    ILogger<RestaurantPayPalRecoveryProcessor> logger,
    TimeProvider? clock = null)
  {
    _website = website;
    _sessions = sessions;
    _clients = clients;
    _options = options.Value;
    _logger = logger;
    _clock = clock ?? TimeProvider.System;
  }

  public async Task<int> ProcessPendingAsync(int batchSize = 10, CancellationToken ct = default)
  {
    var binding = await _website.ResolveRequiredAsync(ct);
    if (!string.Equals(binding.ModuleCode, PlatformModuleCodes.Restaurant, StringComparison.Ordinal))
      throw new UnauthorizedAccessException("El procesador PayPal requiere un PublicSite Restaurant verificado.");
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
      "restaurante.PayPalRecoveryClaim",
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
      var client = _clients.Resolve(row.MerchantProfileKey);
      if (row.WorkType == "Event" && IsRefundEvent(row.EventType))
      {
        await ProcessRefundEventAsync(binding, row, client, ct);
        return;
      }
      if (row.WorkType == "Event" && IsCompletedCaptureRefundEvent(row))
      {
        // PayPal reports a completed refund as PAYMENT.CAPTURE.REFUNDED whose
        // resource is the refund itself, so it reconciles like PAYMENT.REFUND.*.
        await ProcessRefundEventAsync(binding, row, client, ct);
        return;
      }
      if (row.WorkType == "Event" && IsCaptureRefundOrReversalEvent(row.EventType))
      {
        await FinishRecoveryAsync(
          binding,
          row,
          "Failed",
          "PAYPAL_CAPTURE_REFUND_REQUIRES_RECONCILIATION",
          "PayPal reportó un reembolso o reverso sin un ID de reembolso conciliable.",
          ct,
          ManualReviewRetryDelay);
        return;
      }
      if (string.IsNullOrWhiteSpace(row.PayPalOrderId))
      {
        await FinishRecoveryAsync(binding, row, "Ignored", null, null, ct);
        return;
      }
      var order = await client.GetOrderAsync(row.PayPalOrderId, ct);
      ValidateOrder(order, binding, row);
      var capture = order.Captures.SingleOrDefault();
      if (capture is null
          && row.State == RestaurantOnlineCheckoutStatuses.CapturePending
          && string.Equals(order.Status, "APPROVED", StringComparison.OrdinalIgnoreCase))
      {
        try
        {
          capture = await client.CaptureOrderAsync(
            row.PayPalOrderId,
            RestaurantPayPalMetadataPolicy.RequestId(binding.PublicSiteKey, "capture", row.CheckoutAttemptId),
            ct);
          capture = CompleteCaptureMetadata(capture, order);
        }
        catch (PayPalClientException exception) when (exception.IsAlreadyCaptured)
        {
          order = await client.GetOrderAsync(row.PayPalOrderId, ct);
          ValidateOrder(order, binding, row);
          capture = order.Captures.SingleOrDefault();
        }
      }

      if (capture is null)
      {
        if (row.State == RestaurantOnlineCheckoutStatuses.CapturePending
            && string.Equals(order.Status, "VOIDED", StringComparison.OrdinalIgnoreCase))
        {
          await SetAttemptStateAsync(
            binding,
            row,
            RestaurantOnlineCheckoutStatuses.PaymentDenied,
            "PAYPAL_ORDER_VOIDED",
            "PayPal cerró la orden sin completar el cobro.",
            ct);
          await FinishRecoveryAsync(binding, row, "Processed", null, null, ct);
          return;
        }

        var outcome = row.WorkType == "Event" && row.State != RestaurantOnlineCheckoutStatuses.CapturePending
          ? "Ignored"
          : "Pending";
        await FinishRecoveryAsync(
          binding,
          row,
          outcome,
          outcome == "Pending" ? "CAPTURE_NOT_FINAL" : null,
          outcome == "Pending" ? "PayPal todavía no reporta una captura final." : null,
          ct);
        return;
      }

      ValidateCapture(capture, binding, row);
      var status = capture.IsCompleted
        ? "Completed"
        : string.Equals(capture.Status, "PENDING", StringComparison.OrdinalIgnoreCase)
          ? "Pending"
          : string.Equals(capture.Status, "DENIED", StringComparison.OrdinalIgnoreCase)
            ? "Denied"
            : null;
      if (status is null)
      {
        var refundStatus = string.Equals(capture.Status, "REFUNDED", StringComparison.OrdinalIgnoreCase)
          || string.Equals(capture.Status, "PARTIALLY_REFUNDED", StringComparison.OrdinalIgnoreCase);
        await FinishRecoveryAsync(
          binding,
          row,
          "Failed",
          refundStatus
            ? "PAYPAL_CAPTURE_REFUND_REQUIRES_RECONCILIATION"
            : "PAYPAL_CAPTURE_STATE_REQUIRES_RECONCILIATION",
          "PayPal reportó un estado posterior al cobro que requiere conciliación.",
          ct);
        return;
      }
      if (status == "Denied"
          && row.State is not RestaurantOnlineCheckoutStatuses.PayPalCreated
            and not RestaurantOnlineCheckoutStatuses.CapturePending)
      {
        await FinishRecoveryAsync(
          binding,
          row,
          "Failed",
          "PAYPAL_CAPTURE_STATE_REGRESSION",
          "PayPal reportó una captura denegada después de que el pago ya estaba confirmado.",
          ct);
        return;
      }
      await using (var connection = await OpenAsync(binding, ct))
      {
        using var result = await connection.QueryMultipleAsync(new CommandDefinition(
          "restaurante.OnlineCheckoutCaptureRecord",
          new
          {
            Id = row.CheckoutAttemptId,
            PayPalOrderId = row.PayPalOrderId,
            PayPalCaptureId = capture.CaptureId,
            Status = status,
            GrossAmount = capture.Amount.Value,
            FeeAmount = capture.SellerReceivableBreakdown?.PayPalFee.Value,
            NetAmount = capture.SellerReceivableBreakdown?.NetAmount.Value,
            CurrencyCode = capture.Amount.Currency,
            IdempotencyKey = RestaurantPayPalMetadataPolicy.RequestId(binding.PublicSiteKey, "capture", row.CheckoutAttemptId),
            CapturedAtUtc = capture.IsCompleted ? _clock.GetUtcNow().UtcDateTime : (DateTime?)null
          },
          commandType: CommandType.StoredProcedure,
          cancellationToken: ct));
        _ = await result.ReadSingleAsync<dynamic>();
        _ = await result.ReadSingleOrDefaultAsync<dynamic>();
      }
      await FinishRecoveryAsync(
        binding,
        row,
        status == "Pending" ? "Pending" : "Processed",
        status == "Pending" ? "CAPTURE_PENDING" : null,
        status == "Pending" ? "PayPal continúa procesando la captura." : null,
        ct);
    }
    catch (PayPalClientException exception)
    {
      LogProviderFailure("recovery", row.CheckoutAttemptId, exception);
      if (IsExplicitCaptureDenial(exception)
          && row.State is RestaurantOnlineCheckoutStatuses.PayPalCreated
            or RestaurantOnlineCheckoutStatuses.CapturePending)
      {
        await SetAttemptStateAsync(
          binding,
          row,
          RestaurantOnlineCheckoutStatuses.PaymentDenied,
          exception.ProviderErrorCode,
          "PayPal rechazó el cobro.",
          ct);
        await FinishRecoveryAsync(binding, row, "Processed", null, null, ct);
        return;
      }
      await FinishRecoveryAsync(
        binding,
        row,
        exception.IsTransient ? "Pending" : "Failed",
        exception.ProviderErrorCode,
        "No fue posible conciliar la captura con PayPal.",
        ct);
    }
    catch (InvalidOperationException exception)
    {
      _logger.LogWarning("PayPal recovery validation failed for checkout {CheckoutAttemptId}: {Reason}", row.CheckoutAttemptId, exception.Message);
      await FinishRecoveryAsync(
        binding,
        row,
        "Failed",
        "PAYPAL_RECONCILIATION_MISMATCH",
        "La respuesta de PayPal no coincide con el checkout almacenado.",
        ct);
    }
  }

  private async Task ProcessRefundEventAsync(
    PublicSiteBinding binding,
    RecoveryRow row,
    IPayPalOrdersClient client,
    CancellationToken ct)
  {
    var providerRefundId = ResolveRefundEventId(row);
    if (providerRefundId is null)
    {
      await FinishRecoveryAsync(
        binding,
        row,
        "Failed",
        "PAYPAL_REFUND_EVENT_ID_MISSING",
        "El webhook de reembolso no incluyó una identidad de proveedor conciliable.",
        ct,
        ManualReviewRetryDelay);
      return;
    }

    var refund = await client.GetRefundAsync(providerRefundId, ct);
    if (!string.Equals(refund.RefundId, providerRefundId, StringComparison.Ordinal)
        || string.IsNullOrWhiteSpace(refund.CaptureId)
        || refund.Amount.Value <= 0
        || string.IsNullOrWhiteSpace(refund.Amount.Currency))
      throw new InvalidOperationException("PayPal refund event metadata mismatch.");

    RefundEventBindResult result;
    await using (var connection = await OpenAsync(binding, ct))
    {
      result = await connection.QuerySingleAsync<RefundEventBindResult>(new CommandDefinition(
        "restaurante.PaymentGatewayRefundEventBind",
        new
        {
          EventId = row.EventId,
          row.LeaseId,
          ProviderRefundId = refund.RefundId,
          ProviderCaptureId = refund.CaptureId,
          Amount = refund.Amount.Value,
          CurrencyCode = refund.Amount.Currency
        },
        commandType: CommandType.StoredProcedure,
        cancellationToken: ct));
    }

    if (string.Equals(result.MatchOutcome, "Matched", StringComparison.Ordinal)
        && result.RefundId.HasValue
        && string.Equals(result.ProviderRefundId, refund.RefundId, StringComparison.Ordinal))
    {
      await FinishRecoveryAsync(binding, row, "Processed", null, null, ct);
      return;
    }

    var outcome = result.MatchOutcome?.Trim().ToUpperInvariant() switch
    {
      "AMBIGUOUS" => "AMBIGUOUS",
      "CONFLICT" => "CONFLICT",
      "UNSUPPORTED" => "UNSUPPORTED",
      _ => "UNMATCHED"
    };
    await FinishRecoveryAsync(
      binding,
      row,
      "Failed",
      $"PAYPAL_REFUND_EVENT_{outcome}",
      "El webhook de reembolso requiere conciliación manual.",
      ct,
      ManualReviewRetryDelay);
  }

  private async Task ProcessRefundAsync(
    PublicSiteBinding binding,
    RefundRow row,
    CancellationToken ct)
  {
    PayPalRefundResult? refund = null;
    try
    {
      var client = _clients.Resolve(row.MerchantProfileKey);
      refund = string.IsNullOrWhiteSpace(row.ProviderRefundId)
        ? await client.RefundCaptureAsync(
          row.ProviderCaptureId,
          new PayPalRefundRequest
          {
            Amount = new PayPalMoney { Value = row.Amount, Currency = row.CurrencyCode },
            InvoiceId = RestaurantPayPalMetadataPolicy.RefundInvoiceId(binding.PublicSiteKey, row.Id),
            NoteToPayer = "Reembolso de pedido para recoger"
          },
          row.IdempotencyKey,
          ct)
        : await client.GetRefundAsync(row.ProviderRefundId, ct);
      ValidateRefund(refund, row);

      var outcome = refund.IsCompleted
        ? "Completed"
        : string.Equals(refund.Status, "PENDING", StringComparison.OrdinalIgnoreCase)
          ? "Pending"
          : "Failed";
      await FinishRefundAsync(
        binding,
        row,
        outcome,
        refund.RefundId,
        refund.SellerPayableBreakdown,
        outcome == "Failed" ? "PAYPAL_REFUND_DENIED" : null,
        outcome == "Failed" ? "PayPal no aprobó el reembolso." : null,
        ct);
    }
    catch (PayPalClientException exception)
    {
      LogProviderFailure("refund", row.CheckoutAttemptId, exception);
      var acceptedRefundId = string.IsNullOrWhiteSpace(row.ProviderRefundId) ? null : row.ProviderRefundId;
      await FinishRefundAsync(
        binding,
        row,
        exception.IsTransient ? "Pending" : "Failed",
        providerRefundId: acceptedRefundId,
        providerBreakdown: null,
        exception.ProviderErrorCode,
        "No fue posible completar el reembolso con PayPal.",
        ct);
    }
    catch (InvalidOperationException exception)
    {
      _logger.LogWarning("PayPal refund validation failed for checkout {CheckoutAttemptId}: {Reason}", row.CheckoutAttemptId, exception.Message);
      var acceptedRefundId = !string.IsNullOrWhiteSpace(refund?.RefundId)
        ? refund.RefundId
        : row.ProviderRefundId;
      await FinishRefundAsync(
        binding,
        row,
        "Failed",
        providerRefundId: acceptedRefundId,
        providerBreakdown: null,
        "PAYPAL_REFUND_MISMATCH",
        "La respuesta de PayPal no coincide con el reembolso solicitado.",
        ct);
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
      "restaurante.PayPalRecoveryResult",
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

  private async Task SetAttemptStateAsync(
    PublicSiteBinding binding,
    RecoveryRow row,
    string state,
    string failureCode,
    string failureMessage,
    CancellationToken ct)
  {
    try
    {
      await using var connection = await OpenAsync(binding, ct);
      _ = await connection.QuerySingleAsync<dynamic>(new CommandDefinition(
        "restaurante.OnlineCheckoutStateSet",
        new
        {
          Id = row.CheckoutAttemptId,
          ExpectedState = row.State,
          State = state,
          FailureCode = failureCode,
          FailureMessage = failureMessage,
          NextRetryAtUtc = (DateTime?)null,
          RestaurantOrderId = (Guid?)null
        },
        commandType: CommandType.StoredProcedure,
        cancellationToken: ct));
    }
    catch (Microsoft.Data.SqlClient.SqlException exception) when (exception.Number == 53652)
    {
      // Another browser request or webhook can legitimately advance the
      // checkout after this recovery row was leased. Confirm that a durable
      // state won before treating the compare-and-set conflict as handled.
      await using var connection = await OpenAsync(binding, ct);
      var current = await connection.QuerySingleOrDefaultAsync<ConcurrentAttemptState>(new CommandDefinition(
        "restaurante.OnlineCheckoutAttemptGet",
        new { Id = row.CheckoutAttemptId },
        commandType: CommandType.StoredProcedure,
        cancellationToken: ct));
      if (current is null || string.Equals(current.State, row.State, StringComparison.Ordinal))
        throw;

      _logger.LogInformation(
        "PayPal recovery state update for checkout {CheckoutAttemptId} was superseded by durable state {CheckoutState}.",
        row.CheckoutAttemptId,
        current.State);
    }
  }

  private async Task FinishRefundAsync(
    PublicSiteBinding binding,
    RefundRow row,
    string outcome,
    string? providerRefundId,
    PayPalSellerPayableBreakdown? providerBreakdown,
    string? failureCode,
    string? failureMessage,
    CancellationToken ct)
  {
    await using var connection = await OpenAsync(binding, ct);
    await connection.ExecuteAsync(new CommandDefinition(
      "restaurante.PaymentGatewayRefundResult",
      new
      {
        row.Id,
        row.LeaseId,
        Outcome = outcome,
        ProviderRefundId = providerRefundId,
        ProviderGrossAmount = providerBreakdown?.GrossAmount.Value,
        ProviderFeeAmount = providerBreakdown?.PayPalFee.Value,
        ProviderNetAmount = providerBreakdown?.NetAmount.Value,
        ReconciledAtUtc = providerBreakdown is null
          ? (DateTime?)null
          : _clock.GetUtcNow().UtcDateTime,
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

  private static void ValidateOrder(PayPalOrderResult order, PublicSiteBinding binding, RecoveryRow row)
  {
    if (!string.Equals(order.OrderId, row.PayPalOrderId, StringComparison.Ordinal))
      throw new InvalidOperationException("PayPal order id mismatch.");
    var unit = order.PurchaseUnits.SingleOrDefault()
      ?? throw new InvalidOperationException("PayPal purchase unit missing.");
    if (!string.Equals(unit.ReferenceId, RestaurantPayPalMetadataPolicy.ReferenceId(binding.PublicSiteKey, row.CheckoutAttemptId), StringComparison.Ordinal)
        || !string.Equals(unit.CustomId, RestaurantPayPalMetadataPolicy.CustomId(binding.PublicSiteKey, row.QuoteFingerprint), StringComparison.Ordinal)
        || !string.Equals(unit.InvoiceId, RestaurantPayPalMetadataPolicy.InvoiceId(binding.PublicSiteKey, row.CheckoutAttemptId), StringComparison.Ordinal)
        || unit.Amount.Value != row.Total
        || !string.Equals(unit.Amount.Currency, row.CurrencyCode, StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("PayPal purchase unit mismatch.");
  }

  private static void ValidateCapture(PayPalCaptureResult capture, PublicSiteBinding binding, RecoveryRow row)
  {
    if (string.IsNullOrWhiteSpace(capture.CaptureId)
        || !string.Equals(capture.OrderId, row.PayPalOrderId, StringComparison.Ordinal)
        || !string.Equals(capture.ReferenceId, RestaurantPayPalMetadataPolicy.ReferenceId(binding.PublicSiteKey, row.CheckoutAttemptId), StringComparison.Ordinal)
        || !string.Equals(capture.CustomId, RestaurantPayPalMetadataPolicy.CustomId(binding.PublicSiteKey, row.QuoteFingerprint), StringComparison.Ordinal)
        || !string.Equals(capture.InvoiceId, RestaurantPayPalMetadataPolicy.InvoiceId(binding.PublicSiteKey, row.CheckoutAttemptId), StringComparison.Ordinal)
        || capture.Amount.Value != row.Total
        || !string.Equals(capture.Amount.Currency, row.CurrencyCode, StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("PayPal capture mismatch.");
  }

  private static PayPalCaptureResult CompleteCaptureMetadata(
    PayPalCaptureResult capture,
    PayPalOrderResult verifiedOrder)
  {
    var unit = verifiedOrder.PurchaseUnits.SingleOrDefault()
      ?? throw new InvalidOperationException("PayPal purchase unit missing.");
    return new PayPalCaptureResult
    {
      OrderId = string.IsNullOrWhiteSpace(capture.OrderId) ? verifiedOrder.OrderId : capture.OrderId,
      OrderStatus = string.IsNullOrWhiteSpace(capture.OrderStatus) ? verifiedOrder.Status : capture.OrderStatus,
      CaptureId = capture.CaptureId,
      Status = capture.Status,
      StatusReason = capture.StatusReason,
      ReferenceId = string.IsNullOrWhiteSpace(capture.ReferenceId) ? unit.ReferenceId : capture.ReferenceId,
      CustomId = string.IsNullOrWhiteSpace(capture.CustomId) ? unit.CustomId : capture.CustomId,
      InvoiceId = string.IsNullOrWhiteSpace(capture.InvoiceId) ? unit.InvoiceId : capture.InvoiceId,
      Amount = capture.Amount,
      SellerReceivableBreakdown = capture.SellerReceivableBreakdown,
      Payer = capture.Payer
    };
  }

  private void LogProviderFailure(string operation, Guid checkoutAttemptId, PayPalClientException exception)
    => _logger.LogWarning(
      "PayPal {RecoveryOperation} failed for checkout {CheckoutAttemptId}: provider code {ProviderCode}, HTTP {StatusCode}, debug id {DebugId}.",
      operation,
      checkoutAttemptId,
      exception.ProviderErrorCode,
      exception.StatusCode,
      exception.DebugId ?? "<none>");

  private static TimeSpan RetryDelay(int attempts)
    => TimeSpan.FromSeconds(Math.Min(1800, Math.Pow(2, Math.Clamp(attempts, 1, 10)) * 10));
  // Event work reports the checkout's RecoveryAttempts, not the event's own attempts,
  // so a failure that needs a person would otherwise be re-claimed every 20 seconds.
  private static readonly TimeSpan ManualReviewRetryDelay = TimeSpan.FromMinutes(30);
  private static bool IsExplicitCaptureDenial(PayPalClientException exception)
  {
    if (!string.Equals(exception.Operation, "capture_order", StringComparison.Ordinal))
      return false;
    return exception.ProviderIssueCodes
      .Append(exception.ProviderErrorCode)
      .Any(code => code is not null && code.ToUpperInvariant() is
        "INSTRUMENT_DECLINED" or "PAYER_CANNOT_PAY" or "TRANSACTION_REFUSED"
        or "CARD_DECLINED" or "MAX_NUMBER_OF_PAYMENT_ATTEMPTS_EXCEEDED");
  }
  private static bool IsRefundEvent(string? eventType)
    => eventType?.StartsWith("PAYMENT.REFUND.", StringComparison.OrdinalIgnoreCase) == true;
  private static bool IsCaptureRefundOrReversalEvent(string? eventType)
    => string.Equals(eventType, "PAYMENT.CAPTURE.REFUNDED", StringComparison.OrdinalIgnoreCase)
      || string.Equals(eventType, "PAYMENT.CAPTURE.REVERSED", StringComparison.OrdinalIgnoreCase);
  private static bool IsCompletedCaptureRefundEvent(RecoveryRow row)
    => string.Equals(row.EventType, "PAYMENT.CAPTURE.REFUNDED", StringComparison.OrdinalIgnoreCase)
      && string.Equals(row.ResourceType, "refund", StringComparison.OrdinalIgnoreCase);
  private static string? ResolveRefundEventId(RecoveryRow row)
  {
    if (!string.Equals(row.ResourceType, "refund", StringComparison.OrdinalIgnoreCase))
      return null;
    var resourceId = NullIfWhiteSpace(row.ResourceId);
    var relatedRefundId = NullIfWhiteSpace(row.RelatedRefundId);
    if (resourceId is not null && relatedRefundId is not null
        && !string.Equals(resourceId, relatedRefundId, StringComparison.Ordinal))
      return null;
    return relatedRefundId ?? resourceId;
  }
  private static void ValidateRefund(PayPalRefundResult refund, RefundRow row)
  {
    if (string.IsNullOrWhiteSpace(refund.RefundId)
        || !string.Equals(refund.CaptureId, row.ProviderCaptureId, StringComparison.Ordinal)
        || refund.Amount.Value != row.Amount
        || !string.Equals(refund.Amount.Currency, row.CurrencyCode, StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("PayPal refund metadata mismatch.");

    var breakdown = refund.SellerPayableBreakdown;
    if (breakdown is null) return;
    if (breakdown.GrossAmount.Value != row.Amount
        || breakdown.PayPalFee.Value < 0
        || breakdown.NetAmount.Value < 0
        || breakdown.GrossAmount.Value != breakdown.PayPalFee.Value + breakdown.NetAmount.Value
        || !MoneyUsesCurrency(breakdown.GrossAmount, row.CurrencyCode)
        || !MoneyUsesCurrency(breakdown.PayPalFee, row.CurrencyCode)
        || !MoneyUsesCurrency(breakdown.NetAmount, row.CurrencyCode))
      throw new InvalidOperationException("PayPal refund settlement metadata mismatch.");
  }
  private static bool MoneyUsesCurrency(PayPalMoney money, string currency)
    => string.Equals(money.Currency, currency, StringComparison.OrdinalIgnoreCase);
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
    public string PayPalOrderId { get; set; } = string.Empty;
    public string? PayPalCaptureId { get; set; }
    public string MerchantProfileKey { get; set; } = string.Empty;
    public string QuoteFingerprint { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public string CurrencyCode { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public int RecoveryAttempts { get; set; }
    public Guid LeaseId { get; set; }
  }

  private sealed class RefundEventBindResult
  {
    public string MatchOutcome { get; set; } = string.Empty;
    public Guid? RefundId { get; set; }
    public bool WasBound { get; set; }
    public string? RefundStatus { get; set; }
    public string? ProviderRefundId { get; set; }
  }

  private sealed class ConcurrentAttemptState
  {
    public string State { get; set; } = string.Empty;
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
