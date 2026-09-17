using System.Reflection;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantCaptureRefundReconciliationTests
{
  private const string LegacyMigrationPath =
    "src/OrionERP.Infrastructure/Features/Restaurante/Sql/20260914_restaurant_online_ordering_capture_refund_reconciliation.sql";
  private const string RecoveryPath =
    "src/OrionERP.Infrastructure/Features/Restaurante/RestaurantPaymentRecoveryProcessor.cs";

  [Fact]
  public void ClipNotification_IsAcceptedOnlyByItsShape()
  {
    // Forma exacta del aviso de Clip: tres campos cortos y sin firma. No hay
    // nada que verificar criptograficamente, asi que lo unico que se puede
    // exigir es la forma; la verdad se consulta despues con GET /payments.
    const string payload = """
      { "id": "1960c5eb-d9ed-4a55-8d65-a377b5", "origin": "payments-api", "event_type": "UPDATE" }
      """;

    Assert.True(TryParse(payload, out var notification));
    Assert.Equal("1960c5eb-d9ed-4a55-8d65-a377b5", ReadProperty(notification!, "Id"));
    Assert.Equal("UPDATE", ReadProperty(notification!, "EventType"));
    Assert.Equal("payments-api", ReadProperty(notification!, "Origin"));
  }

  [Theory]
  [InlineData("")]
  [InlineData("{")]
  [InlineData("[]")]
  [InlineData("{\"origin\":\"payments-api\",\"event_type\":\"UPDATE\"}")]
  [InlineData("{\"id\":\"PAY-17\"}")]
  [InlineData("{\"id\":17,\"event_type\":\"UPDATE\"}")]
  public void ClipNotification_RejectsAnythingThatIsNotOne(string payload)
    => Assert.False(TryParse(payload, out _));

  [Fact]
  public void ClipNotification_RejectsIdentitiesLongerThanClipCanIssue()
  {
    Assert.False(TryParse($$"""{"id":"{{new string('I', 65)}}","event_type":"UPDATE"}""", out _));
    Assert.False(TryParse($$"""{"id":"PAY-17","event_type":"{{new string('E', 31)}}"}""", out _));
  }

  [Fact]
  public void ExternalRefundsReconcileBeforeTheAttemptIsTreatedAsSettled()
  {
    var recovery = RepoFile.Read(RecoveryPath);
    var chargeRoute = recovery.IndexOf("if (IsAwaitingCharge(row.State))", StringComparison.Ordinal);
    var refundRoute = recovery.IndexOf(
      "if (payment.AmountRefunded > 0 && !IsRefundState(row.State))",
      StringComparison.Ordinal);

    Assert.True(chargeRoute >= 0 && refundRoute > chargeRoute,
      "A pending charge must be resolved before a settled attempt is checked for external refunds.");
    Assert.Contains("CLIP_EXTERNAL_REFUND_REQUIRES_RECONCILIATION", recovery[refundRoute..], StringComparison.Ordinal);
    Assert.Contains("ManualReviewRetryDelay = TimeSpan.FromMinutes(30)", recovery, StringComparison.Ordinal);
    Assert.Contains("retryDelay ?? RetryDelay(row.RecoveryAttempts)", recovery, StringComparison.Ordinal);
  }

  [Fact]
  public void RefundBind_AcceptsCaptureRefundedButStillRequiresTheProviderCapture()
  {
    // El checkout de Bruno's ya no usa este procedimiento —Clip no avisa de
    // reembolsos—, pero sigue instalado y con permiso, asi que sus guardas
    // siguen siendo parte de la superficie de la base.
    var migration = RepoFile.Read(LegacyMigrationPath);

    Assert.Contains("@EventType<>''PAYMENT.CAPTURE.REFUNDED''", migration, StringComparison.Ordinal);
    Assert.Contains("(@RelatedCaptureId IS NULL AND @EventType LIKE ''PAYMENT.REFUND.%'')", migration, StringComparison.Ordinal);
    Assert.Contains("(@RelatedCaptureId IS NOT NULL AND @RelatedCaptureId<>@ProviderCaptureId)", migration, StringComparison.Ordinal);
    Assert.Contains("AND @KnownCaptureId=@ProviderCaptureId", migration, StringComparison.Ordinal);
    Assert.Contains("AND transactionInfo.ProviderCaptureId=@ProviderCaptureId", migration, StringComparison.Ordinal);
    Assert.Contains("AND @KnownAmount=@Amount AND @KnownCurrencyCode=@CurrencyCode", migration, StringComparison.Ordinal);
  }

  private static bool TryParse(string payload, out object? notification)
  {
    var service = Type.GetType(
      "OrionERP.Infrastructure.Features.Restaurante.RestaurantOnlineCheckoutService, OrionERP.Infrastructure",
      throwOnError: true)!;
    var method = service.GetMethod("TryParseClipNotification", BindingFlags.NonPublic | BindingFlags.Static)
      ?? throw new InvalidOperationException("TryParseClipNotification was not found.");
    object?[] arguments = [payload, null];
    var parsed = (bool)method.Invoke(null, arguments)!;
    notification = arguments[1];
    return parsed;
  }

  private static string? ReadProperty(object notification, string name)
    => (string?)(notification.GetType().GetProperty(name)
      ?? throw new InvalidOperationException($"{name} was not found.")).GetValue(notification);
}
