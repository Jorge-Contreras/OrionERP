using OrionERP.Application.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class LoyaltyRedemptionTests
{
  private const decimal PointValue = 1m;
  private const int Minimum = 100;

  [Theory]
  [InlineData(100, 1, 100)]
  [InlineData(250, 1, 250)]
  [InlineData(100, 0.5, 50)]
  [InlineData(0, 1, 0)]
  [InlineData(-50, 1, 0)]
  public void RedemptionValue_ConvertsPointsToPesos(int points, decimal pointValue, decimal expected)
  {
    Assert.Equal(expected, LoyaltyPointsCalculator.CalculateRedemptionValue(points, pointValue));
  }

  [Fact]
  public void RedemptionValue_RoundsToTwoDecimalsAwayFromZero()
  {
    Assert.Equal(33.34m, LoyaltyPointsCalculator.CalculateRedemptionValue(100, 0.33335m));
  }

  [Fact]
  public void RedemptionValue_RejectsNonPositivePointValue()
  {
    Assert.Throws<ArgumentOutOfRangeException>(
      () => LoyaltyPointsCalculator.CalculateRedemptionValue(100, 0));
  }

  [Fact]
  public void Redemption_IsAcceptedAtTheMinimumExactly()
  {
    Assert.Null(LoyaltyPointsCalculator.ValidateRedemption(100, 100, Minimum));
  }

  [Fact]
  public void Redemption_IsRejectedBelowTheMinimum()
  {
    var reason = LoyaltyPointsCalculator.ValidateRedemption(99, 500, Minimum);
    Assert.NotNull(reason);
    Assert.Contains("100", reason);
  }

  [Fact]
  public void Redemption_IsRejectedWhenBalanceIsInsufficient()
  {
    var reason = LoyaltyPointsCalculator.ValidateRedemption(200, 150, Minimum);
    Assert.NotNull(reason);
    Assert.Contains("150", reason);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-10)]
  public void Redemption_IsRejectedForNonPositivePoints(int points)
  {
    Assert.NotNull(LoyaltyPointsCalculator.ValidateRedemption(points, 500, Minimum));
  }

  [Theory]
  [InlineData(150, 100, 150)]
  [InlineData(99, 100, 0)]
  [InlineData(0, 100, 0)]
  [InlineData(-5, 100, 0)]
  public void RedeemablePoints_RespectTheMinimum(int balance, int minimum, int expected)
  {
    Assert.Equal(expected, LoyaltyPointsCalculator.CalculateRedeemablePoints(balance, minimum));
  }

  [Theory]
  [InlineData(500, 100, 1, 250, 250)]
  [InlineData(80, 100, 1, 500, 0)]
  [InlineData(500, 100, 1, 99, 0)]
  [InlineData(150, 100, 1, 500, 150)]
  [InlineData(500, 100, 0.5, 75, 150)]
  public void OrderRedemption_CapsPointsByBalanceMinimumAndRemainingMerchandise(
    int balance,
    int minimum,
    decimal pointValue,
    decimal merchandise,
    int expected)
  {
    Assert.Equal(
      expected,
      LoyaltyPointsCalculator.CalculateMaximumOrderRedemptionPoints(
        balance,
        minimum,
        pointValue,
        merchandise));
  }

  [Fact]
  public void OrderRedemption_DoesNotExceedMerchandiseAfterCurrencyRounding()
  {
    var points = LoyaltyPointsCalculator.CalculateMaximumOrderRedemptionPoints(
      500,
      1,
      0.33335m,
      33.33m);

    Assert.Equal(99, points);
    Assert.True(LoyaltyPointsCalculator.CalculateRedemptionValue(points, 0.33335m) <= 33.33m);
  }

  [Theory]
  [InlineData(200, 300, 150, 100)]
  [InlineData(200, 300, 299.99, 200)]
  [InlineData(200, 300, 300, 200)]
  [InlineData(200, 300, 0, 0)]
  [InlineData(200, 0, 100, 0)]
  public void RefundRestoration_IsProportionalAndCompletesAtFullRefund(
    int redeemed,
    decimal total,
    decimal refunded,
    int expected)
  {
    Assert.Equal(
      expected,
      LoyaltyPointsCalculator.CalculateTargetRestoredPoints(redeemed, total, refunded));
  }

  /* ---------- Caducidad PEPS ---------- */

  [Fact]
  public void Expiry_TakesOnlyTheUnconsumedRemainderOfOldLots()
  {
    // Acreditados 100 antes del corte, consumidos 30 en total, saldo 120.
    // El lote viejo conserva 70 vivos: esos caducan.
    Assert.Equal(70, LoyaltyPointsCalculator.CalculateExpiringPoints(100, 30, 120));
  }

  [Fact]
  public void Expiry_IsZeroWhenOldLotsWereAlreadyFullyConsumed()
  {
    // Acreditados 100 antes del corte pero se consumieron 130: el lote viejo ya se agotó.
    Assert.Equal(0, LoyaltyPointsCalculator.CalculateExpiringPoints(100, 130, 20));
  }

  [Fact]
  public void Expiry_NeverExceedsTheCurrentBalance()
  {
    Assert.Equal(40, LoyaltyPointsCalculator.CalculateExpiringPoints(500, 0, 40));
  }

  [Fact]
  public void Expiry_IsZeroWhenNothingWasCreditedBeforeTheCutoff()
  {
    Assert.Equal(0, LoyaltyPointsCalculator.CalculateExpiringPoints(0, 0, 250));
  }

  [Fact]
  public void Expiry_NeverReturnsNegativePoints()
  {
    Assert.Equal(0, LoyaltyPointsCalculator.CalculateExpiringPoints(-10, -10, -10));
  }

  [Fact]
  public void Expiry_ConsumesOldestLotsFirstAcrossSuccessiveRuns()
  {
    // Enero: +100. Junio: +50. Canje de 30. Saldo 120.
    // Primer corte (vence enero): caducan 70, saldo 50.
    var first = LoyaltyPointsCalculator.CalculateExpiringPoints(100, 30, 120);
    Assert.Equal(70, first);

    var balanceAfterFirst = 120 - first;
    Assert.Equal(50, balanceAfterFirst);

    // Segundo corte (vence junio): acreditados antes del corte 150,
    // consumidos 30 del canje + 70 de la caducidad anterior = 100.
    var second = LoyaltyPointsCalculator.CalculateExpiringPoints(150, 100, balanceAfterFirst);
    Assert.Equal(50, second);
    Assert.Equal(0, balanceAfterFirst - second);
  }

  [Fact]
  public void Expiry_LeavesRecentLotsUntouched()
  {
    // Solo 40 se acreditaron antes del corte; el resto del saldo es reciente.
    Assert.Equal(40, LoyaltyPointsCalculator.CalculateExpiringPoints(40, 0, 300));
  }
}
