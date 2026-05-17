namespace OrionERP.Application.Features.ReportesFinancieros.Models;

public sealed class SaludEmpresaReport
{
    public SaludEmpresaReport(
        IReadOnlyList<SaludEmpresaExecutiveIndicatorRow> executiveIndicators,
        IReadOnlyList<SaludEmpresaSuitePerformanceRow> suitePerformance,
        IReadOnlyList<SaludEmpresaFinancialBreakdownRow> financialBreakdown,
        IReadOnlyList<SaludEmpresaCashFlowRow> cashFlow,
        IReadOnlyList<SaludEmpresaDataQualityRow> dataQuality)
    {
        ExecutiveIndicators = executiveIndicators;
        SuitePerformance = suitePerformance;
        FinancialBreakdown = financialBreakdown;
        CashFlow = cashFlow;
        DataQuality = dataQuality;
    }

    public IReadOnlyList<SaludEmpresaExecutiveIndicatorRow> ExecutiveIndicators { get; }
    public IReadOnlyList<SaludEmpresaSuitePerformanceRow> SuitePerformance { get; }
    public IReadOnlyList<SaludEmpresaFinancialBreakdownRow> FinancialBreakdown { get; }
    public IReadOnlyList<SaludEmpresaCashFlowRow> CashFlow { get; }
    public IReadOnlyList<SaludEmpresaDataQualityRow> DataQuality { get; }

    public SaludEmpresaExecutiveIndicatorRow? SelectedPeriod => GetExecutiveIndicator(1);
    public SaludEmpresaExecutiveIndicatorRow? PreviousPeriod => GetExecutiveIndicator(2);
    public SaludEmpresaExecutiveIndicatorRow? SamePeriodPreviousYear => GetExecutiveIndicator(3);
    public SaludEmpresaExecutiveIndicatorRow? CurrentYearToDate => GetExecutiveIndicator(4);
    public SaludEmpresaExecutiveIndicatorRow? PreviousYearToDate => GetExecutiveIndicator(5);
    public SaludEmpresaExecutiveIndicatorRow? SelectedMonth => SelectedPeriod;
    public SaludEmpresaExecutiveIndicatorRow? PreviousMonth => PreviousPeriod;
    public SaludEmpresaExecutiveIndicatorRow? SameMonthPreviousYear => SamePeriodPreviousYear;

    public SaludEmpresaFinancialBreakdownRow? SelectedFinancialBreakdown => GetFinancialBreakdown(1);
    public SaludEmpresaCashFlowRow? SelectedCashFlow => GetCashFlow(1);

    public IReadOnlyList<SaludEmpresaSuitePerformanceRow> SelectedPeriodSuites => SuitePerformance
        .Where(row => row.SortOrder == 1)
        .OrderByDescending(row => row.RoomRevenue)
        .ThenBy(row => row.RoomName)
        .ToList();

    public IReadOnlyList<SaludEmpresaDataQualityRow> SelectedPeriodIssues => DataQuality
        .Where(row => row.SortOrder == 1)
        .OrderBy(row => row.SeverityRank)
        .ThenBy(row => row.CheckType)
        .ThenBy(row => row.Item)
        .ToList();

    public IReadOnlyList<SaludEmpresaSuitePerformanceRow> SelectedMonthSuites => SelectedPeriodSuites;
    public IReadOnlyList<SaludEmpresaDataQualityRow> SelectedMonthIssues => SelectedPeriodIssues;

    public SaludEmpresaExecutiveIndicatorRow? GetExecutiveIndicator(int sortOrder)
        => ExecutiveIndicators.FirstOrDefault(row => row.SortOrder == sortOrder);

    public SaludEmpresaFinancialBreakdownRow? GetFinancialBreakdown(int sortOrder)
        => FinancialBreakdown.FirstOrDefault(row => row.SortOrder == sortOrder);

    public SaludEmpresaCashFlowRow? GetCashFlow(int sortOrder)
        => CashFlow.FirstOrDefault(row => row.SortOrder == sortOrder);
}

public sealed record SaludEmpresaMetricComparison(decimal? Current, decimal? Baseline)
{
    public bool HasComparison => Current.HasValue && Baseline.HasValue;
    public decimal? Delta => HasComparison ? Current!.Value - Baseline!.Value : null;
    public decimal? DeltaPercent => HasComparison && Baseline != 0
        ? Delta!.Value / Math.Abs(Baseline!.Value) * 100m
        : null;

    public int Direction => Delta switch
    {
        > 0 => 1,
        < 0 => -1,
        _ => 0
    };

    public bool IsFavorable(bool lowerIsBetter = false)
        => lowerIsBetter ? Direction < 0 : Direction > 0;
}

public sealed class SaludEmpresaExecutiveIndicatorRow
{
    public string ResultSetName { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string PeriodLabel { get; set; } = string.Empty;
    public string PeriodScope { get; set; } = string.Empty;
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public int RentableSuites { get; set; }
    public int AvailableNights { get; set; }
    public int OccupiedNights { get; set; }
    public decimal? OccupancyPct { get; set; }
    public decimal RoomRevenue { get; set; }
    public decimal? ADR { get; set; }
    public decimal? RevPAR { get; set; }
    public int ReservationCount { get; set; }
    public decimal ReservationTotal { get; set; }
    public decimal PostedCollections { get; set; }
    public decimal? CollectionPct { get; set; }
    public decimal OutstandingCollections { get; set; }
    public decimal NetAccountingIncome { get; set; }
    public decimal CostOfSales { get; set; }
    public decimal OperatingExpenses { get; set; }
    public decimal FinancialExpenses { get; set; }
    public decimal OtherNet { get; set; }
    public decimal Taxes { get; set; }
    public decimal NormalizedOperatingResult { get; set; }
    public decimal NetResult { get; set; }
    public decimal? OperatingMarginPct { get; set; }
    public decimal? NetMarginPct { get; set; }
    public decimal CashIn { get; set; }
    public decimal CashOut { get; set; }
    public decimal NetCashflow { get; set; }
    public decimal EstimatedOwnerShare { get; set; }
    public decimal EstimatedOwnerISR10 { get; set; }
    public decimal EstimatedOwnerFinalPayout { get; set; }
    public decimal PendingBankNetExcluded { get; set; }
}

public sealed class SaludEmpresaSuitePerformanceRow
{
    public string ResultSetName { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string PeriodLabel { get; set; } = string.Empty;
    public string PeriodScope { get; set; } = string.Empty;
    public string RoomName { get; set; } = string.Empty;
    public int? OwnerID { get; set; }
    public decimal? BasePrice { get; set; }
    public int AvailableNights { get; set; }
    public int OccupiedNights { get; set; }
    public decimal? OccupancyPct { get; set; }
    public decimal RoomRevenue { get; set; }
    public decimal? ADR { get; set; }
    public decimal? RevPAR { get; set; }
    public decimal EstimatedOwnerShare { get; set; }
    public decimal EstimatedOwnerISR10 { get; set; }
    public decimal EstimatedOwnerFinalPayout { get; set; }
}

public sealed class SaludEmpresaFinancialBreakdownRow
{
    public string ResultSetName { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string PeriodLabel { get; set; } = string.Empty;
    public string PeriodScope { get; set; } = string.Empty;
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public decimal GrossIncome401403 { get; set; }
    public decimal SalesReturns402 { get; set; }
    public decimal NetAccountingIncome { get; set; }
    public decimal CostOfSales501504 { get; set; }
    public decimal GrossProfit { get; set; }
    public decimal? GrossMarginPct { get; set; }
    public decimal OperatingExpenses602605 { get; set; }
    public decimal FinancialExpenses701 { get; set; }
    public decimal OtherIncome704 { get; set; }
    public decimal OtherExpenses703 { get; set; }
    public decimal OtherNet { get; set; }
    public decimal Taxes611 { get; set; }
    public decimal NormalizedOperatingResult { get; set; }
    public decimal NetResult { get; set; }
    public decimal? OperatingMarginPct { get; set; }
    public decimal? NetMarginPct { get; set; }
    public decimal PendingBankDebeExcluded { get; set; }
    public decimal PendingBankHaberExcluded { get; set; }
    public decimal PendingBankNetExcluded { get; set; }
}

public sealed class SaludEmpresaCashFlowRow
{
    public string ResultSetName { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string PeriodLabel { get; set; } = string.Empty;
    public string PeriodScope { get; set; } = string.Empty;
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public int CashTransactionCount { get; set; }
    public decimal OpeningCashBalance { get; set; }
    public decimal CashIn { get; set; }
    public decimal CashOut { get; set; }
    public decimal NetCashflow { get; set; }
    public decimal ClosingCashBalance { get; set; }
}

public sealed class SaludEmpresaDataQualityRow
{
    public string ResultSetName { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string PeriodLabel { get; set; } = string.Empty;
    public string PeriodScope { get; set; } = string.Empty;
    public string CheckType { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Item { get; set; } = string.Empty;
    public int? ItemCount { get; set; }
    public decimal? MetricAmount { get; set; }
    public decimal? ReferenceAmount { get; set; }
    public decimal? NetEffect { get; set; }
    public string? SampleReference { get; set; }
    public string? Notes { get; set; }

    public int SeverityRank => Severity.Trim().ToUpperInvariant() switch
    {
        "ALTA" => 1,
        "MEDIA" => 2,
        _ => 3
    };
}
