using OrionERP.Application.Features.Logistica.Purchasing;

namespace OrionERP.UnitTests.Logistica;

public sealed class PurchaseReceiptAmountCalculatorTests
{
  [Fact]
  public void Calculate_WhenIvaIsIncluded_ReconcilesToReceiptTotal()
  {
    var result = PurchaseReceiptAmountCalculator.Calculate(116m, includesIva: true);

    Assert.Equal(100m, result.SubtotalAmount);
    Assert.Equal(16m, result.IvaAmount);
    Assert.Equal(116m, result.TotalAmount);
    Assert.Equal(result.TotalAmount, result.SubtotalAmount + result.IvaAmount);
  }

  [Fact]
  public void Calculate_WhenItemDoesNotIncludeIva_KeepsFullAmountAsSubtotal()
  {
    var result = PurchaseReceiptAmountCalculator.Calculate(21m, includesIva: false);

    Assert.Equal(21m, result.SubtotalAmount);
    Assert.Equal(0m, result.IvaAmount);
    Assert.Equal(21m, result.TotalAmount);
  }

  [Fact]
  public void CalculateBaseUnitCost_UsesNetAmountAndReceivedBaseQuantity()
  {
    var result = PurchaseReceiptAmountCalculator.CalculateBaseUnitCost(116m, includesIva: true, baseQuantity: 2m);

    Assert.Equal(50m, result);
  }
}
