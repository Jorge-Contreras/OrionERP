namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantLoyaltyCancellationTests
{
  [Fact]
  public void Cancellation_ReversesPointsAndClearsTheOrderAwardInTheSameWorkflow()
  {
    var orderService = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOrderService.cs");
    var loyaltyService = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/LoyaltyService.cs");
    var cancellationStart = orderService.IndexOf(
      "public async Task<RestaurantCommandResult> CancelOrderAsync",
      StringComparison.Ordinal);
    var paymentsStart = orderService.IndexOf(
      "public async Task<IReadOnlyList<RestaurantPaymentDto>> GetPaymentsAsync",
      cancellationStart,
      StringComparison.Ordinal);
    var cancellationWorkflow = orderService[cancellationStart..paymentsStart];

    Assert.Contains("ReverseCancelledOrderAsync", cancellationWorkflow, StringComparison.Ordinal);
    Assert.Contains("LoyaltyPointsReversed", cancellationWorkflow, StringComparison.Ordinal);
    Assert.Contains("SET PointsEarned=0", loyaltyService, StringComparison.Ordinal);
    Assert.Contains("'CancellationReversal'", loyaltyService, StringComparison.Ordinal);
  }

  [Fact]
  public void MemberPanel_LoadsAllLinkedOrdersAndDisplaysTheirCurrentStatus()
  {
    var loyaltyService = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/LoyaltyService.cs");
    var memberPage = ReadRepoFile("src/OrionERP.Bruno.Web/Pages/Account/Member/Index.cshtml");
    var profileStart = loyaltyService.IndexOf("private async Task<LoyaltyMemberProfileDto?> LoadProfileAsync", StringComparison.Ordinal);
    var mapStart = loyaltyService.IndexOf("private static LoyaltyMemberDto MapMember", profileStart, StringComparison.Ordinal);
    var profileQuery = loyaltyService[profileStart..mapStart];

    Assert.Contains("profile.OrderHistory", profileQuery, StringComparison.Ordinal);
    Assert.Contains("orderInfo.MemberId=@MemberId", profileQuery, StringComparison.Ordinal);
    Assert.Contains("CASE WHEN orderInfo.[Status]='Cancelled' THEN 0", profileQuery, StringComparison.Ordinal);
    Assert.Contains("Mis órdenes", memberPage, StringComparison.Ordinal);
    Assert.Contains("OrderStatusLabel(row.Status)", memberPage, StringComparison.Ordinal);
    Assert.Contains("\"Cancelled\" => \"Cancelada\"", memberPage, StringComparison.Ordinal);
  }

  [Fact]
  public void ReconciliationScript_IsDryRunCapableAndDoesNotRemoveCancelledOrders()
  {
    var migration = ReadRepoFile(
      "src/OrionERP.Infrastructure/Features/Restaurante/Sql/20260802_cancelled_order_loyalty_reconciliation.sql");

    Assert.Contains("ExpectedDatabase", migration, StringComparison.Ordinal);
    Assert.Contains("ApplyChanges", migration, StringComparison.Ordinal);
    Assert.Contains("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE", migration, StringComparison.Ordinal);
    Assert.Contains("'CancellationReversal'", migration, StringComparison.Ordinal);
    Assert.Contains("SET PointsEarned=0", migration, StringComparison.Ordinal);
    Assert.Contains("WHEN @OutstandingPoints<@CurrentBalance THEN @OutstandingPoints", migration, StringComparison.Ordinal);
    Assert.Contains("ROLLBACK TRANSACTION", migration, StringComparison.Ordinal);
    Assert.DoesNotContain("DELETE FROM restaurante.[Order]", migration, StringComparison.OrdinalIgnoreCase);
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath)));
}
