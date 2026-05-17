using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using OrionERP.Application.Features.ReportesFinancieros.Models;
using OrionERP.Web.Features.ReportesFinancieros.SaludEmpresa;
using System.Text;

namespace OrionERP.UnitTests.ReportesFinancieros;

public class SaludEmpresaDashboardTests
{
  [Fact]
  public void MetricComparison_CalculatesDirectionAndPercentDelta()
  {
    var comparison = new SaludEmpresaMetricComparison(125m, 100m);

    Assert.True(comparison.HasComparison);
    Assert.Equal(25m, comparison.Delta);
    Assert.Equal(25m, comparison.DeltaPercent);
    Assert.Equal(1, comparison.Direction);
    Assert.True(comparison.IsFavorable());
    Assert.False(comparison.IsFavorable(lowerIsBetter: true));
  }

  [Fact]
  public void DashboardFormatting_UsesPointsForPercentMetrics()
  {
    var change = SaludEmpresaDashboardFormatting.BuildChange(
      18.5m,
      15m,
      SaludEmpresaMetricFormat.Percent);

    Assert.Equal("+3.5 pp", change.Text);
    Assert.Equal("health-change--good", change.CssClass);
    Assert.True(change.HasValue);
  }

  [Fact]
  public void DashboardFormatting_HonorsLowerIsBetter()
  {
    var change = SaludEmpresaDashboardFormatting.BuildChange(
      80m,
      100m,
      SaludEmpresaMetricFormat.Money,
      lowerIsBetter: true);

    Assert.Equal("-20.0%", change.Text);
    Assert.Equal("health-change--good", change.CssClass);
    Assert.True(change.IsFavorable);
  }

  [Fact]
  public void DashboardFormatting_UsesAbsoluteDeltaWhenBaselineIsZero()
  {
    var change = SaludEmpresaDashboardFormatting.BuildChange(
      250m,
      0m,
      SaludEmpresaMetricFormat.Money);

    Assert.StartsWith("+", change.Text, StringComparison.Ordinal);
    Assert.Contains("$", change.Text, StringComparison.Ordinal);
    Assert.DoesNotContain("%", change.Text, StringComparison.Ordinal);
  }

  [Fact]
  public void SaludEmpresaPdfService_GeneratesPdf()
  {
    var report = BuildSampleReport();
    var service = new SaludEmpresaPdfService(new FakeWebHostEnvironment());

    var bytes = service.Generate(new SaludEmpresaPdfDocumentModel(
      "OHM191112Q26",
      new DateTime(2026, 2, 1, 0, 0, 0),
      new DateTime(2026, 9, 30, 23, 59, 59),
      new DateTime(2026, 5, 17, 10, 30, 0),
      report));

    Assert.NotEmpty(bytes);
    Assert.StartsWith("%PDF", Encoding.ASCII.GetString(bytes, 0, 4), StringComparison.Ordinal);
  }

  private static SaludEmpresaReport BuildSampleReport()
  {
    var indicators = new List<SaludEmpresaExecutiveIndicatorRow>
    {
      Indicator(1, "2026-05", "Mes seleccionado", 120000m, 24000m, 20m),
      Indicator(2, "2026-04", "Mes anterior", 100000m, 15000m, 15m),
      Indicator(3, "2025-05", "Mismo mes año anterior", 90000m, 12000m, 13.33m),
      Indicator(4, "2026 acumulado", "Acumulado del año", 500000m, 90000m, 18m),
      Indicator(5, "2025 acumulado", "Acumulado año anterior", 420000m, 60000m, 14.29m)
    };

    return new SaludEmpresaReport(
      indicators,
      [
        new SaludEmpresaSuitePerformanceRow
        {
          SortOrder = 1,
          PeriodLabel = "2026-05",
          PeriodScope = "Mes seleccionado",
          RoomName = "MOSCU",
          AvailableNights = 31,
          OccupiedNights = 16,
          OccupancyPct = 51.61m,
          RoomRevenue = 45000m,
          ADR = 2812.5m,
          RevPAR = 1451.61m,
          EstimatedOwnerShare = 13500m,
          EstimatedOwnerISR10 = 1350m,
          EstimatedOwnerFinalPayout = 12150m
        }
      ],
      indicators.Select(row => new SaludEmpresaFinancialBreakdownRow
      {
        SortOrder = row.SortOrder,
        PeriodLabel = row.PeriodLabel,
        PeriodScope = row.PeriodScope,
        PeriodStart = row.PeriodStart,
        PeriodEnd = row.PeriodEnd,
        GrossIncome401403 = row.RoomRevenue,
        NetAccountingIncome = row.RoomRevenue,
        CostOfSales501504 = 10000m,
        GrossProfit = row.RoomRevenue - 10000m,
        GrossMarginPct = 70m,
        OperatingExpenses602605 = 60000m,
        FinancialExpenses701 = 2000m,
        OtherIncome704 = 1000m,
        OtherExpenses703 = 500m,
        OtherNet = 500m,
        Taxes611 = 3500m,
        NormalizedOperatingResult = row.NormalizedOperatingResult,
        NetResult = row.NetResult,
        OperatingMarginPct = row.OperatingMarginPct,
        NetMarginPct = row.NetMarginPct
      }).ToList(),
      indicators.Select(row => new SaludEmpresaCashFlowRow
      {
        SortOrder = row.SortOrder,
        PeriodLabel = row.PeriodLabel,
        PeriodScope = row.PeriodScope,
        PeriodStart = row.PeriodStart,
        PeriodEnd = row.PeriodEnd,
        CashTransactionCount = 22,
        OpeningCashBalance = 10000m,
        CashIn = row.CashIn,
        CashOut = row.CashOut,
        NetCashflow = row.NetCashflow,
        ClosingCashBalance = 20000m
      }).ToList(),
      [
        new SaludEmpresaDataQualityRow
        {
          SortOrder = 1,
          PeriodLabel = "2026-05",
          PeriodScope = "Mes seleccionado",
          CheckType = "Estado de cobranza",
          Severity = "Media",
          Item = "Cobranza parcial contabilizada",
          ItemCount = 2,
          MetricAmount = 8000m,
          ReferenceAmount = 6500m,
          NetEffect = 1500m,
          SampleReference = "reservacion_id=42",
          Notes = "Muestra de prueba."
        }
      ]);
  }

  private static SaludEmpresaExecutiveIndicatorRow Indicator(
    int sortOrder,
    string label,
    string scope,
    decimal revenue,
    decimal netResult,
    decimal netMargin)
  {
    return new SaludEmpresaExecutiveIndicatorRow
    {
      SortOrder = sortOrder,
      PeriodLabel = label,
      PeriodScope = scope,
      PeriodStart = new DateTime(2026, 5, 1),
      PeriodEnd = new DateTime(2026, 5, 31),
      RentableSuites = 8,
      AvailableNights = 248,
      OccupiedNights = 62,
      OccupancyPct = 25m,
      RoomRevenue = revenue,
      ADR = 1935.48m,
      RevPAR = 483.87m,
      ReservationCount = 18,
      ReservationTotal = revenue,
      PostedCollections = revenue * .82m,
      CollectionPct = 82m,
      OutstandingCollections = revenue * .18m,
      NetAccountingIncome = revenue,
      CostOfSales = 10000m,
      OperatingExpenses = 60000m,
      FinancialExpenses = 2000m,
      OtherNet = 500m,
      Taxes = 3500m,
      NormalizedOperatingResult = netResult + 3500m,
      NetResult = netResult,
      OperatingMarginPct = netMargin + 2m,
      NetMarginPct = netMargin,
      CashIn = revenue * .75m,
      CashOut = revenue * .52m,
      NetCashflow = revenue * .23m,
      EstimatedOwnerShare = revenue * .3m,
      EstimatedOwnerISR10 = revenue * .03m,
      EstimatedOwnerFinalPayout = revenue * .27m,
      PendingBankNetExcluded = 1000m
    };
  }

  private sealed class FakeWebHostEnvironment : IWebHostEnvironment
  {
    public string ApplicationName { get; set; } = "OrionERP";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = Path.GetTempPath();
    public string EnvironmentName { get; set; } = "UnitTest";
    public string ContentRootPath { get; set; } = Path.GetTempPath();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
  }
}
