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
