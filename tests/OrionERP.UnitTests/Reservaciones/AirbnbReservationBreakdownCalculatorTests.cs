using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.UnitTests.Reservaciones;

public class AirbnbReservationBreakdownCalculatorTests
{
  [Fact]
  public void Calculate_MatchesReferenceAirbnbAccountingBreakdown()
  {
    var breakdown = AirbnbReservationBreakdownCalculator.Calculate(new AirbnbReservationBreakdownInput
    {
      PayoutAmount = 3193.23m,
      CleaningFee = 200m
    });

    Assert.Equal(3193.23m, breakdown.PayoutAmount);
    Assert.Equal(3130.00m, breakdown.TaxableBase);
    Assert.Equal(2930.00m, breakdown.RoomRateAmount);
    Assert.Equal(200.00m, breakdown.CleaningFee);
    Assert.Equal(500.80m, breakdown.IvaTransferredAmount);
    Assert.Equal(250.40m, breakdown.IvaRetainedAmount);
    Assert.Equal(78.25m, breakdown.IsrRetainedAmount);
    Assert.Equal(93.90m, breakdown.HostServiceFeeBaseAmount);
    Assert.Equal(15.02m, breakdown.HostServiceFeeIvaAmount);
    Assert.Equal(108.92m, breakdown.HostServiceFeeTotalAmount);
    Assert.Equal(3630.80m, breakdown.GrossCfdiTotal);
    Assert.Equal(3630.80m, breakdown.DebitTotal);
    Assert.Equal(3630.80m, breakdown.CreditTotal);
    Assert.True(breakdown.IsBalanced);
  }

  [Fact]
  public void SplitCurrency_DistributesTaxableBaseAcrossSuites()
  {
    var amounts = AirbnbReservationBreakdownCalculator.SplitCurrency(3130.00m, 2);

    Assert.Equal(new[] { 1565.00m, 1565.00m }, amounts);
  }

  [Fact]
  public void SplitCurrency_DistributesRemainderByCent()
  {
    var amounts = AirbnbReservationBreakdownCalculator.SplitCurrency(100m, 3);

    Assert.Equal(new[] { 33.34m, 33.33m, 33.33m }, amounts);
    Assert.Equal(100m, amounts.Sum());
  }

  [Fact]
  public void Calculate_RejectsCleaningFeeAboveTaxableBase()
  {
    var ex = Assert.Throws<InvalidOperationException>(() => AirbnbReservationBreakdownCalculator.Calculate(
      new AirbnbReservationBreakdownInput
      {
        PayoutAmount = 100m,
        CleaningFee = 500m
      }));

    Assert.Contains("Cleaning fee", ex.Message);
  }
}
