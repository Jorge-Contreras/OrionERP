namespace OrionERP.Application.Features.Restaurante;

public static class LoyaltyPointsCalculator
{
  public static int CalculateEarnedPoints(decimal eligibleMerchandise, decimal pesosPerPoint)
  {
    if (pesosPerPoint <= 0) throw new ArgumentOutOfRangeException(nameof(pesosPerPoint));
    return checked((int)decimal.Floor(Math.Max(0, eligibleMerchandise) / pesosPerPoint));
  }

  public static LoyaltyRefundCalculation CalculateRefund(
    decimal paidOrderTotal,
    decimal originalEligibleMerchandise,
    decimal cumulativeRefundedTotal,
    int originallyEarnedPoints,
    int pointsAlreadyReversed,
    int currentMemberBalance,
    decimal pesosPerPoint)
  {
    if (pesosPerPoint <= 0) throw new ArgumentOutOfRangeException(nameof(pesosPerPoint));
    var retainedRatio = paidOrderTotal <= 0
      ? 0
      : Math.Clamp((paidOrderTotal - Math.Max(0, cumulativeRefundedTotal)) / paidOrderTotal, 0, 1);
    var retainedEligible = decimal.Round(
      Math.Max(0, originalEligibleMerchandise) * retainedRatio,
      2,
      MidpointRounding.AwayFromZero);
    var justifiedPoints = CalculateEarnedPoints(retainedEligible, pesosPerPoint);
    var currentlyRetainedPoints = Math.Max(0, originallyEarnedPoints - Math.Max(0, pointsAlreadyReversed));
    var justifiedReversal = Math.Max(0, currentlyRetainedPoints - justifiedPoints);
    var actualReversal = Math.Min(justifiedReversal, Math.Max(0, currentMemberBalance));
    return new LoyaltyRefundCalculation(retainedEligible, justifiedPoints, actualReversal);
  }

  public static int CalculateCancellationReversal(
    int originallyEarnedPoints,
    int pointsAlreadyReversed,
    int currentMemberBalance)
  {
    var outstandingPoints = Math.Max(0, originallyEarnedPoints - Math.Max(0, pointsAlreadyReversed));
    return Math.Min(outstandingPoints, Math.Max(0, currentMemberBalance));
  }
}

public sealed record LoyaltyRefundCalculation(
  decimal RetainedEligibleMerchandise,
  int JustifiedRetainedPoints,
  int PointsToReverse);
