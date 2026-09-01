namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantPosPointsRedemptionFlowTests
{
  [Fact]
  public void Pos_SubmitsPointsOnlyThroughAtomicOrderCreation()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPosPage.razor");
    var orderService = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOrderService.cs");
    var loyaltyContract = ReadRepoFile("src/OrionERP.Application/Features/Restaurante/ILoyaltyService.cs");
    var adminPage = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPromotionsPage.razor");

    Assert.Contains("PointsToRedeem=appliedRedemptionPoints", page, StringComparison.Ordinal);
    Assert.Contains("PrepareOrderRedemptionAsync", orderService, StringComparison.Ordinal);
    Assert.Contains("ApplyOrderRedemptionAsync", orderService, StringComparison.Ordinal);
    Assert.Contains("order:{orderId:N}:redeem", ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/LoyaltyService.cs"), StringComparison.Ordinal);
    Assert.DoesNotContain("RedeemPointsAsync", loyaltyContract, StringComparison.Ordinal);
    Assert.DoesNotContain("Canjear y generar vale", adminPage, StringComparison.Ordinal);
  }

  [Fact]
  public void Pos_SupportsTipOnlyCashWhenPointsCoverTheOrder()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPosPage.razor");
    var orderService = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOrderService.cs");
    var migration = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/Sql/20260830_bruno_pos_points_redemption.sql");

    Assert.Contains("PosCash.CashAppliedToOrder > 0.01m || tipAmount > 0.01m", page, StringComparison.Ordinal);
    Assert.Contains("payment.Amount + payment.TipAmount <= 0", orderService, StringComparison.Ordinal);
    Assert.Contains("Amount+TipAmount>0", migration, StringComparison.Ordinal);
  }

  [Fact]
  public void RefundWorkflow_ReversesEarnedPointsBeforeRestoringRedeemedPoints()
  {
    var orderService = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOrderService.cs");
    var refundStart = orderService.IndexOf("public async Task<RestaurantCommandResult> RefundPaymentAsync", StringComparison.Ordinal);
    var refundWorkflow = orderService[refundStart..];
    var earnedReversal = refundWorkflow.IndexOf("ReverseRefundAsync", StringComparison.Ordinal);
    var redemptionRestoration = refundWorkflow.IndexOf("RestoreRefundedRedemptionAsync", StringComparison.Ordinal);

    Assert.True(earnedReversal >= 0);
    Assert.True(redemptionRestoration > earnedReversal);
    Assert.Contains("LoyaltyRedemptionRestored", refundWorkflow, StringComparison.Ordinal);
  }

  [Fact]
  public void SupervisorInventoryOverride_CoversStockAndConfigurationProblems()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPosPage.razor");
    var orderService = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOrderService.cs");

    Assert.DoesNotContain("Autorizar cualquier excepción de inventario", page, StringComparison.Ordinal);
    Assert.Contains("RestaurantInventoryOverrideRequiredException", orderService, StringComparison.Ordinal);
    Assert.Contains("catch (RestaurantInventoryOverrideRequiredException ex)", page, StringComparison.Ordinal);
    Assert.Contains("OpenSupervisorOverride(SupervisorOverrideKind.Inventory", page, StringComparison.Ordinal);
    Assert.Contains("Autorizar y reintentar", page, StringComparison.Ordinal);
    Assert.Contains("if (!allowInventoryOverride)", orderService, StringComparison.Ordinal);
    Assert.Contains("@AllowInventoryOverride = 1 OR product.SoldOutOverride = 0", orderService, StringComparison.Ordinal);
    Assert.Contains("productsWithInventoryOverride", orderService, StringComparison.Ordinal);
    Assert.Contains("componentsWithInventoryOverride", orderService, StringComparison.Ordinal);
    Assert.Contains("fallbackLocation.HasValue", orderService, StringComparison.Ordinal);
    Assert.Contains("no tiene una ubicación de inventario configurada", orderService, StringComparison.Ordinal);
    Assert.Contains("InventoryDeficitAuthorized", orderService, StringComparison.Ordinal);
    Assert.DoesNotContain(
      "!site.AllowSupervisorDeficit || string.IsNullOrWhiteSpace(request.SupervisorAuthorizedBy)",
      orderService,
      StringComparison.Ordinal);
    Assert.DoesNotContain(
      "?? throw new InvalidOperationException(\"No hay una ubicación de inventario configurada",
      orderService,
      StringComparison.Ordinal);
  }

  [Fact]
  public void Pos_PutsClubBrunosFirstAndKeepsSupervisorCredentialsInTheContextualModal()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPosPage.razor");
    var checkoutStart = page.IndexOf("<section class=\"pos-order-options\">", StringComparison.Ordinal);

    Assert.True(checkoutStart >= 0);
    var membershipStart = page.IndexOf("<section class=\"pos-membership\"", checkoutStart, StringComparison.Ordinal);
    var orderModeStart = page.IndexOf("<div class=\"pos-segmented\"", checkoutStart, StringComparison.Ordinal);
    var discountsStart = page.IndexOf("<details class=\"pos-discounts\">", checkoutStart, StringComparison.Ordinal);
    Assert.True(discountsStart > orderModeStart);
    var discountsEnd = page.IndexOf("</details>", discountsStart, StringComparison.Ordinal);

    Assert.True(membershipStart > checkoutStart);
    Assert.True(orderModeStart > membershipStart);
    Assert.True(discountsEnd > discountsStart);
    var discountSection = page[discountsStart..discountsEnd];
    Assert.Contains("Pregunta primero si el cliente es miembro", page, StringComparison.Ordinal);
    Assert.DoesNotContain("supervisorIdentity", discountSection, StringComparison.Ordinal);
    Assert.DoesNotContain("supervisorPin", discountSection, StringComparison.Ordinal);
    Assert.DoesNotContain("Autorizar cualquier excepción", discountSection, StringComparison.Ordinal);
    Assert.Contains("isSupervisorOverrideOpen", page, StringComparison.Ordinal);
    Assert.Contains("Motivo del descuento", page, StringComparison.Ordinal);
    Assert.Contains("VerifySupervisorPinAsync", page, StringComparison.Ordinal);
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath)));
}
