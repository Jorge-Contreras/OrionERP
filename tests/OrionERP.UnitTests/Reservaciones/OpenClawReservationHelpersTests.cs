using OrionERP.Application.Features.Reservaciones.OpenClaw;
using OrionERP.Application.Features.Reservaciones.Cfdi;
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

  [Fact]
  public void CalculateTotals_UsesPerLineTaxesToAvoidAggregateExtraCents()
  {
    var totals = ReservacionTotalsCalculator.Calculate(
      new DateTime(2026, 3, 18),
      new DateTime(2026, 3, 20),
      new[] { 1077.59m, 1077.59m, 1077.59m, 1077.59m },
      Array.Empty<decimal>(),
      0m);

    Assert.Equal(4310.36m, totals.TotalSuites);
    Assert.Equal(0m, totals.TotalExtras);
    Assert.Equal(4310.36m, totals.SubTotal);
    Assert.Equal(689.64m, totals.Tax);
    Assert.Equal(0m, totals.Ish);
    Assert.Equal(5000.00m, totals.TotalReservacion);
    Assert.Equal(0m, totals.TotalPagado);
    Assert.Equal(5000.00m, totals.PorPagar);
    Assert.Equal(2, totals.NumNoches);
  }

  [Fact]
  public void CalculateTotals_AppliesSuiteDiscountWithoutDiscountingExtras()
  {
    var totals = ReservacionTotalsCalculator.Calculate(
      new DateTime(2026, 3, 18),
      new DateTime(2026, 3, 20),
      1000m,
      200m,
      0m,
      10m);

    Assert.Equal(1000m, totals.TotalSuites);
    Assert.Equal(10m, totals.SuiteDiscountPercent);
    Assert.Equal(100m, totals.SuiteDiscountAmount);
    Assert.Equal(200m, totals.TotalExtras);
    Assert.Equal(1100m, totals.SubTotal);
    Assert.Equal(176m, totals.Tax);
    Assert.Equal(1276m, totals.TotalReservacion);
  }

  [Fact]
  public void CalculateTotals_DiscountsSuiteLinesBeforePerLineTaxes()
  {
    var totals = ReservacionTotalsCalculator.Calculate(
      new DateTime(2026, 3, 18),
      new DateTime(2026, 3, 20),
      new[] { 1077.59m, 1077.59m, 1077.59m, 1077.59m },
      new[] { 100m },
      0m,
      10m);

    Assert.Equal(4310.36m, totals.TotalSuites);
    Assert.Equal(431.04m, totals.SuiteDiscountAmount);
    Assert.Equal(100m, totals.TotalExtras);
    Assert.Equal(3979.32m, totals.SubTotal);
    Assert.Equal(636.68m, totals.Tax);
    Assert.Equal(4616.00m, totals.TotalReservacion);
  }

  [Fact]
  public void CalculateTotals_TreatsOnePercentOrLessAsNoSuiteDiscount()
  {
    var zero = ReservacionTotalsCalculator.Calculate(
      null,
      null,
      100m,
      0m,
      0m,
      0m);

    var one = ReservacionTotalsCalculator.Calculate(
      null,
      null,
      100m,
      0m,
      0m,
      1m);

    Assert.Equal(0m, zero.SuiteDiscountPercent);
    Assert.Equal(0m, zero.SuiteDiscountAmount);
    Assert.Equal(0m, one.SuiteDiscountPercent);
    Assert.Equal(0m, one.SuiteDiscountAmount);
    Assert.Equal(100m, one.SubTotal);
  }

  [Fact]
  public void CalculateTotals_RejectsSuiteDiscountGreaterThanOneHundred()
  {
    Assert.Throws<ArgumentOutOfRangeException>(() => ReservacionTotalsCalculator.Calculate(
      null,
      null,
      100m,
      0m,
      0m,
      100.01m));
  }

  [Fact]
  public void CreateItems_BuildsReservationCfdiLinesAndDistributesDiscounts()
  {
    var items = ReservationCfdiLineFactory.CreateItems(
      new[]
      {
        new ReservationCfdiSuiteSource
        {
          Id = 1,
          Fecha = new DateTime(2026, 4, 10),
          RoomName = "SUITE 1",
          RoomDescription = "SUITE MASTER",
          Price = 100m
        },
        new ReservationCfdiSuiteSource
        {
          Id = 2,
          Fecha = new DateTime(2026, 4, 11),
          RoomName = "SUITE 2",
          RoomDescription = "SUITE MASTER",
          Price = 100m
        }
      },
      new[]
      {
        new ReservationCfdiExtraSource
        {
          Id = 3,
          CatalogName = "CAMASTRO",
          Description = "Camastro Extra Para Suite",
          Amount = 50m
        },
        new ReservationCfdiExtraSource
        {
          Id = 4,
          CatalogName = "DESCUENTO",
          Description = "DESCUENTO GENERAL",
          Amount = -25m
        }
      });

    Assert.Equal(3, items.Count);
    Assert.Equal(25m, items.Sum(item => item.Discount));
    Assert.Equal(261.00m, items.Sum(item => item.Total));
    Assert.All(items, item => Assert.True(item.Total > 0m));
    Assert.All(items, item => Assert.Equal("02", item.TaxObject));
    Assert.Contains(items, item => item.SourceType == "Extra" && item.ProductCode == "56101515");
  }

  [Fact]
  public void CreateItems_AppliesSuiteDiscountOnlyToSuiteConcepts()
  {
    var items = ReservationCfdiLineFactory.CreateItems(
      new[]
      {
        new ReservationCfdiSuiteSource
        {
          Id = 1,
          Fecha = new DateTime(2026, 4, 10),
          RoomName = "SUITE 1",
          RoomDescription = "SUITE MASTER",
          Price = 100m
        },
        new ReservationCfdiSuiteSource
        {
          Id = 2,
          Fecha = new DateTime(2026, 4, 11),
          RoomName = "SUITE 2",
          RoomDescription = "SUITE MASTER",
          Price = 100m
        }
      },
      new[]
      {
        new ReservationCfdiExtraSource
        {
          Id = 3,
          CatalogName = "CAMASTRO",
          Description = "Camastro Extra Para Suite",
          Amount = 50m
        }
      },
      suiteDiscountPercent: 10m);

    Assert.Equal(20m, items.Where(item => item.SourceType == "Suite").Sum(item => item.Discount));
    Assert.Equal(0m, items.Single(item => item.SourceType == "Extra").Discount);
    Assert.Equal(266.80m, items.Sum(item => item.Total));
  }

  [Fact]
  public void CreateItems_ThrowsWhenDiscountExceedsSubtotal()
  {
    Assert.Throws<InvalidOperationException>(() => ReservationCfdiLineFactory.CreateItems(
      new[]
      {
        new ReservationCfdiSuiteSource
        {
          Id = 1,
          Fecha = new DateTime(2026, 4, 10),
          RoomName = "SUITE 1",
          Price = 100m
        }
      },
      new[]
      {
        new ReservationCfdiExtraSource
        {
          Id = 2,
          CatalogName = "DESCUENTO",
          Description = "DESCUENTO GENERAL",
          Amount = -150m
        }
      }));
  }
}
