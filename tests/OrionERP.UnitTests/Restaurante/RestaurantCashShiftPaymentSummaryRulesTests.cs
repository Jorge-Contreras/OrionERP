using OrionERP.Application.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantCashShiftPaymentSummaryRulesTests
{
  [Fact]
  public void Combine_MergesLegacyAliasesAndPreservesFinancialTotals()
  {
    var result = RestaurantCashShiftPaymentSummaryRules.Combine(
    [
      new() { PaymentMethod = "Cash", PaymentCount = 1, Sales = 100m, Tips = 10m },
      new() { PaymentMethod = "cash", RefundCount = 1, Refunds = 5m },
      new() { PaymentMethod = "Card", PaymentCount = 1, Sales = 40m },
      new() { PaymentMethod = "ExternalCard", PaymentCount = 2, Sales = 60m, Tips = 3m, RefundCount = 1, Refunds = 8m },
      new() { PaymentMethod = "DeliveryProvider", PaymentCount = 1, Sales = 20m },
      new() { PaymentMethod = "Platform", RefundCount = 1, Refunds = 2m },
      new() { PaymentMethod = "Transfer", PaymentCount = 1, Sales = 30m }
    ]);

    Assert.Equal(["Cash", "ExternalCard", "Transfer", "Platform"], result.Select(method => method.PaymentMethod));

    var cash = result[0];
    Assert.Equal(1, cash.PaymentCount);
    Assert.Equal(1, cash.RefundCount);
    Assert.Equal(100m, cash.Sales);
    Assert.Equal(10m, cash.Tips);
    Assert.Equal(5m, cash.Refunds);
    Assert.Equal(105m, cash.NetTotal);

    var card = result[1];
    Assert.Equal(3, card.PaymentCount);
    Assert.Equal(100m, card.Sales);
    Assert.Equal(3m, card.Tips);
    Assert.Equal(8m, card.Refunds);
    Assert.Equal(95m, card.NetTotal);

    Assert.Equal(250m, result.Sum(method => method.Sales));
    Assert.Equal(13m, result.Sum(method => method.Tips));
    Assert.Equal(15m, result.Sum(method => method.Refunds));
    Assert.Equal(248m, result.Sum(method => method.NetTotal));
  }

  [Fact]
  public void Combine_KeepsUnknownAndMissingMethodsVisible()
  {
    var result = RestaurantCashShiftPaymentSummaryRules.Combine(
    [
      new() { PaymentMethod = "Voucher", PaymentCount = 1, Sales = 25m },
      new() { PaymentMethod = " ", PaymentCount = 1, Sales = 15m }
    ]);

    Assert.Contains(result, method => method.PaymentMethod == "Voucher" && method.Sales == 25m);
    Assert.Contains(result, method => method.PaymentMethod == "Sin especificar" && method.Sales == 15m);
  }
}
