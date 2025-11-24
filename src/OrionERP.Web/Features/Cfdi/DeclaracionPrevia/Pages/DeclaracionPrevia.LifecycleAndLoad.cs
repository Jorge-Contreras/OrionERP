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

    protected override async Task OnInitializedAsync()
    {
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

        // One after another—no parallel readers:
        emitidas = (await conn.QueryAsync<DeclaracionEmitida>(
          "EXEC dbo.Declaracion_Emitidas @Year, @Month, @RFC_Emisor", common)).AsList();

        emitidasTotals = await conn.QueryFirstOrDefaultAsync<DeclaracionTotales>(
          "EXEC dbo.Declaracion_Emitidas_Totales @Year, @Month, @RFC_Emisor", common);

        emitidasNomina = (await conn.QueryAsync<DeclaracionEmitida>(
          "EXEC cfdi.Declaracion_Emitidas_Nomina @Year, @Month, @RFC_Emisor", common)).AsList();

        emitidasNominaTotals = await conn.QueryFirstOrDefaultAsync<DeclaracionTotales>(
          "EXEC cfdi.Declaracion_Emitidas_Nomina_Totales @Year, @Month, @RFC_Emisor", common);

        recibidas = (await conn.QueryAsync<DeclaracionRecibida>(
          "EXEC dbo.Declaracion_Recibidas @Year, @Month, @RFC_Receptor", common)).AsList();

        recibidasTotals = await conn.QueryFirstOrDefaultAsync<DeclaracionTotales>(
          "EXEC dbo.Declaracion_Recibidas_Totales @Year, @Month, @RFC_Receptor", common);

        recibidasNomina = (await conn.QueryAsync<DeclaracionRecibida>(
          "EXEC cfdi.Declaracion_Recibidas_Nomina @Year, @Month, @RFC_Receptor", common)).AsList();

        recibidasNominaTotals = await conn.QueryFirstOrDefaultAsync<DeclaracionTotales>(
          "EXEC cfdi.Declaracion_Recibidas_Nomina_Totales @Year, @Month, @RFC_Receptor", common);

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


    // Utility to format date for SQL as string (if needed; SP might accept date properly too)
    private string GetSqlDate(DateTime dt) => dt.ToString("yyyy-MM-dd HH:mm:ss");

    // Compute StartDate and EndDate based on current filter:
    private DateTime startDate => isAnnual ? new DateTime(selectedYear, 1, 1) : new DateTime(selectedYear, selectedMonth, 1);
    private DateTime endDate => isAnnual
        ? new DateTime(selectedYear, 12, 31)
        : new DateTime(selectedYear, selectedMonth, DateTime.DaysInMonth(selectedYear, selectedMonth));
  }
}
