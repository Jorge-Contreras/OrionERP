using OrionERP.Application.Features.Reservaciones.OpenClaw;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.UnitTests.Reservaciones;

public class OpenClawReservationHelpersTests
{
  [Fact]
  public void NormalizeLookupKey_RemovesAccentsAndSymbols()
  {
    var result = OpenClawReservationNaming.NormalizeLookupKey("  Check-in   Anticipadó  ");

    Assert.Equal("CHECK IN ANTICIPADO", result);
  }

  [Fact]
  public void CreateExtra_AggregatesQuantityIntoSingleLine()
  {
    var line = OpenClawReservationLineFactory.CreateExtra("CAMASTRO", 2, 200m, "Llegada de Jorge");

    Assert.Equal("CAMASTRO", line.CatalogName);
    Assert.Equal(2, line.Quantity);
    Assert.Equal(200m, line.UnitPrice);
    Assert.Equal(400m, line.LinePrice);
    Assert.Equal("CAMASTRO x2 - Llegada de Jorge", line.Notes);
  }

  [Fact]
  public void CreateDiscount_ReturnsNegativeLine()
  {
    var line = OpenClawReservationLineFactory.CreateDiscount("DESCUENTO", 6197.90m, 5m);

    Assert.Equal("DESCUENTO", line.CatalogName);
    Assert.Equal(1, line.Quantity);
    Assert.Equal(-309.90m, line.UnitPrice);
    Assert.Equal(-309.90m, line.LinePrice);
    Assert.Equal("DESCUENTO 5%", line.Notes);
  }

  [Fact]
  public void CalculateTotals_ComputesTaxesAndBalance()
  {
    var totals = ReservacionTotalsCalculator.Calculate(
      new DateTime(2026, 3, 18),
      new DateTime(2026, 3, 20),
      true,
      4237.29m,
      290.10m,
      1000m);

    Assert.Equal(4237.29m, totals.TotalSuites);
    Assert.Equal(290.10m, totals.TotalExtras);
    Assert.Equal(4527.39m, totals.SubTotal);
    Assert.Equal(724.38m, totals.Tax);
    Assert.Equal(0m, totals.Ish);
    Assert.Equal(5251.77m, totals.TotalReservacion);
    Assert.Equal(1000m, totals.TotalPagado);
    Assert.Equal(4251.77m, totals.PorPagar);
    Assert.Equal(2, totals.NumNoches);
  }
}
