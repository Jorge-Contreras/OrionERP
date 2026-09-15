using OrionERP.Application.Features.Restaurante;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantOnlineRefundBalanceAndTrackingTests
{
  [Fact]
  public void RefundRequests_ReopenTheBalanceByDefaultForPosCorrections()
  {
    Assert.True(new RestaurantPaymentRefundRequest().ReopenBalance);
  }

  [Fact]
  public void ProviderRefunds_KeepTheOrderBalanceInsteadOfReopeningIt()
  {
    // A PayPal partial refund left a paid web order showing "Saldo pendiente" in the
    // 2026-09-14 sandbox test, as if the customer still owed the refunded amount.
    var orderService = RepoFile.Read("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOrderService.cs");
    Assert.Contains("orderInfo.Total,orderInfo.BalanceDue,orderInfo.[Status] AS OrderStatus", orderService, StringComparison.Ordinal);
    Assert.Contains("var balanceDue = request.ReopenBalance", orderService, StringComparison.Ordinal);
    Assert.Contains(": payment.BalanceDue;", orderService, StringComparison.Ordinal);

    var importer = RepoFile.Read("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOnlineOrderImportProcessor.cs");
    Assert.Contains("ReopenBalance = false", importer, StringComparison.Ordinal);
  }

  [Fact]
  public void TrackingPage_OnlyClaimsTheReadyEmailOnceTheQueueSentIt()
  {
    var page = RepoFile.Read("src/OrionERP.Bruno.Web/Features/Ordering/BrunoOrderStatusPage.razor");
    Assert.DoesNotContain("ReadyStepComplete ? \"Te enviamos un correo\"", page, StringComparison.Ordinal);
    Assert.Contains("IsReadyEmailSent ? \"Te enviamos un correo\"", page, StringComparison.Ordinal);
    Assert.Contains("NormalizedOrder == RestaurantOrderStatuses.Ready && IsReadyEmailInFlight", page, StringComparison.Ordinal);
  }

  [Fact]
  public void StatusProcedure_ReturnsOnlyTheTrackedCheckoutsReadyEmailStatus()
  {
    var migration = RepoFile.Read(
      "src/OrionERP.Infrastructure/Features/Restaurante/Sql/20260914_restaurant_online_ordering_tracking_ready_email_status.sql");
    Assert.Contains("readyNotification.[Status] ReadyNotificationStatus", migration, StringComparison.Ordinal);
    Assert.Contains("notification.CheckoutAttemptId=attempt.Id", migration, StringComparison.Ordinal);
    Assert.Contains("notification.NotificationType=''Ready''", migration, StringComparison.Ordinal);
    Assert.Contains("attempt.TrackingTokenHash=@TrackingTokenHash", migration, StringComparison.Ordinal);
    Assert.Contains("SESSION_CONTEXT(N''OrionERP.PublicSiteId'')", migration, StringComparison.Ordinal);
  }
}
