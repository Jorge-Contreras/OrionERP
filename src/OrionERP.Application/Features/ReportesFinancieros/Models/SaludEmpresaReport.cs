namespace OrionERP.Application.Features.ReportesFinancieros.Models;

public sealed class SaludEmpresaReport
{
    public SaludEmpresaReport(
        IReadOnlyList<SaludEmpresaExecutiveIndicatorRow> executiveIndicators,
        IReadOnlyList<SaludEmpresaSuitePerformanceRow> suitePerformance,
        IReadOnlyList<SaludEmpresaFinancialBreakdownRow> financialBreakdown,
        IReadOnlyList<SaludEmpresaCashFlowRow> cashFlow,
        IReadOnlyList<SaludEmpresaDataQualityRow> dataQuality,
        SaludEmpresaMetadata? metadata = null,
        IReadOnlyList<SaludEmpresaTrendRow>? trends = null,
        IReadOnlyList<SaludEmpresaRevenueMixRow>? revenueMix = null,
        IReadOnlyList<SaludEmpresaExpenseRow>? expenses = null,
        IReadOnlyList<SaludEmpresaLiquidityRow>? liquidity = null,
        IReadOnlyList<SaludEmpresaTargetVarianceRow>? targetVariances = null,
        IReadOnlyList<SaludEmpresaOutlookDailyRow>? dailyOutlook = null,
        IReadOnlyList<SaludEmpresaOutlookMonthlyRow>? monthlyOutlook = null)
    {
        ExecutiveIndicators = executiveIndicators;
        SuitePerformance = suitePerformance;
        FinancialBreakdown = financialBreakdown;
        CashFlow = cashFlow;
        DataQuality = dataQuality;
        Metadata = metadata ?? new SaludEmpresaMetadata();
        Trends = trends ?? [];
        RevenueMix = revenueMix ?? [];
        Expenses = expenses ?? [];
        Liquidity = liquidity ?? [];
        TargetVariances = targetVariances ?? [];
        DailyOutlook = dailyOutlook ?? [];
        MonthlyOutlook = monthlyOutlook ?? [];
    }

    public IReadOnlyList<SaludEmpresaExecutiveIndicatorRow> ExecutiveIndicators { get; }
    public IReadOnlyList<SaludEmpresaSuitePerformanceRow> SuitePerformance { get; }
    public IReadOnlyList<SaludEmpresaFinancialBreakdownRow> FinancialBreakdown { get; }
    public IReadOnlyList<SaludEmpresaCashFlowRow> CashFlow { get; }
    public IReadOnlyList<SaludEmpresaDataQualityRow> DataQuality { get; }
    public SaludEmpresaMetadata Metadata { get; }
    public IReadOnlyList<SaludEmpresaTrendRow> Trends { get; }
    public IReadOnlyList<SaludEmpresaRevenueMixRow> RevenueMix { get; }
    public IReadOnlyList<SaludEmpresaExpenseRow> Expenses { get; }
    public IReadOnlyList<SaludEmpresaLiquidityRow> Liquidity { get; }
    public IReadOnlyList<SaludEmpresaTargetVarianceRow> TargetVariances { get; }
    public IReadOnlyList<SaludEmpresaOutlookDailyRow> DailyOutlook { get; }
    public IReadOnlyList<SaludEmpresaOutlookMonthlyRow> MonthlyOutlook { get; }

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
    public decimal ExtrasRevenue { get; set; }
    public decimal ExperiencesRevenue { get; set; }
    public decimal ComplementaryRevenue => ExtrasRevenue + ExperiencesRevenue;
    public decimal TotalOperatingRevenue { get; set; }
    public decimal? ADR { get; set; }
    public decimal? RevPAR { get; set; }
    public decimal? TRevPAR { get; set; }
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
    public int PipelineReservationCount { get; set; }
    public decimal PipelineReservationTotal { get; set; }
    public DateTime? CutoffDate { get; set; }
    public bool IsProvisional { get; set; }
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
    public decimal GeneralExpenses601 { get; set; }
    public decimal OtherOperatingExpenses606 { get; set; }
    public decimal Depreciation613 { get; set; }
    public decimal Amortization614 { get; set; }
    public decimal EstimatedOperatingEbitda { get; set; }
    public decimal OperatingResult { get; set; }
    public decimal FinancialExpenses701 { get; set; }
    public decimal FinancialIncome702 { get; set; }
    public decimal OtherIncome704 { get; set; }
    public decimal OtherExpenses703 { get; set; }
    public decimal OtherNet { get; set; }
    public decimal Taxes611 { get; set; }
    public decimal ProfitSharing607610 { get; set; }
    public decimal NonDeductible612 { get; set; }
    public decimal NormalizedOperatingResult { get; set; }
    public decimal NetResult { get; set; }
    public decimal? OperatingMarginPct { get; set; }
    public decimal? NetMarginPct { get; set; }
    public decimal PendingBankDebeExcluded { get; set; }
    public decimal PendingBankHaberExcluded { get; set; }
    public decimal PendingBankNetExcluded { get; set; }
}

public sealed record SaludEmpresaQuery(
    int StartYear,
    int StartMonth,
    int EndYear,
    int EndMonth,
    string Rfc,
    DateTime? CutoffDate = null,
    bool IncludeNonRentableRooms = false);

public sealed class SaludEmpresaMetadata
{
    public string Rfc { get; set; } = string.Empty;
    public DateTime CutoffDate { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
    public bool IsProvisional { get; set; }
    public bool LodgingEnabled { get; set; }
    public decimal OwnerWithholdingPct { get; set; }
    public bool RatiosAvailable { get; set; }
    public string RatioAvailabilityNotes { get; set; } = string.Empty;
    public string MethodologyVersion { get; set; } = "Salud Financiera v2";
}

public sealed class SaludEmpresaTrendRow
{
    public DateTime Month { get; set; }
    public string MonthLabel { get; set; } = string.Empty;
    public decimal RoomRevenue { get; set; }
    public decimal ComplementaryRevenue { get; set; }
    public decimal TotalOperatingRevenue { get; set; }
    public decimal NetResult { get; set; }
    public decimal? OperatingMarginPct { get; set; }
    public decimal? OccupancyPct { get; set; }
    public decimal? ADR { get; set; }
    public decimal? RevPAR { get; set; }
    public decimal? RevenueTarget { get; set; }
    public decimal? NetResultTarget { get; set; }
    public decimal? PreviousYearRevenue { get; set; }
}

public sealed class SaludEmpresaRevenueMixRow
{
    public string RevenueType { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal? MixPct { get; set; }
}

public sealed class SaludEmpresaExpenseRow
{
    public string AccountFamily { get; set; } = string.Empty;
    public string AccountCode { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal? MixPct { get; set; }
    public bool IsMapped { get; set; }
}

public sealed class SaludEmpresaLiquidityRow
{
    public string MetricKey { get; set; } = string.Empty;
    public string MetricLabel { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public bool IsAvailable { get; set; }
    public string? Notes { get; set; }
}

public sealed class SaludEmpresaTargetVarianceRow
{
    public DateTime Month { get; set; }
    public string MetricKey { get; set; } = string.Empty;
    public string MetricLabel { get; set; } = string.Empty;
    public decimal ActualValue { get; set; }
    public decimal? TargetValue { get; set; }
    public decimal? VarianceValue { get; set; }
    public decimal? VariancePct { get; set; }
    public bool LowerIsBetter { get; set; }
}

public sealed class SaludEmpresaOutlookDailyRow
{
    public DateTime Date { get; set; }
    public int AvailableNights { get; set; }
    public int OnBooksNights { get; set; }
    public decimal RoomRevenue { get; set; }
    public decimal ComplementaryRevenue { get; set; }
    public decimal? OccupancyPct { get; set; }
}

public sealed class SaludEmpresaOutlookMonthlyRow
{
    public DateTime Month { get; set; }
    public int AvailableNights { get; set; }
    public int OnBooksNights { get; set; }
    public decimal RoomRevenue { get; set; }
    public decimal ComplementaryRevenue { get; set; }
    public decimal? OccupancyPct { get; set; }
}

public sealed class SaludEmpresaConfiguration
{
    public string Rfc { get; set; } = string.Empty;
    public bool LodgingEnabled { get; set; }
    public decimal OwnerWithholdingPct { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
}

public sealed class SaludEmpresaRoomConfiguration
{
    public int RoomId { get; set; }
    public string RoomName { get; set; } = string.Empty;
    public string RoomType { get; set; } = string.Empty;
    public decimal BasePrice { get; set; }
    public bool IsActive { get; set; }
    public bool IsRentable { get; set; }
}

public sealed class SaludEmpresaTarget
{
    public long TargetId { get; set; }
    public string Rfc { get; set; } = string.Empty;
    public DateTime Month { get; set; }
    public decimal? RoomRevenueTarget { get; set; }
    public decimal? ComplementaryRevenueTarget { get; set; }
    public decimal? OccupancyPctTarget { get; set; }
    public decimal? AdrTarget { get; set; }
    public decimal? OperatingExpensesTarget { get; set; }
    public decimal? NetResultTarget { get; set; }
    public decimal? NetCashFlowTarget { get; set; }
    public decimal? ClosingCashTarget { get; set; }
    public string? Notes { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed record SaludEmpresaReconciliationQuery(
    string Rfc,
    DateTime StartDate,
    DateTime EndDate,
    int Page = 1,
    int PageSize = 25,
    string? Severity = null,
    string? Type = null,
    string? Search = null);

public sealed class SaludEmpresaReconciliationRow
{
    public long ReconciliationId { get; set; }
    public string Severity { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Item { get; set; } = string.Empty;
    public DateTime? EventDate { get; set; }
    public decimal? Amount { get; set; }
    public decimal? ReferenceAmount { get; set; }
    public decimal? NetEffect { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public int? ReservationId { get; set; }
    public int? TransactionId { get; set; }
}

public sealed class SaludEmpresaReconciliationPage
{
    public IReadOnlyList<SaludEmpresaReconciliationRow> Items { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int HighCount { get; init; }
    public int MediumCount { get; init; }
    public int LowCount { get; init; }
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
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
