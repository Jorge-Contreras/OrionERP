using OrionERP.Application.Features.Reservaciones.Cfdi;

namespace OrionERP.UnitTests.Reservaciones;

public class ReservationFacturacionStatusCalculatorTests
{
  [Fact]
  public void Calculate_ReturnsSinFacturar_WhenThereAreNoPayments()
  {
    var result = ReservationFacturacionStatusCalculator.Calculate([]);

    Assert.Equal(ReservationFacturacionStatuses.SinFacturar, result.Status);
    Assert.Equal(0, result.PaymentCount);
    Assert.Equal(0, result.FacturadoPaymentCount);
  }

  [Fact]
  public void Calculate_ReturnsSinFacturar_WhenPaymentsHaveNoCfdiEvidence()
  {
    var result = ReservationFacturacionStatusCalculator.Calculate(
    [
      Payment(101, regularCfdiCount: 0, pago20Count: 0),
      Payment(102, regularCfdiCount: 0, pago20Count: 0)
    ]);

    Assert.Equal(ReservationFacturacionStatuses.SinFacturar, result.Status);
    Assert.Equal(2, result.PaymentCount);
    Assert.Equal(0, result.FacturadoPaymentCount);
  }

  [Fact]
  public void Calculate_ReturnsParcial_WhenOnlySomePaymentsHaveCfdiEvidence()
  {
    var result = ReservationFacturacionStatusCalculator.Calculate(
    [
      Payment(101, regularCfdiCount: 1, pago20Count: 0),
      Payment(102, regularCfdiCount: 0, pago20Count: 0)
    ]);

    Assert.Equal(ReservationFacturacionStatuses.Parcial, result.Status);
    Assert.Equal(2, result.PaymentCount);
    Assert.Equal(1, result.FacturadoPaymentCount);
    Assert.True(result.HasAnyFacturacionEvidence);
  }

  [Fact]
  public void Calculate_ReturnsFacturada_WhenEveryPaymentHasAnyCfdiEvidence()
  {
    var result = ReservationFacturacionStatusCalculator.Calculate(
    [
      Payment(101, regularCfdiCount: 1, pago20Count: 0),
      Payment(102, regularCfdiCount: 0, pago20Count: 1)
    ]);

    Assert.Equal(ReservationFacturacionStatuses.Facturada, result.Status);
    Assert.Equal(2, result.PaymentCount);
    Assert.Equal(2, result.FacturadoPaymentCount);
  }

  [Fact]
  public void Calculate_SumsMixedRegularCfdiAndPago20Evidence()
  {
    var result = ReservationFacturacionStatusCalculator.Calculate(
    [
      Payment(101, regularCfdiCount: 1, pago20Count: 2),
      Payment(102, regularCfdiCount: 0, pago20Count: 1)
    ]);

    Assert.Equal(ReservationFacturacionStatuses.Facturada, result.Status);
    Assert.Equal(1, result.RegularCfdiCount);
    Assert.Equal(3, result.Pago20Count);
  }

  private static ReservationPaymentFacturacionStatusDto Payment(
      int transaccionId,
      int regularCfdiCount,
      int pago20Count)
    => new()
    {
      TransaccionId = transaccionId,
      Monto = 100m,
      RegularCfdiCount = regularCfdiCount,
      Pago20Count = pago20Count
    };
}
