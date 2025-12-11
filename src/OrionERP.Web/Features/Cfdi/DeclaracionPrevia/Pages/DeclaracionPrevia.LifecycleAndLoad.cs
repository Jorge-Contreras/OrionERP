using Dapper;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.SqlClient;
using Microsoft.IdentityModel.Tokens;
using OrionERP.Web.State;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia
  {
    [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;
    [Inject] protected IUserRfcState RfcState { get; set; } = default!;
    private int _placeholderTransaccionId;

    protected override async Task OnInitializedAsync()
    {
      var placeholderSetting = Configuration["SatXml:PlaceholderTransaccionId"];
      _placeholderTransaccionId = int.TryParse(placeholderSetting, out var parsedPlaceholder)
        ? parsedPlaceholder
        : 0;
      connectionString = Configuration.GetConnectionString("OrionDb");
      // Initialize filter defaults:
      disponibleYears = Enumerable.Range(2020, 7).ToList();  // 2020-2026
      disponibleMonths = new List<(int, string)> {
                (1,"ENERO"),(2,"FEBRERO"),(3,"MARZO"),(4,"ABRIL"),(5,"MAYO"),(6,"JUNIO"),
                (7,"JULIO"),(8,"AGOSTO"),(9,"SEPTIEMBRE"),(10,"OCTUBRE"),(11,"NOVIEMBRE"),(12,"DICIEMBRE")
            };
      try
      {
        // For RazonSocial list, query the Emisor table for distinct RFCs:
        var auth = await AuthStateProvider.GetAuthenticationStateAsync();
        var user = auth.User;

        disponiblesRFCs = user.FindAll("rfc")
            .Select(c => c.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r)
            .ToList();
      }
      catch
      {
        disponiblesRFCs = new List<string>(); // if fails, fallback
      }
      if (disponiblesRFCs == null || disponiblesRFCs.Count == 0)
      {
        // If none found, just use a default from config or known value
        disponiblesRFCs = new List<string> { "" };
      }
      selectedRfc = RfcState.CurrentRfc ;
      selectedYear = DateTime.Now.Year;
      selectedMonth = DateTime.Now.Month;
      isAnnual = false;
      // Setup sortable fields:
      emitidasSortableFields = new Dictionary<string, string> {
                {"Fecha", "Fecha"},
                {"Receptor", "RECEPTOR"},
                {"Total", "Total"},
                {"UUID", "FOLIO_FISCAL"}
            };
      recibidasSortableFields = new Dictionary<string, string> {
                {"Fecha", "Fecha"},
                {"Emisor", "EMISOR"},
                {"Total", "Total"},
                {"UUID", "FOLIO_FISCAL"}
            };
      emitidasSortColumn = "Fecha";
      emitidasSortOrder = "ASC";
      recibidasSortColumn = "Fecha";
      recibidasSortOrder = "ASC";
      // Load initial data:
      await LoadAllData();
    }

    private async Task LoadAllData()
    {
      ClearErrorMessage();
      statusMessage = null;

      try
      {
        
        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var common = new
        {
          Year = selectedYear.ToString(),
          Month = isAnnual ? (object)DBNull.Value : selectedMonth.ToString("D2"),
          RFC_Emisor = selectedRfc,
          RFC_Receptor = selectedRfc,
          RFC = selectedRfc,
          Anio = selectedYear,
          Mes = selectedMonth,
          startDate = GetSqlDate(startDate),
          endDate = GetSqlDate(endDate)
        };

        var allCfdiBase = (await conn.QueryAsync<DeclaracionCfdiBase>(
          "EXEC cfdi.Declaracion_CFDI_Base @Year, @Month, @RFC", new
          {
            Year = selectedYear,
            Month = isAnnual ? (object)DBNull.Value : selectedMonth,
            RFC = selectedRfc
          })).AsList();

        var emitidasBase = allCfdiBase
          .Where(x => x.EsEmitida && !IsNomina(x.TipoDeComprobante) && !IsTipoE(x.TipoDeComprobante))
          .ToList();

        var recibidasBase = allCfdiBase
          .Where(x => x.EsRecibida && !IsNomina(x.TipoDeComprobante) && !IsTipoE(x.TipoDeComprobante))
          .ToList();

        var nominaEmitidasBase = allCfdiBase
          .Where(x => x.EsEmitida && IsNomina(x.TipoDeComprobante))
          .ToList();

        var nominaRecibidasBase = allCfdiBase
          .Where(x => x.EsRecibida && IsNomina(x.TipoDeComprobante))
          .ToList();

        var tipoERecibidasBase = allCfdiBase
          .Where(x => x.EsRecibida && IsTipoE(x.TipoDeComprobante))
          .ToList();

        emitidas = emitidasBase.Select(ToDeclaracionEmitida).ToList();
        emitidasTotals = ComputeDeclaracionTotales(emitidasBase);

        emitidasNomina = nominaEmitidasBase.Select(ToDeclaracionEmitida).ToList();
        emitidasNominaTotals = ComputeDeclaracionTotales(nominaEmitidasBase);

        recibidas = recibidasBase.Select(ToDeclaracionRecibida).ToList();
        recibidasTotals = ComputeDeclaracionTotales(recibidasBase);

        recibidasNomina = nominaRecibidasBase.Select(ToDeclaracionRecibida).ToList();
        recibidasNominaTotals = ComputeDeclaracionTotales(nominaRecibidasBase);

        recibidasTipoE = tipoERecibidasBase.Select(ToDeclaracionRecibida).ToList();
        recibidasTipoETotals = ComputeDeclaracionTotales(tipoERecibidasBase);

        desfase = (await conn.QueryAsync<DesfaseItem>(
          "EXEC dbo.Declaracion_Comprobantes_Con_Desfase @RFC, @Anio, @Mes", common)).AsList();

        desfaseTotals = await conn.QueryFirstOrDefaultAsync<DesfaseTotales>(
          "EXEC dbo.Declaracion_Comprobantes_Con_Desfase_Totales @RFC, @Anio, @Mes", common);

        polizasNoConsolidadas = (await conn.QueryAsync<PolizaNoConsolidada>(
          "EXEC dbo.Polizas_No_Consolidadas @RFC, @Anio, @Mes", common)).AsList();

        impuestosSummary = await conn.QueryFirstOrDefaultAsync<string>(
          "EXEC dbo.CALCULATE_TAXES @RFC, @startDate, @endDate", common) ?? "";

        var bancosVal = await conn.QueryFirstOrDefaultAsync<string?>(
          "EXEC dbo.Reporte_Bancos_Caja @Year, @Month, @RFC",
          new { Year = selectedYear, Month = isAnnual ? (object)DBNull.Value : selectedMonth, RFC = selectedRfc });

        bancosCajaSummary = bancosVal;

        ApplySorting();
        selectedEmitida = null; selectedRecibida = null;
        emitidasComplementos = new List<PagoComplementoResumen>();
        recibidasComplementos = new List<PagoComplementoResumen>();
        ResetPagination();
      }
      catch (Exception ex)
      {
        SetErrorMessage("Error loading data: " + ex.Message);
      }

    }


    private DeclaracionTotales ComputeDeclaracionTotales(IEnumerable<DeclaracionCfdiBase> items)
    {
      var list = items?.ToList() ?? new List<DeclaracionCfdiBase>();

      return new DeclaracionTotales
      {
        CountCFDIs = list.Count,
        SumSubTotal = SatRound(list.Sum(x => x.SubTotal)),
        SumDescuento = SatRound(list.Sum(x => x.Descuento)),
        SumSubTotalDesc = SatRound(list.Sum(x => x.SubTotal_Desc)),
        SumActos16 = SatRound(list.Sum(x => x.Actos_16)),
        SumActos0 = SatRound(list.Sum(x => x.Actos_0)),
        SumIVA = SatRound(list.Sum(x => x.IVA)),
        SumIEPS = SatRound(list.Sum(x => x.IEPS)),
        SumIVA_RETENIDO = SatRound(list.Sum(x => x.IVA_RETENIDO)),
        SumISR_RETENIDO = SatRound(list.Sum(x => x.ISR_RETENIDO)),
        SumIEPS_RETENIDO = SatRound(list.Sum(x => x.IEPS_RETENIDO)),
        SumTotal = SatRound(list.Sum(x => x.Total))
      };
    }

    private static DeclaracionEmitida ToDeclaracionEmitida(DeclaracionCfdiBase item) => new DeclaracionEmitida(item);

    private static DeclaracionRecibida ToDeclaracionRecibida(DeclaracionCfdiBase item) => new DeclaracionRecibida(item);

    private static decimal SatRound(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static bool IsNomina(string? tipoDeComprobante) => string.Equals(tipoDeComprobante, "N", StringComparison.OrdinalIgnoreCase);

    private static bool IsTipoE(string? tipoDeComprobante) => string.Equals(tipoDeComprobante, "E", StringComparison.OrdinalIgnoreCase);

    // Utility to format date for SQL as string (if needed; SP might accept date properly too)
    private string GetSqlDate(DateTime dt) => dt.ToString("yyyy-MM-dd HH:mm:ss");

    // Compute StartDate and EndDate based on current filter:
    private DateTime startDate => isAnnual ? new DateTime(selectedYear, 1, 1) : new DateTime(selectedYear, selectedMonth, 1);
    private DateTime endDate => isAnnual
        ? new DateTime(selectedYear, 12, 31)
        : new DateTime(selectedYear, selectedMonth, DateTime.DaysInMonth(selectedYear, selectedMonth));
  }
}
