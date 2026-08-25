using OrionERP.Application.Common;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using OrionERP.Application.Features.ReportesFinancieros;
using OrionERP.Application.Features.ReportesFinancieros.Models;
using OrionERP.Web.Services;
using OrionERP.Web.State;

namespace OrionERP.Web.Features.ReportesFinancieros.SaludEmpresa;

public partial class SaludEmpresaPage : ComponentBase
{
  private static readonly CultureInfo EsMx = CultureInfo.GetCultureInfo("es-MX");
  [Inject] private ICurrentCompanyContext RfcState { get; set; } = default!;
  [Inject] private IReportesFinancierosService Reports { get; set; } = default!;
  [Inject] private ISaludEmpresaPdfService Pdf { get; set; } = default!;
  [Inject] private ISaludEmpresaExcelService Excel { get; set; } = default!;
  [Inject] private IJSRuntime JS { get; set; } = default!;
  [Inject] private IUiMessageService Messages { get; set; } = default!;
  [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
  [Inject] private IAuthorizationService Authorization { get; set; } = default!;

  protected int StartYear { get; set; } = DateTime.Today.Year;
  protected int StartMonth { get; set; } = DateTime.Today.Month;
  protected int EndYear { get; set; } = DateTime.Today.Year;
  protected int EndMonth { get; set; } = DateTime.Today.Month;
  protected DateTime CutoffDate { get; set; } = DateTime.Today;
  protected string CurrentRfc => RfcState.RequireRfc();
  protected string ActiveTab { get; set; } = "executive";
  protected bool IsLoading { get; private set; }
  protected bool IsExporting { get; private set; }
  protected bool CanManage { get; private set; }
  protected string? ErrorMessage { get; private set; }
  protected SaludEmpresaReport? Report { get; private set; }
  protected SaludEmpresaReconciliationPage Reconciliation { get; private set; } = new();
  protected List<SaludEmpresaTarget> Targets { get; private set; } = [];
  protected SaludEmpresaConfiguration? Configuration { get; private set; }
  protected List<SaludEmpresaRoomConfiguration> Rooms { get; private set; } = [];
  protected int ReconciliationPage { get; set; } = 1;
  protected string ReconciliationSeverity { get; set; } = string.Empty;
  protected string ReconciliationType { get; set; } = string.Empty;
  protected string ReconciliationSearch { get; set; } = string.Empty;

  protected SaludEmpresaExecutiveIndicatorRow? Current => Report?.SelectedPeriod;
  protected SaludEmpresaFinancialBreakdownRow? Financial => Report?.SelectedFinancialBreakdown;
  protected SaludEmpresaCashFlowRow? Cash => Report?.SelectedCashFlow;
  protected bool HasReport => Current is not null;
  protected DateTime PeriodStart => new(StartYear, StartMonth, 1);
  protected DateTime PeriodEnd => new DateTime(EndYear, EndMonth, 1).AddMonths(1).AddDays(-1);
  protected string StartMonthValue => $"{StartYear:D4}-{StartMonth:D2}";
  protected string EndMonthValue => $"{EndYear:D4}-{EndMonth:D2}";
  protected IReadOnlyList<SaludEmpresaDataQualityRow> CriticalIssues => Report?.SelectedPeriodIssues.Where(row => row.Severity.Equals("Alta", StringComparison.OrdinalIgnoreCase)).Take(5).ToList() ?? [];
  protected IReadOnlyList<SaludEmpresaExpenseRow> TopExpenses => Report?.Expenses.OrderByDescending(row => Math.Abs(row.Amount)).Take(10).ToList() ?? [];
  protected IReadOnlyList<SaludEmpresaSuitePerformanceRow> Suites => Report?.SelectedPeriodSuites ?? [];
  protected decimal MaxExpense => TopExpenses.Select(row => Math.Abs(row.Amount)).DefaultIfEmpty(1).Max();

  protected override async Task OnInitializedAsync()
  {
    var user = (await AuthenticationStateProvider.GetAuthenticationStateAsync()).User;
    CanManage = (await Authorization.AuthorizeAsync(user, "FinanzasManager")).Succeeded;
    await LoadAsync();
  }

  protected async Task SelectTabAsync(string tab)
  {
    ActiveTab = tab;
    if (tab == "reconciliation" && Reconciliation.TotalCount == 0) await LoadReconciliationAsync();
  }

  protected async Task OnStartMonthChanged(ChangeEventArgs args)
  {
    if (TryParseMonth(args.Value?.ToString(), out var year, out var month)) { StartYear = year; StartMonth = month; await LoadAsync(); }
  }

  protected async Task OnEndMonthChanged(ChangeEventArgs args)
  {
    if (TryParseMonth(args.Value?.ToString(), out var year, out var month)) { EndYear = year; EndMonth = month; await LoadAsync(); }
  }

  protected async Task OnCutoffChanged(ChangeEventArgs args)
  {
    if (DateTime.TryParse(args.Value?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)) { CutoffDate = value.Date; await LoadAsync(); }
  }

  protected Task RefreshAsync() => LoadAsync();

  private async Task LoadAsync()
  {
    if (new DateTime(EndYear, EndMonth, 1) < PeriodStart) { ErrorMessage = "El periodo final debe ser mayor o igual al inicial."; return; }
    IsLoading = true; ErrorMessage = null; await InvokeAsync(StateHasChanged);
    try
    {
      Report = await Reports.GetSaludEmpresaAsync(new SaludEmpresaQuery(StartYear, StartMonth, EndYear, EndMonth, CurrentRfc, CutoffDate));
      var targetStart = new DateTime(2026, 1, 1);
      var targetEnd = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(12);
      Targets = (await Reports.GetSaludEmpresaTargetsAsync(CurrentRfc, targetStart, targetEnd)).ToList();
      await LoadReconciliationAsync();
      if (CanManage)
      {
        Configuration = await Reports.GetSaludEmpresaConfigurationAsync(CurrentRfc);
        Rooms = (await Reports.GetSaludEmpresaRoomsAsync()).Where(room => room.RoomType.Equals("SUITE", StringComparison.OrdinalIgnoreCase) || room.IsRentable).ToList();
      }
    }
    catch (Exception ex) { Report = null; ErrorMessage = ex.Message; }
    finally { IsLoading = false; await InvokeAsync(StateHasChanged); }
  }

  protected async Task FilterReconciliationAsync() { ReconciliationPage = 1; await LoadReconciliationAsync(); }
  protected async Task ChangeReconciliationPageAsync(int page) { ReconciliationPage = Math.Clamp(page, 1, Math.Max(1, Reconciliation.TotalPages)); await LoadReconciliationAsync(); }

  private async Task LoadReconciliationAsync()
  {
    Reconciliation = await Reports.GetSaludEmpresaReconciliationAsync(new SaludEmpresaReconciliationQuery(
      CurrentRfc, PeriodStart, PeriodEnd, ReconciliationPage, 25,
      NullIfBlank(ReconciliationSeverity), NullIfBlank(ReconciliationType), NullIfBlank(ReconciliationSearch)));
  }

  protected async Task SaveTargetAsync(SaludEmpresaTarget target)
  {
    if (!CanManage) return;
    try
    {
      var user = (await AuthenticationStateProvider.GetAuthenticationStateAsync()).User.Identity?.Name ?? "Sistema";
      await Reports.SaveSaludEmpresaTargetAsync(target, user);
      Messages.ShowSuccess($"Meta de {target.Month:MMM yyyy} guardada.");
      Targets = (await Reports.GetSaludEmpresaTargetsAsync(CurrentRfc, Targets.Min(x => x.Month), Targets.Max(x => x.Month))).ToList();
      await LoadAsync();
    }
    catch (Exception ex) { Messages.ShowError(ex.Message); }
  }

  protected async Task SaveConfigurationAsync()
  {
    if (!CanManage || Configuration is null) return;
    var user = (await AuthenticationStateProvider.GetAuthenticationStateAsync()).User.Identity?.Name ?? "Sistema";
    await Reports.SaveSaludEmpresaConfigurationAsync(Configuration, user);
    Messages.ShowSuccess("Configuracion financiera guardada.");
    await LoadAsync();
  }

  protected async Task SaveRoomAsync(SaludEmpresaRoomConfiguration room)
  {
    if (!CanManage) return;
    await Reports.SaveSaludEmpresaRoomAsync(room);
    Messages.ShowSuccess($"Configuracion de {room.RoomName} guardada.");
    await LoadAsync();
  }

  protected Task DownloadInternalPdfAsync() => DownloadPdfAsync(investor: false);
  protected Task DownloadInvestorPdfAsync() => DownloadPdfAsync(investor: true);

  private async Task DownloadPdfAsync(bool investor)
  {
    if (Report is null || IsExporting) return;
    IsExporting = true;
    try
    {
      var reconciliation = investor ? null : await GetAllReconciliationAsync();
      var model = new SaludEmpresaPdfDocumentModel(CurrentRfc, PeriodStart, PeriodEnd, DateTime.Now, Report, Targets, reconciliation);
      var bytes = investor ? Pdf.GenerateInvestor(model) : Pdf.Generate(model);
      await DownloadAsync(investor ? "salud-financiera-inversionistas.pdf" : "salud-financiera-interno.pdf", "application/pdf", bytes);
    }
    catch (Exception ex) { Messages.ShowError($"No se pudo generar el PDF: {ex.Message}"); }
    finally { IsExporting = false; }
  }

  protected async Task DownloadExcelAsync()
  {
    if (Report is null || IsExporting) return;
    IsExporting = true;
    try
    {
      var all = await GetAllReconciliationAsync();
      var bytes = Excel.Generate(new SaludEmpresaPdfDocumentModel(CurrentRfc, PeriodStart, PeriodEnd, DateTime.Now, Report, Targets, all));
      await DownloadAsync("salud-financiera-interno.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", bytes);
    }
    catch (Exception ex) { Messages.ShowError($"No se pudo generar Excel: {ex.Message}"); }
    finally { IsExporting = false; }
  }

  private async Task DownloadAsync(string fileName, string mime, byte[] bytes)
    => await JS.InvokeVoidAsync("triggerFileDownload", fileName, $"data:{mime};base64,{Convert.ToBase64String(bytes)}");

  private async Task<IReadOnlyList<SaludEmpresaReconciliationRow>> GetAllReconciliationAsync()
  {
    var all = new List<SaludEmpresaReconciliationRow>();
    var page = 1;
    SaludEmpresaReconciliationPage result;
    do
    {
      result = await Reports.GetSaludEmpresaReconciliationAsync(new SaludEmpresaReconciliationQuery(CurrentRfc, PeriodStart, PeriodEnd, page, 100));
      all.AddRange(result.Items);
      page++;
    } while (page <= result.TotalPages);
    return all;
  }

  protected static string Money(decimal? value) => value?.ToString("C2", EsMx) ?? "No disponible";
  protected static string Money0(decimal? value) => value?.ToString("C0", EsMx) ?? "Sin meta";
  protected static string Percent(decimal? value) => value.HasValue ? $"{value:N1}%" : "No disponible";
  protected static string Variance(SaludEmpresaTargetVarianceRow? row) => row?.TargetValue is null ? "Sin meta" : $"{row.VariancePct:+0.0;-0.0;0.0}% vs meta";
  protected SaludEmpresaTargetVarianceRow? VarianceFor(string key) => Report?.TargetVariances.FirstOrDefault(row => row.MetricKey == key);
  protected static string SeverityClass(string severity) => severity.Equals("Alta", StringComparison.OrdinalIgnoreCase) ? "sf-badge sf-badge--high" : severity.Equals("Media", StringComparison.OrdinalIgnoreCase) ? "sf-badge sf-badge--medium" : "sf-badge sf-badge--low";
  protected static string Width(decimal value, decimal max) => FormattableString.Invariant($"width:{(max <= 0 ? 0 : Math.Min(100, Math.Abs(value) / max * 100)):0.##}%");
  private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
  private static bool TryParseMonth(string? value, out int year, out int month)
  {
    year = month = 0; var parts = value?.Split('-');
    return parts is { Length: 2 } && int.TryParse(parts[0], out year) && int.TryParse(parts[1], out month) && month is >= 1 and <= 12;
  }

}
