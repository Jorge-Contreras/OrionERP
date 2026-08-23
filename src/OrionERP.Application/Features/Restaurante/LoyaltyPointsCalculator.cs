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

  /// <summary>Valor en pesos de una cantidad de puntos, redondeado a dos decimales.</summary>
  public static decimal CalculateRedemptionValue(int points, decimal pointValueMxn)
  {
    if (pointValueMxn <= 0) throw new ArgumentOutOfRangeException(nameof(pointValueMxn));
    return decimal.Round(Math.Max(0, points) * pointValueMxn, 2, MidpointRounding.AwayFromZero);
  }

  /// <summary>
  /// Valida un canje contra el saldo y el mínimo del programa.
  /// Devuelve el motivo de rechazo, o null cuando el canje es válido.
  /// </summary>
  public static string? ValidateRedemption(int points, int currentBalance, int minimumRedeemPoints)
  {
    if (points <= 0)
      return "El canje debe ser de al menos un punto.";
    if (minimumRedeemPoints > 0 && points < minimumRedeemPoints)
      return $"El canje mínimo es de {minimumRedeemPoints} puntos.";
    if (currentBalance < points)
      return $"El saldo disponible es de {Math.Max(0, currentBalance)} puntos.";
    return null;
  }

  /// <summary>
  /// Puntos que caducan en un corte dado, bajo consumo PEPS (primeras entradas, primeras salidas).
  /// Como todo consumo agota siempre los lotes más antiguos, los puntos vivos originados antes
  /// del corte son la diferencia entre lo acreditado antes del corte y todo lo consumido hasta hoy.
  /// El resultado nunca excede el saldo actual ni baja de cero.
  /// </summary>
  public static int CalculateExpiringPoints(
    int pointsCreditedBeforeCutoff,
    int totalPointsConsumed,
    int currentBalance)
  {
    var unconsumedFromOldLots = Math.Max(0, pointsCreditedBeforeCutoff) - Math.Max(0, totalPointsConsumed);
    return Math.Clamp(unconsumedFromOldLots, 0, Math.Max(0, currentBalance));
  }

  /// <summary>Cuántos puntos completos puede canjear el socio ahora mismo.</summary>
  public static int CalculateRedeemablePoints(int currentBalance, int minimumRedeemPoints)
  {
    var balance = Math.Max(0, currentBalance);
    return minimumRedeemPoints > 0 && balance < minimumRedeemPoints ? 0 : balance;
  }
}

public sealed record LoyaltyRefundCalculation(
  decimal RetainedEligibleMerchandise,
  int JustifiedRetainedPoints,
  int PointsToReverse);
