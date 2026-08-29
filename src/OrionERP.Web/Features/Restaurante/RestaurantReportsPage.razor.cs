using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OrionERP.Application.Features.Restaurante;
using OrionERP.Web.Features.Restaurante.Reports;

namespace OrionERP.Web.Features.Restaurante;

public partial class RestaurantReportsPage
{
  private enum ReportTab
  {
    Resumen,
    Resultados,
    Conciliacion,
    Diagnostico,
    Agrupadores,
    Liquidaciones
  }

  private enum RangePreset
  {
    ThisMonth,
    LastMonth,
    Last90,
    Year
  }

  private static readonly (ReportTab Key, string Label)[] Tabs =
  [
    (ReportTab.Resumen, "Resumen"),
    (ReportTab.Resultados, "Estado de resultados"),
    (ReportTab.Conciliacion, "Conciliación"),
    (ReportTab.Diagnostico, "Diagnóstico"),
    (ReportTab.Agrupadores, "Agrupadores"),
    (ReportTab.Liquidaciones, "Liquidaciones")
  ];

  private List<RestaurantSiteDto> sites = [];
  private RestaurantAccountingReportDto? report;
  private IReadOnlyList<RestaurantRecipeCostDto> recipeCosts = [];
  private IReadOnlyList<RestaurantAgrupadorDto> availableAgrupadores = [];
  private Dictionary<string, string> descriptions = [];
  private RestaurantDiagnosticRunDto? diagnostic;
  private IReadOnlyList<RestaurantDiagnosticRunDto> history = [];
  private List<RestaurantMissingAccountDto> missingAccounts = [];
  private RestaurantPolicyBackfillResultDto? backfill;

  private RestaurantAccountingPreviewDto accountingPreview = new();
  private List<RestaurantSettlementCandidateDto> candidates = [];
  private List<RestaurantProviderSettlementDto> settlements = [];
  private readonly HashSet<Guid> selectedOrders = [];

  private ReportTab activeTab = ReportTab.Resumen;
  private int selectedSiteId;
  private DateTime from = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
  private DateTime to = DateTime.Today;
  private string settlementCode = $"LIQ-{DateTime.Now:yyyyMMdd}";
  private bool isBusy;
  private bool canExecuteActions;
  private string? message;
  private bool isError;

  private string CurrentRfc => RfcState.RequireRfc();

  private decimal IvaContable => report?.Reconciliation.Rows
    .FirstOrDefault(row => row.Concepto == "IVA trasladado cobrado")?.Contabilidad ?? 0m;

  /// <summary>
  /// Un costo de alimentos por encima del 100% de la venta no describe al negocio:
  /// significa que alguna receta tiene el costo mal capturado. Conviene decirlo en
  /// lugar de mostrar un porcentaje absurdo como si fuera un indicador.
  /// </summary>
  private bool FoodCostFueraDeRango => report is not null && report.Summary.FoodCostPorcentaje > 100m;

  private RestaurantAnalyticsQuery Query => new()
  {
    Rfc = CurrentRfc,
    SiteId = selectedSiteId,
    From = from,
    To = to
  };

  protected override async Task OnInitializedAsync()
  {
    var auth = await AuthenticationStateProvider.GetAuthenticationStateAsync();
    canExecuteActions = auth.User.IsInRole("RestauranteAdmin");
    await LoadAsync();
  }

  private void SelectTab(ReportTab tab) => activeTab = tab;

  private async Task LoadAsync()
  {
    isBusy = true;
    try
    {
      sites = (await CatalogService.GetSitesAsync(CurrentRfc)).ToList();
      if (!sites.Any(site => site.Id == selectedSiteId))
      {
        selectedSiteId = sites.FirstOrDefault()?.Id ?? 0;
      }

      await LoadReportAsync();
    }
    catch (Exception ex)
    {
      Show(ex.Message, true);
    }
    finally
    {
      isBusy = false;
    }
  }

  private async Task LoadReportAsync()
  {
    if (selectedSiteId == 0)
    {
      report = null;
      return;
    }

    isBusy = true;
    try
    {
      report = await AnalyticsService.GetAccountingReportAsync(Query);
      recipeCosts = await AnalyticsService.GetRecipeCostsAsync(Query);
      availableAgrupadores = await AnalyticsService.GetAvailableAgrupadoresAsync(CurrentRfc);
      descriptions = availableAgrupadores.ToDictionary(item => item.Nivel1, item => item.Descripcion);

      history = await DiagnosticsService.GetHistoryAsync(CurrentRfc, selectedSiteId);
      diagnostic = history.FirstOrDefault();
      missingAccounts = (await DiagnosticsService.GetMissingAccountsAsync(CurrentRfc)).ToList();

      accountingPreview = await AccountingService.GetDailyPreviewAsync(CurrentRfc, selectedSiteId, to);
      candidates = (await BackofficeService.GetSettlementCandidatesAsync(CurrentRfc, selectedSiteId)).ToList();
      settlements = (await BackofficeService.GetSettlementsAsync(CurrentRfc, selectedSiteId)).ToList();
      selectedOrders.RemoveWhere(id => candidates.All(candidate => candidate.OrderId != id));
    }
    catch (Exception ex)
    {
      Show(ex.Message, true);
    }
    finally
    {
      isBusy = false;
    }
  }

  private async Task ChangeSiteAsync(ChangeEventArgs args)
  {
    if (!int.TryParse(args.Value?.ToString(), out var id)) { return; }
    selectedSiteId = id;
    selectedOrders.Clear();
    backfill = null;
    await LoadReportAsync();
  }

  private async Task SetRangeAsync(RangePreset preset)
  {
    var today = DateTime.Today;
    (from, to) = preset switch
    {
      RangePreset.ThisMonth => (new DateTime(today.Year, today.Month, 1), today),
      RangePreset.LastMonth => (new DateTime(today.Year, today.Month, 1).AddMonths(-1),
                                new DateTime(today.Year, today.Month, 1).AddDays(-1)),
      RangePreset.Last90 => (today.AddDays(-89), today),
      _ => (new DateTime(today.Year, 1, 1), today)
    };

    await LoadReportAsync();
  }

  // ------------------------------------------------------------------
  // Gráficas del resumen
  // ------------------------------------------------------------------

  private IReadOnlyList<ReportBarItem> ResultBars => report is null
    ? []
    : report.Pnl.Rows
      .Where(row => row.Movimientos > 0)
      .OrderByDescending(row => Math.Abs(row.Periodo))
      .Select(row => new ReportBarItem
      {
        Label = row.Etiqueta,
        Sublabel = string.Join(" · ", row.Agrupadores),
        Value = row.Periodo,
        Display = row.Periodo.ToString("C0"),
        Tone = row.Periodo >= 0 ? ReportTone.Good : ReportTone.Neutral,
        Codes = row.Agrupadores
      })
      .ToList();

  private IReadOnlyList<ReportBarItem> AgrupadorBars => report is null
    ? []
    : report.Agrupadores
      .Take(14)
      .Select(agrupador => new ReportBarItem
      {
        Label = $"{agrupador.Nivel1} · {agrupador.Descripcion}",
        Sublabel = $"{agrupador.Movimientos} movimiento(s)",
        Value = agrupador.Saldo,
        Display = agrupador.Saldo.ToString("C0"),
        Tone = agrupador.Incluido ? ReportTone.Neutral : ReportTone.Warning,
        Codes = [agrupador.Nivel1]
      })
      .ToList();

  private IReadOnlyList<ReportBarItem> FoodCostBars => recipeCosts
    .Where(cost => cost.UnidadesVendidas > 0)
    .OrderByDescending(cost => cost.Venta)
    .Take(18)
    .Select(cost => new ReportBarItem
    {
      Label = cost.Producto,
      Sublabel = cost.SinCosto
        ? "sin costo asignado"
        : $"{cost.CostoRecalculado.ToString("C")} de {cost.PrecioLista.ToString("C")} · {cost.CostoOrigen.ToLowerInvariant()}",
      // Un producto con el costo mal capturado puede llegar a miles por ciento y
      // aplastaría al resto de las barras; se recorta la barra y se conserva la cifra real.
      Value = Math.Min(cost.FoodCostPorcentaje, 120m),
      Display = cost.SinCosto ? "sin costo" : $"{cost.FoodCostPorcentaje:0.0}%",
      Tone = cost.SinCosto ? ReportTone.Critical
        : cost.FoodCostPorcentaje > 50 ? ReportTone.Critical
        : cost.FoodCostPorcentaje > 35 ? ReportTone.Warning
        : ReportTone.Good,
      Codes = ["501"]
    })
    .ToList();

  // ------------------------------------------------------------------
  // Desglose del estado de resultados
  // ------------------------------------------------------------------

  private Task<IReadOnlyList<RestaurantLedgerNodeDto>> LoadBreakdownAsync(string nivel1, string? nivel2)
    => AnalyticsService.GetLedgerBreakdownAsync(Query, nivel1, nivel2);

  private Task<IReadOnlyList<RestaurantLedgerEntryDto>> LoadEntriesAsync(string nivel1, string? nivel2, string? nivel3)
    => AnalyticsService.GetLedgerEntriesAsync(Query, nivel1, nivel2, nivel3);

  // ------------------------------------------------------------------
  // Mapeo de agrupadores
  // ------------------------------------------------------------------

  private async Task SaveMapRowAsync(RestaurantAgrupadorMapRowDto row)
  {
    isBusy = true;
    try
    {
      var user = await UserNameAsync();
      var result = await AnalyticsService.SaveAgrupadorMapRowAsync(CurrentRfc, row, user);
      Show(result.Message, !result.Success);
      if (result.Success) { await LoadReportAsync(); }
    }
    catch (Exception ex)
    {
      Show(ex.Message, true);
    }
    finally
    {
      isBusy = false;
    }
  }

  private async Task DeleteMapRowAsync(int id)
  {
    isBusy = true;
    try
    {
      var user = await UserNameAsync();
      var result = await AnalyticsService.DeleteAgrupadorMapRowAsync(CurrentRfc, id, user);
      Show(result.Message, !result.Success);
      if (result.Success) { await LoadReportAsync(); }
    }
    catch (Exception ex)
    {
      Show(ex.Message, true);
    }
    finally
    {
      isBusy = false;
    }
  }

  private async Task ResetMapAsync()
  {
    isBusy = true;
    try
    {
      var user = await UserNameAsync();
      var result = await AnalyticsService.ResetAgrupadorMapAsync(CurrentRfc, user);
      Show(result.Message, !result.Success);
      await LoadReportAsync();
    }
    catch (Exception ex)
    {
      Show(ex.Message, true);
    }
    finally
    {
      isBusy = false;
    }
  }

  // ------------------------------------------------------------------
  // Diagnóstico y acciones guiadas
  // ------------------------------------------------------------------

  private async Task RunDiagnosticAsync()
  {
    isBusy = true;
    backfill = null;
    try
    {
      var user = await UserNameAsync();
      diagnostic = await DiagnosticsService.RunAsync(Query, user);
      history = await DiagnosticsService.GetHistoryAsync(CurrentRfc, selectedSiteId);
      Show($"El diagnóstico encontró {diagnostic.HallazgosTotal} hallazgo(s), {diagnostic.Criticos} crítico(s).", false);
    }
    catch (Exception ex)
    {
      Show(ex.Message, true);
    }
    finally
    {
      isBusy = false;
    }
  }

  private async Task CreateAccountsAsync()
  {
    isBusy = true;
    try
    {
      var user = await UserNameAsync();
      var result = await DiagnosticsService.CreateMissingAccountsAsync(CurrentRfc, missingAccounts, user);
      Show(result.Message, !result.Success);
      if (result.Success)
      {
        missingAccounts = (await DiagnosticsService.GetMissingAccountsAsync(CurrentRfc)).ToList();
      }
    }
    catch (Exception ex)
    {
      Show(ex.Message, true);
    }
    finally
    {
      isBusy = false;
    }
  }

  private async Task BackfillPoliciesAsync()
  {
    isBusy = true;
    try
    {
      var user = await UserNameAsync();
      backfill = await DiagnosticsService.BackfillDailyPoliciesAsync(Query, user);
      Show(backfill.Message, !backfill.Success);
      if (backfill.Generadas > 0) { await LoadReportAsync(); }
    }
    catch (Exception ex)
    {
      Show(ex.Message, true);
    }
    finally
    {
      isBusy = false;
    }
  }

  private async Task AcceptFindingAsync((long Id, string Justificacion) accept)
  {
    isBusy = true;
    try
    {
      var user = await UserNameAsync();
      var result = await DiagnosticsService.AcceptFindingAsync(CurrentRfc, accept.Id, accept.Justificacion, user);
      Show(result.Message, !result.Success);
      if (result.Success)
      {
        history = await DiagnosticsService.GetHistoryAsync(CurrentRfc, selectedSiteId);
        diagnostic = history.FirstOrDefault();
      }
    }
    catch (Exception ex)
    {
      Show(ex.Message, true);
    }
    finally
    {
      isBusy = false;
    }
  }

  // ------------------------------------------------------------------
  // Exportación
  // ------------------------------------------------------------------

  private async Task ExportAsync()
  {
    if (report is null) { return; }
    isBusy = true;
    try
    {
      var siteName = sites.FirstOrDefault(site => site.Id == selectedSiteId)?.Name ?? $"Sede {selectedSiteId}";
      var workbook = WorkbookService.CreateAccountingWorkbook(report, recipeCosts, diagnostic, CurrentRfc, siteName);
      var dataUrl = $"data:{workbook.ContentType};base64,{Convert.ToBase64String(workbook.Content)}";
      await Js.InvokeVoidAsync("triggerFileDownload", workbook.FileName, dataUrl);
    }
    catch (Exception ex)
    {
      Show($"No se pudo generar el archivo: {ex.Message}", true);
    }
    finally
    {
      isBusy = false;
    }
  }

  // ------------------------------------------------------------------
  // Liquidaciones y póliza diaria
  // ------------------------------------------------------------------

  private void ToggleCandidate(RestaurantSettlementCandidateDto candidate)
  {
    if (selectedOrders.Remove(candidate.OrderId)) { return; }
    var provider = candidates.FirstOrDefault(item => selectedOrders.Contains(item.OrderId))?.ExternalProviderId;
    if (provider.HasValue && provider != candidate.ExternalProviderId) { selectedOrders.Clear(); }
    selectedOrders.Add(candidate.OrderId);
  }

  private async Task SettleAsync()
  {
    isBusy = true;
    try
    {
      var user = await UserNameAsync("liquidacion");
      var result = await BackofficeService.CreateSettlementAsync(new()
      {
        Rfc = CurrentRfc,
        SiteId = selectedSiteId,
        SettlementCode = settlementCode,
        OrderIds = selectedOrders.ToList()
      }, user);
      Show(result.Message, !result.Success);
      if (result.Success)
      {
        selectedOrders.Clear();
        settlementCode = $"LIQ-{DateTime.Now:yyyyMMdd-HHmm}";
        await LoadReportAsync();
      }
    }
    catch (Exception ex)
    {
      Show(ex.Message, true);
    }
    finally
    {
      isBusy = false;
    }
  }

  private async Task GeneratePolicyAsync()
  {
    isBusy = true;
    try
    {
      var user = await UserNameAsync("contabilidad");
      var result = await AccountingService.GenerateDailyPolicyAsync(CurrentRfc, selectedSiteId, to, user);
      Show(result.Message, !result.Success);
      await LoadReportAsync();
    }
    catch (Exception ex)
    {
      Show(ex.Message, true);
    }
    finally
    {
      isBusy = false;
    }
  }

  private async Task<string> UserNameAsync(string fallback = "reportes")
  {
    var auth = await AuthenticationStateProvider.GetAuthenticationStateAsync();
    return auth.User.Identity?.Name ?? fallback;
  }

  private void Show(string value, bool error)
  {
    message = value;
    isError = error;
  }
}
