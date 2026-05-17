using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OrionERP.Application.Features.ReportesFinancieros;
using OrionERP.Application.Features.ReportesFinancieros.Models;
using OrionERP.Web.Services;
using OrionERP.Web.State;

namespace OrionERP.Web.Features.ReportesFinancieros.SaludEmpresa;

public partial class SaludEmpresaPage : ComponentBase, IDisposable
{
  private static readonly CultureInfo MexicanCulture = CultureInfo.GetCultureInfo("es-MX");

  [Inject] private IUserRfcState RfcState { get; set; } = default!;
  [Inject] private IReportesFinancierosService ReportesService { get; set; } = default!;
  [Inject] private ISaludEmpresaPdfService PdfService { get; set; } = default!;
  [Inject] private IJSRuntime JS { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;

  protected int StartYear { get; set; } = DateTime.Today.Year;
  protected int StartMonth { get; set; } = DateTime.Today.Month;
  protected int EndYear { get; set; } = DateTime.Today.Year;
  protected int EndMonth { get; set; } = DateTime.Today.Month;
  protected string? CurrentRfc { get; private set; }
  protected SaludEmpresaReport? Report { get; private set; }
  protected bool IsLoading { get; private set; }
  protected bool IsGeneratingPdf { get; private set; }
  protected string? ErrorMessage { get; private set; }

  protected SaludEmpresaExecutiveIndicatorRow? SelectedPeriod => Report?.SelectedPeriod;
  protected SaludEmpresaExecutiveIndicatorRow? PreviousPeriod => Report?.PreviousPeriod;
  protected SaludEmpresaExecutiveIndicatorRow? PreviousYearPeriod => Report?.SamePeriodPreviousYear;
  protected SaludEmpresaFinancialBreakdownRow? SelectedFinancialBreakdown => Report?.SelectedFinancialBreakdown;
  protected SaludEmpresaCashFlowRow? SelectedCashFlow => Report?.SelectedCashFlow;
  protected bool HasReport => SelectedPeriod is not null;

  protected IReadOnlyList<DashboardMetricVm> HeadlineMetrics => BuildHeadlineMetrics();
  protected IReadOnlyList<FinancialBreakdownVm> FinancialBreakdownItems => BuildFinancialBreakdownItems();

  protected IReadOnlyList<SaludEmpresaExecutiveIndicatorRow> PeriodRows => Report?.ExecutiveIndicators
    .OrderBy(row => row.SortOrder)
    .ToList() ?? [];

  protected IReadOnlyList<SaludEmpresaCashFlowRow> CashFlowRows => Report?.CashFlow
    .OrderBy(row => row.SortOrder)
    .ToList() ?? [];

  protected IReadOnlyList<SaludEmpresaSuitePerformanceRow> TopSuites => Report?.SelectedPeriodSuites
    .Take(8)
    .ToList() ?? [];

  protected IReadOnlyList<SaludEmpresaDataQualityRow> PriorityIssues => Report?.SelectedPeriodIssues
    .Take(10)
    .ToList() ?? [];

  protected decimal MaxPeriodRevenue => GetMaxAbs(PeriodRows.Select(row => row.RoomRevenue));
  protected decimal MaxPeriodNetResult => GetMaxAbs(PeriodRows.Select(row => row.NetResult));
  protected decimal MaxCashFlowAmount => GetMaxAbs(CashFlowRows.SelectMany(row => new[] { row.CashIn, row.CashOut, row.NetCashflow }));
  protected decimal MaxSuiteRevenue => GetMaxAbs(TopSuites.Select(row => row.RoomRevenue));
  protected decimal MaxFinancialAmount => GetMaxAbs(FinancialBreakdownItems.Select(row => row.Amount));
  protected string StartMonthValue => $"{StartYear:D4}-{StartMonth:D2}";
  protected string EndMonthValue => $"{EndYear:D4}-{EndMonth:D2}";
  protected DateTime PeriodStart => new(StartYear, StartMonth, 1);
  protected DateTime PeriodEnd => new DateTime(EndYear, EndMonth, 1).AddMonths(1).AddSeconds(-1);
  protected string PeriodRangeDescription => $"Del {PeriodStart:dd/MM/yyyy HH:mm:ss} al {PeriodEnd:dd/MM/yyyy HH:mm:ss}";

  protected override void OnInitialized()
  {
    RfcState.Changed += OnRfcStateChanged;
    CurrentRfc = RfcState.CurrentRfc;
  }

  protected override async Task OnInitializedAsync()
  {
    await LoadDataAsync();
  }

  private async void OnRfcStateChanged()
  {
    CurrentRfc = RfcState.CurrentRfc;
    await LoadDataAsync();
    await InvokeAsync(StateHasChanged);
  }

  protected async Task OnStartMonthChanged(ChangeEventArgs e)
  {
    if (TryParseMonthValue(e.Value?.ToString(), out var year, out var month))
    {
      StartYear = year;
      StartMonth = month;
      await LoadDataAsync();
    }
  }

  protected async Task OnEndMonthChanged(ChangeEventArgs e)
  {
    if (TryParseMonthValue(e.Value?.ToString(), out var year, out var month))
    {
      EndYear = year;
      EndMonth = month;
      await LoadDataAsync();
    }
  }

  protected async Task RefreshAsync()
  {
    await LoadDataAsync();
  }

  protected async Task GeneratePdfAsync()
  {
    if (Report is null || SelectedPeriod is null || IsGeneratingPdf)
    {
      UiMessages.ShowWarning("Genera primero el reporte.");
      return;
    }

    IsGeneratingPdf = true;
    try
    {
      var model = new SaludEmpresaPdfDocumentModel(CurrentRfc ?? string.Empty, PeriodStart, PeriodEnd, DateTime.Now, Report);
      var pdfBytes = PdfService.Generate(model);
      var dataUrl = $"data:application/pdf;base64,{Convert.ToBase64String(pdfBytes)}";
      await JS.InvokeVoidAsync("triggerFileDownload", BuildPdfFileName(), dataUrl);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo generar el PDF. {ex.Message}");
    }
    finally
    {
      IsGeneratingPdf = false;
    }
  }

  private async Task LoadDataAsync()
  {
    if (string.IsNullOrWhiteSpace(CurrentRfc))
    {
      Report = null;
      ErrorMessage = null;
      await InvokeAsync(StateHasChanged);
      return;
    }

    if (new DateTime(EndYear, EndMonth, 1) < PeriodStart)
    {
      Report = null;
      ErrorMessage = "El mes final debe ser mayor o igual al mes inicial.";
      await InvokeAsync(StateHasChanged);
      return;
    }

    IsLoading = true;
    ErrorMessage = null;
    await InvokeAsync(StateHasChanged);

    try
    {
      Report = await ReportesService.GetSaludEmpresaAsync(StartYear, StartMonth, EndYear, EndMonth, CurrentRfc);
    }
    catch (Exception ex)
    {
      ErrorMessage = ex.Message;
      Report = null;
    }
    finally
    {
      IsLoading = false;
      await InvokeAsync(StateHasChanged);
    }
  }

  private IReadOnlyList<DashboardMetricVm> BuildHeadlineMetrics()
  {
    var current = SelectedPeriod;
    var previous = PreviousPeriod;
    var previousYear = PreviousYearPeriod;

    if (current is null)
    {
      return [];
    }

    return
    [
      BuildMetric("Ingresos", "Ventas de hospedaje", current.RoomRevenue, previous?.RoomRevenue, previousYear?.RoomRevenue, SaludEmpresaMetricFormat.Money, "oi-dollar"),
      BuildMetric("Resultado neto", "Utilidad del periodo", current.NetResult, previous?.NetResult, previousYear?.NetResult, SaludEmpresaMetricFormat.Money, "oi-pulse"),
      BuildMetric("Margen neto", "Resultado / ingresos", current.NetMarginPct, previous?.NetMarginPct, previousYear?.NetMarginPct, SaludEmpresaMetricFormat.Percent, "oi-graph"),
      BuildMetric("Flujo neto", "Entradas menos salidas", current.NetCashflow, previous?.NetCashflow, previousYear?.NetCashflow, SaludEmpresaMetricFormat.Money, "oi-transfer"),
      BuildMetric("Ocupacion", "Noches vendidas", current.OccupancyPct, previous?.OccupancyPct, previousYear?.OccupancyPct, SaludEmpresaMetricFormat.Percent, "oi-calendar"),
      BuildMetric("RevPAR", "Ingreso por noche disponible", current.RevPAR, previous?.RevPAR, previousYear?.RevPAR, SaludEmpresaMetricFormat.Money, "oi-bar-chart"),
      BuildMetric("Cobranza", "Cobrado / reservado", current.CollectionPct, previous?.CollectionPct, previousYear?.CollectionPct, SaludEmpresaMetricFormat.Percent, "oi-check"),
      BuildMetric(
        "Pendiente banco",
        "Impacto excluido",
        current.PendingBankNetExcluded,
        previous?.PendingBankNetExcluded,
        previousYear?.PendingBankNetExcluded,
        SaludEmpresaMetricFormat.Money,
        "oi-warning",
        lowerIsBetter: true,
        compareAbsolute: true)
    ];
  }

  private static DashboardMetricVm BuildMetric(
    string label,
    string caption,
    decimal? current,
    decimal? previous,
    decimal? previousYear,
    SaludEmpresaMetricFormat format,
    string icon,
    bool lowerIsBetter = false,
    bool compareAbsolute = false)
  {
    var comparisonCurrent = compareAbsolute ? Abs(current) : current;
    var comparisonPrevious = compareAbsolute ? Abs(previous) : previous;
    var comparisonPreviousYear = compareAbsolute ? Abs(previousYear) : previousYear;

    var monthChange = SaludEmpresaDashboardFormatting.BuildChange(
      comparisonCurrent,
      comparisonPrevious,
      format,
      lowerIsBetter);
    var yearChange = SaludEmpresaDashboardFormatting.BuildChange(
      comparisonCurrent,
      comparisonPreviousYear,
      format,
      lowerIsBetter);

    return new DashboardMetricVm(
      label,
      caption,
      SaludEmpresaDashboardFormatting.FormatValue(current, format),
      icon,
      monthChange,
      yearChange);
  }

  private IReadOnlyList<FinancialBreakdownVm> BuildFinancialBreakdownItems()
  {
    var row = SelectedFinancialBreakdown;
    if (row is null)
    {
      return [];
    }

    return
    [
      new("Ingresos netos", row.NetAccountingIncome, "health-bar--income"),
      new("Costo de ventas", row.CostOfSales501504, "health-bar--cost"),
      new("Gastos operativos", row.OperatingExpenses602605, "health-bar--expense"),
      new("Gastos financieros", row.FinancialExpenses701, "health-bar--expense"),
      new("Otros netos", row.OtherNet, row.OtherNet >= 0 ? "health-bar--income" : "health-bar--expense"),
      new("Impuestos", row.Taxes611, "health-bar--tax"),
      new("Resultado neto", row.NetResult, row.NetResult >= 0 ? "health-bar--income" : "health-bar--loss")
    ];
  }

  protected static string Money(decimal? value)
    => value.HasValue ? value.Value.ToString("C2", MexicanCulture) : "-";

  protected static string MoneyCompact(decimal? value)
    => value.HasValue ? value.Value.ToString("C0", MexicanCulture) : "-";

  protected static string Number(decimal? value)
    => value.HasValue ? value.Value.ToString("N0", MexicanCulture) : "-";

  protected static string Percent(decimal? value)
    => value.HasValue ? $"{value.Value.ToString("N2", MexicanCulture)}%" : "-";

  protected static string Date(DateTime value)
    => value == default ? "-" : value.ToString("dd/MM/yyyy", MexicanCulture);

  protected static string WidthStyle(decimal? value, decimal max)
  {
    var width = max <= 0 ? 0 : Math.Min(100, Math.Abs(value ?? 0) / max * 100);
    return FormattableString.Invariant($"width:{width:0.##}%");
  }

  protected static string SignedClass(decimal value)
    => value >= 0 ? "health-value--positive" : "health-value--negative";

  protected static string DataQualityClass(string severity)
    => severity.Trim().Equals("Alta", StringComparison.OrdinalIgnoreCase)
      ? "health-severity health-severity--high"
      : severity.Trim().Equals("Media", StringComparison.OrdinalIgnoreCase)
        ? "health-severity health-severity--medium"
        : "health-severity health-severity--low";

  private static decimal GetMaxAbs(IEnumerable<decimal?> values)
  {
    var max = values
      .Where(value => value.HasValue)
      .Select(value => Math.Abs(value!.Value))
      .DefaultIfEmpty(0m)
      .Max();

    return max <= 0 ? 1m : max;
  }

  private static decimal GetMaxAbs(IEnumerable<decimal> values)
  {
    var max = values
      .Select(Math.Abs)
      .DefaultIfEmpty(0m)
      .Max();

    return max <= 0 ? 1m : max;
  }

  private static decimal? Abs(decimal? value)
    => value.HasValue ? Math.Abs(value.Value) : null;

  private string BuildPdfFileName()
  {
    var rfc = NormalizeFileNamePart(CurrentRfc ?? "sin-rfc");
    return $"salud-empresa-{rfc}-{StartYear}-{StartMonth:00}-{EndYear}-{EndMonth:00}.pdf";
  }

  private static bool TryParseMonthValue(string? value, out int year, out int month)
  {
    year = 0;
    month = 0;

    if (string.IsNullOrWhiteSpace(value))
    {
      return false;
    }

    var parts = value.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length != 2 ||
        !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out year) ||
        !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out month) ||
        year is < 1900 or > 9999 ||
        month is < 1 or > 12)
    {
      year = 0;
      month = 0;
      return false;
    }

    return true;
  }

  private static string NormalizeFileNamePart(string value)
  {
    var normalized = new string(value
      .Trim()
      .ToLowerInvariant()
      .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
      .ToArray());

    while (normalized.Contains("--", StringComparison.Ordinal))
    {
      normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
    }

    return normalized.Trim('-');
  }

  public void Dispose()
  {
    RfcState.Changed -= OnRfcStateChanged;
  }

  protected sealed record DashboardMetricVm(
    string Label,
    string Caption,
    string Value,
    string Icon,
    SaludEmpresaMetricChange MonthChange,
    SaludEmpresaMetricChange YearChange);
  protected sealed record FinancialBreakdownVm(string Label, decimal Amount, string BarClass);
}
