using OrionERP.Application.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantCashTenderCalculatorTests
{
  [Fact]
  public void Calculate_ReturnsChangeWithoutOverpayingTheOrder()
  {
    var result = RestaurantCashTenderCalculator.Calculate(
      orderTotal: 34m,
      cardAmount: 0,
      transferAmount: 0,
      cashReceived: 100m);

    Assert.Equal(34m, result.CashRequired);
    Assert.Equal(34m, result.CashAppliedToOrder);
    Assert.Equal(66m, result.Change);
    Assert.Equal(0m, result.Shortfall);
  }

  [Fact]
  public void Calculate_UsesElectronicPaymentsBeforeCash()
  {
    var result = RestaurantCashTenderCalculator.Calculate(
      orderTotal: 100m,
      cardAmount: 40m,
      transferAmount: 10m,
      cashReceived: 100m);

    Assert.Equal(50m, result.CashRequired);
    Assert.Equal(50m, result.CashAppliedToOrder);
    Assert.Equal(50m, result.Change);
  }

  [Fact]
  public void Calculate_ReportsCashShortfall()
  {
    var result = RestaurantCashTenderCalculator.Calculate(
      orderTotal: 34m,
      cardAmount: 0,
      transferAmount: 0,
      cashReceived: 20m);

    Assert.Equal(20m, result.CashAppliedToOrder);
    Assert.Equal(0m, result.Change);
    Assert.Equal(14m, result.Shortfall);
  }

  [Fact]
  public void Calculate_IncludesCashTipBeforeCalculatingChange()
  {
    var result = RestaurantCashTenderCalculator.Calculate(
      orderTotal: 34m,
      cardAmount: 0,
      transferAmount: 0,
      cashReceived: 100m,
      cashTip: 10m);

    Assert.Equal(44m, result.CashRequired);
    Assert.Equal(34m, result.CashAppliedToOrder);
    Assert.Equal(56m, result.Change);
  }

  [Fact]
  public void Pos_RecordsOnlyAppliedCashAndShowsTheChange()
  {
    var page = File.ReadAllText(Path.GetFullPath(Path.Combine(
      AppContext.BaseDirectory,
      "../../../../../src/OrionERP.Web/Features/Restaurante/RestaurantPosPage.razor")));

    Assert.Contains("Recibido en efectivo", page, StringComparison.Ordinal);
    Assert.Contains("Cambio a entregar", page, StringComparison.Ordinal);
    Assert.Contains("Amount = PosCash.CashAppliedToOrder", page, StringComparison.Ordinal);
  }
}
