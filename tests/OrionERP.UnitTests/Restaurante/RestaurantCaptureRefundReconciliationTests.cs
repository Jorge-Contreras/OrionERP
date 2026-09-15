using System.Reflection;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantCaptureRefundReconciliationTests
{
  private const string MigrationPath =
    "src/OrionERP.Infrastructure/Features/Restaurante/Sql/20260914_restaurant_online_ordering_capture_refund_reconciliation.sql";

  [Fact]
  public void ParseWebhook_ReadsTheCaptureOfARefundFromItsUpLink()
  {
    // Shape of the PAYMENT.CAPTURE.REFUNDED event PayPal Sandbox delivered on 2026-09-14:
    // the resource is the refund and there is no supplementary_data.related_ids.
    const string payload = """
      {
        "id": "WH-REFUND-TEST",
        "event_type": "PAYMENT.CAPTURE.REFUNDED",
        "resource_type": "refund",
        "resource": {
          "id": "1RF12345AB678901C",
          "status": "COMPLETED",
          "amount": { "value": "20.00", "currency_code": "MXN" },
          "links": [
            { "href": "https://api.sandbox.paypal.com/v2/payments/refunds/1RF12345AB678901C", "rel": "self", "method": "GET" },
            { "href": "https://api.sandbox.paypal.com/v2/payments/captures/8T399360NX3048442", "rel": "up", "method": "GET" }
          ]
        }
      }
      """;

    var envelope = ParseWebhook(payload);

    Assert.Equal("1RF12345AB678901C", ReadProperty(envelope, "ResourceId"));
    Assert.Equal("8T399360NX3048442", ReadProperty(envelope, "RelatedCaptureId"));
    Assert.Null(ReadProperty(envelope, "RelatedRefundId"));
  }

  [Fact]
  public void ParseWebhook_DoesNotTreatACaptureOrderLinkAsACapture()
  {
    const string payload = """
      {
        "id": "WH-CAPTURE-TEST",
        "event_type": "PAYMENT.CAPTURE.COMPLETED",
        "resource_type": "capture",
        "resource": {
          "id": "8T399360NX3048442",
          "status": "COMPLETED",
          "supplementary_data": { "related_ids": { "order_id": "51C88532F23689731" } },
          "links": [
            { "href": "https://api.sandbox.paypal.com/v2/checkout/orders/51C88532F23689731", "rel": "up", "method": "GET" }
          ]
        }
      }
      """;

    var envelope = ParseWebhook(payload);

    Assert.Equal("51C88532F23689731", ReadProperty(envelope, "RelatedOrderId"));
    Assert.Null(ReadProperty(envelope, "RelatedCaptureId"));
  }

  [Fact]
  public void CompletedCaptureRefunds_ReconcileBeforeTheManualReviewBranch()
  {
    var recovery = RepoFile.Read("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantPayPalRecoveryProcessor.cs");
    var completedRoute = recovery.IndexOf("IsCompletedCaptureRefundEvent(row))", StringComparison.Ordinal);
    var manualRoute = recovery.IndexOf("IsCaptureRefundOrReversalEvent(row.EventType))", StringComparison.Ordinal);

    Assert.True(completedRoute >= 0 && manualRoute > completedRoute,
      "Completed refunds must be reconciled before reversals fall through to manual review.");
    Assert.Contains("await ProcessRefundEventAsync(binding, row, client, ct);", recovery[completedRoute..manualRoute], StringComparison.Ordinal);
    Assert.Contains("ManualReviewRetryDelay = TimeSpan.FromMinutes(30)", recovery, StringComparison.Ordinal);
    Assert.Contains("retryDelay ?? RetryDelay(row.RecoveryAttempts)", recovery, StringComparison.Ordinal);
  }

  [Fact]
  public void RefundBind_AcceptsCaptureRefundedButStillRequiresTheProviderCapture()
  {
    var migration = RepoFile.Read(MigrationPath);

    Assert.Contains("@EventType<>''PAYMENT.CAPTURE.REFUNDED''", migration, StringComparison.Ordinal);
    Assert.Contains("(@RelatedCaptureId IS NULL AND @EventType LIKE ''PAYMENT.REFUND.%'')", migration, StringComparison.Ordinal);
    Assert.Contains("(@RelatedCaptureId IS NOT NULL AND @RelatedCaptureId<>@ProviderCaptureId)", migration, StringComparison.Ordinal);
    Assert.Contains("AND @KnownCaptureId=@ProviderCaptureId", migration, StringComparison.Ordinal);
    Assert.Contains("AND transactionInfo.ProviderCaptureId=@ProviderCaptureId", migration, StringComparison.Ordinal);
    Assert.Contains("AND @KnownAmount=@Amount AND @KnownCurrencyCode=@CurrencyCode", migration, StringComparison.Ordinal);
  }

  private static object ParseWebhook(string payload)
  {
    var service = Type.GetType(
      "OrionERP.Infrastructure.Features.Restaurante.RestaurantOnlineCheckoutService, OrionERP.Infrastructure",
      throwOnError: true)!;
    var method = service.GetMethod("ParseWebhook", BindingFlags.NonPublic | BindingFlags.Static)
      ?? throw new InvalidOperationException("ParseWebhook was not found.");
    return method.Invoke(null, [payload])!;
  }

  private static string? ReadProperty(object envelope, string name)
    => (string?)(envelope.GetType().GetProperty(name)
      ?? throw new InvalidOperationException($"{name} was not found.")).GetValue(envelope);
}
