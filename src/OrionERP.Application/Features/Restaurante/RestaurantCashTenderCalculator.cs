namespace OrionERP.Application.Features.Restaurante;

public readonly record struct RestaurantCashTenderBreakdown(
  decimal CashRequired,
  decimal CashAppliedToOrder,
  decimal Change,
  decimal Shortfall);

public static class RestaurantCashTenderCalculator
{
  public static RestaurantCashTenderBreakdown Calculate(
    decimal orderTotal,
    decimal cardAmount,
    decimal transferAmount,
    decimal cashReceived,
    decimal cashTip = 0)
  {
    var normalizedTotal = RoundCurrency(Math.Max(0, orderTotal));
    var normalizedCard = RoundCurrency(Math.Max(0, cardAmount));
    var normalizedTransfer = RoundCurrency(Math.Max(0, transferAmount));
    var normalizedCashReceived = RoundCurrency(Math.Max(0, cashReceived));
    var normalizedCashTip = RoundCurrency(Math.Max(0, cashTip));

    var orderCashDue = RoundCurrency(Math.Max(0, normalizedTotal - normalizedCard - normalizedTransfer));
    var cashRequired = RoundCurrency(orderCashDue + normalizedCashTip);
    var cashAppliedToOrder = RoundCurrency(Math.Min(normalizedCashReceived, orderCashDue));
    var change = RoundCurrency(Math.Max(0, normalizedCashReceived - cashRequired));
    var shortfall = RoundCurrency(Math.Max(0, cashRequired - normalizedCashReceived));

    return new RestaurantCashTenderBreakdown(cashRequired, cashAppliedToOrder, change, shortfall);
  }

  private static decimal RoundCurrency(decimal value)
    => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
