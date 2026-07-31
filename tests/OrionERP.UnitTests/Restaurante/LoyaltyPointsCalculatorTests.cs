using OrionERP.Application.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class LoyaltyPointsCalculatorTests
{
  [Theory]
  [InlineData(0, 0)]
  [InlineData(9.99, 0)]
  [InlineData(10, 1)]
  [InlineData(29.99, 2)]
  [InlineData(30, 3)]
  public void EarnedPoints_UsesWholeTenPesoBlocks(decimal eligible, int expected)
  {
    Assert.Equal(expected, LoyaltyPointsCalculator.CalculateEarnedPoints(eligible, 10));
  }

  [Fact]
  public void EligibleAmount_IsCallerControlled_AndDoesNotCarryRemainders()
  {
    Assert.Equal(1, LoyaltyPointsCalculator.CalculateEarnedPoints(19.99m, 10));
    Assert.Equal(0, LoyaltyPointsCalculator.CalculateEarnedPoints(0.01m, 10));
  }

  [Fact]
  public void PartialRefund_RecalculatesPointsFromRetainedMerchandise()
  {
    var result = LoyaltyPointsCalculator.CalculateRefund(
      paidOrderTotal: 120,
      originalEligibleMerchandise: 100,
      cumulativeRefundedTotal: 60,
      originallyEarnedPoints: 10,
      pointsAlreadyReversed: 0,
      currentMemberBalance: 25,
      pesosPerPoint: 10);

    Assert.Equal(50, result.RetainedEligibleMerchandise);
    Assert.Equal(5, result.JustifiedRetainedPoints);
    Assert.Equal(5, result.PointsToReverse);
  }

  [Fact]
  public void TotalRefund_ReversesAllAvailableEarnedPoints()
  {
    var result = LoyaltyPointsCalculator.CalculateRefund(100, 89, 100, 8, 0, 20, 10);
    Assert.Equal(0, result.RetainedEligibleMerchandise);
    Assert.Equal(8, result.PointsToReverse);
  }

  [Fact]
  public void RepeatedRefund_AccountsForPriorReversals()
  {
    var result = LoyaltyPointsCalculator.CalculateRefund(100, 100, 80, 10, 4, 10, 10);
    Assert.Equal(2, result.JustifiedRetainedPoints);
    Assert.Equal(4, result.PointsToReverse);
  }

  [Fact]
  public void Reversal_DoesNotCreateNegativeBalance()
  {
    var result = LoyaltyPointsCalculator.CalculateRefund(100, 100, 100, 10, 0, 3, 10);
    Assert.Equal(3, result.PointsToReverse);
  }
}
