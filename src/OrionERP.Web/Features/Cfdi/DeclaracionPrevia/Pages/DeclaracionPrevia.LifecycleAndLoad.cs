using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia
  {
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
        using var conn = new SqlConnection(connectionString);
        disponiblesRFCs = (await conn.QueryAsync<string>("SELECT DISTINCT Rfc FROM Emisor ORDER BY Rfc")).AsList();
      }
      catch
      {
        disponiblesRFCs = new List<string>(); // if fails, fallback
      }
      if (disponiblesRFCs == null || disponiblesRFCs.Count == 0)
      {
        // If none found, just use a default from config or known value
        disponiblesRFCs = new List<string> { "OHM191112Q26" };
      }
      selectedRfc = disponiblesRFCs[0];
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
      errorMessage = null;
      statusMessage = null;
      try
      {
        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        // Determine parameters for year/month
        string yearParam = isAnnual ? selectedYear.ToString() : selectedYear.ToString();
        string monthParam = isAnnual ? "NULL" : selectedMonth.ToString("D2");  // procedures expect NVARCHAR(2) for month or NULL
        string? rfcParam = selectedRfc;
        // Use a transaction or at least parallel queries if possible to reduce latency
        // We'll query each dataset:
        var emitidasTask = conn.QueryAsync<DeclaracionEmitida>("EXEC dbo.Declaracion_Emitidas @Year, @Month, @RFC_Emisor",
                            new { Year = isAnnual ? (object)DBNull.Value : selectedYear.ToString(), Month = isAnnual ? (object)DBNull.Value : selectedMonth.ToString("D2"), RFC_Emisor = rfcParam });
        var emitidasTotTask = conn.QueryFirstOrDefaultAsync<DeclaracionTotales>("EXEC dbo.Declaracion_Emitidas_Totales @Year, @Month, @RFC_Emisor",
                            new { Year = isAnnual ? (object)DBNull.Value : selectedYear.ToString(), Month = isAnnual ? (object)DBNull.Value : selectedMonth.ToString("D2"), RFC_Emisor = rfcParam });
        var recibidasTask = conn.QueryAsync<DeclaracionRecibida>("EXEC dbo.Declaracion_Recibidas @Year, @Month, @RFC_Receptor",
                            new { Year = isAnnual ? (object)DBNull.Value : selectedYear.ToString(), Month = isAnnual ? (object)DBNull.Value : selectedMonth.ToString("D2"), RFC_Receptor = rfcParam });
        var recibidasTotTask = conn.QueryFirstOrDefaultAsync<DeclaracionTotales>("EXEC dbo.Declaracion_Recibidas_Totales @Year, @Month, @RFC_Receptor",
                            new { Year = isAnnual ? (object)DBNull.Value : selectedYear.ToString(), Month = isAnnual ? (object)DBNull.Value : selectedMonth.ToString("D2"), RFC_Receptor = rfcParam });
        var desfaseTask = conn.QueryAsync<DesfaseItem>("EXEC dbo.Declaracion_Comprobantes_Con_Desfase @RFC, @Anio, @Mes",
                            new { RFC = rfcParam, Anio = selectedYear, Mes = selectedMonth });
        var desfaseTotTask = conn.QueryFirstOrDefaultAsync<DesfaseTotales>("EXEC dbo.Declaracion_Comprobantes_Con_Desfase_Totales @RFC, @Anio, @Mes",
                            new { RFC = rfcParam, Anio = selectedYear, Mes = selectedMonth });
        var polizasTask = conn.QueryAsync<PolizaNoConsolidada>("EXEC dbo.Polizas_No_Consolidadas @RFC, @Anio, @Mes",
                            new { RFC = rfcParam, Anio = selectedYear, Mes = selectedMonth });
        // Additionally, tax summary and banks:
        var impuestosTask = conn.QueryFirstOrDefaultAsync<string>("EXEC dbo.CALCULATE_TAXES @RFC, @startDate, @endDate",
                            new { RFC = rfcParam, startDate = GetSqlDate(startDate), endDate = GetSqlDate(endDate) });
        var bancosTask = conn.QueryFirstOrDefaultAsync<decimal?>("EXEC dbo.Reporte_Bancos_Caja @Year, @Month, @RFC",
                            new { Year = selectedYear, Month = (isAnnual ? (object)DBNull.Value : selectedMonth), RFC = rfcParam });
        // Wait for all tasks:
        emitidas = (await emitidasTask).AsList();
        emitidasTotals = await emitidasTotTask;
        recibidas = (await recibidasTask).AsList();
        recibidasTotals = await recibidasTotTask;
        desfase = (await desfaseTask).AsList();
        desfaseTotals = await desfaseTotTask;
        polizasNoConsolidadas = (await polizasTask).AsList();
        impuestosSummary = await impuestosTask ?? "";
        var bancosVal = await bancosTask;
        bancosCajaSummary = bancosVal.HasValue ? bancosVal.Value.ToString("C2") : "$0.00";
        // After loading, apply initial sorting:
        ApplySorting();
        // Reset selection and pagination:
        selectedEmitida = null;
        selectedRecibida = null;
        emitidasCurrentPage = 1;
        if (emitidas != null)
          emitidasPageCount = (int)Math.Ceiling(emitidas.Count / (double)pageSize);
        recibidasCurrentPage = 1;
        if (recibidas != null)
          recibidasPageCount = (int)Math.Ceiling(recibidas.Count / (double)pageSize);
      }
      catch (Exception ex)
      {
        errorMessage = "Error loading data: " + ex.Message;
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
