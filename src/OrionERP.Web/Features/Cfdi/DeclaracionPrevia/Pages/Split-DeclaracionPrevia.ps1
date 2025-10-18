# Split-DeclaracionPrevia.ps1
# Usage:  pwsh -File .\Split-DeclaracionPrevia.ps1
# (Edit $Dir if your path is different.)

param(
  [string]$Dir = "C:\Users\jc_ca\OrionERP\src\OrionERP.Web\Features\Cfdi\DeclaracionPrevia\Pages"
)

Write-Host "Target directory: $Dir"

if (-not (Test-Path $Dir)) {
  throw "Directory not found: $Dir"
}

$orig = Join-Path $Dir "DeclaracionPrevia.razor.cs"
if (Test-Path $orig) {
  $ts = Get-Date -Format "yyyyMMdd-HHmmss"
  $backup = "$orig.$ts.bak"
  Copy-Item $orig $backup -Force
  Write-Host "Backed up original to: $backup"
  $renamed = Join-Path $Dir "DeclaracionPrevia.ORIGINAL_$ts.razor.cs"
  Rename-Item $orig $renamed -Force
  Write-Host "Renamed original to: $renamed"
} else {
  Write-Host "Original file not found (continuing to write split files)."
}

function Write-File {
  param([string]$Name, [string]$Content)
  $path = Join-Path $Dir $Name
  $Content | Set-Content -Path $path -Encoding UTF8
  Write-Host "Wrote $Name"
}

# ------------- Root (only place that inherits ComponentBase and holds Nav inject) -------------
Write-File -Name "DeclaracionPrevia.Root.cs" -Content @'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using System.Net.Http.Headers;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OfficeOpenXml;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia : ComponentBase
  {
    // Keep this too if you use it elsewhere:
    // Data models corresponding to stored procedure outputs:
    [Inject] private NavigationManager Nav { get; set; } = default!;
  }
}
'@

# ------------- Models (nested DTOs only) -------------
Write-File -Name "DeclaracionPrevia.Models.cs" -Content @'
using System;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia
  {
    public class DeclaracionEmitida
    {
      public int Comprobante_Id { get; set; }
      public string? D { get; set; }            // "✓" or "X"
      public DateTime Fecha { get; set; }
      public string? MES_GLOBAL { get; set; }
      public string?  ANIO_GLOBAL { get; set; }
      public string? RECEPTOR { get; set; }
      public decimal SubTotal { get; set; }
      public decimal Descuento { get; set; }
      public decimal SubTotal_Desc { get; set; }
      public decimal Actos_16 { get; set; }
      public decimal Actos_0 { get; set; }
      public decimal IVA { get; set; }
      public decimal IEPS { get; set; }
      public decimal IVA_RETENIDO { get; set; }
      public decimal ISR_RETENIDO { get; set; }
      public decimal IEPS_RETENIDO { get; set; }
      public decimal Total { get; set; }
      public string? FOLIO_FISCAL { get; set; }
      public string? FormaPago { get; set; }
      public string?  TipoDeComprobante { get; set; }
      public string? MetodoPago { get; set; }
      public string? UsoCFDI { get; set; }
      public DateTime? FechaCancelacion { get; set; }
      public string? Estatus { get; set; }
      public string? fechastransacciones { get; set; }
      public string? Poliza { get; set; }
      public int? SumaPolizas { get; set; }
    }

    public class DeclaracionRecibida
    {
      public int Comprobante_Id { get; set; }
      public string? D { get; set; }
      public DateTime Fecha { get; set; }
      public string? MES_GLOBAL { get; set; }
      public string? ANIO_GLOBAL { get; set; }
      public string? EMISOR { get; set; }
      public decimal SubTotal { get; set; }
      public decimal Descuento { get; set; }
      public decimal SubTotal_Desc { get; set; }
      public decimal Actos_16 { get; set; }
      public decimal Actos_0 { get; set; }
      public decimal IVA { get; set; }
      public decimal IEPS { get; set; }
      public decimal IVA_RETENIDO { get; set; }
      public decimal ISR_RETENIDO { get; set; }
      public decimal IEPS_RETENIDO { get; set; }
      public decimal Total { get; set; }
      public string? FOLIO_FISCAL { get; set; }
      public string? FormaPago { get; set; }
      public string? TipoDeComprobante { get; set; }
      public string? MetodoPago { get; set; }
      public string? UsoCFDI { get; set; }
      public DateTime? FechaPago { get; set; }    // if the SP returns payment date
      public string? Estatus { get; set; }
      public long? TransaccionVinculada { get; set; }  // if the SP returns linked transaction ID
    }

    public class DesfaseItem
    {
      public int Comprobante_Id { get; set; }
      public int? Transaccion_Id { get; set; }
      public DateTime? FechaComprobante { get; set; }
      public string? MesComprobante { get; set; }
      public string? AnioComprobante { get; set; }
      public string? RFC_Emisor { get; set; }
      public string? RFC_Receptor { get; set; }
      public decimal? TotalComprobante { get; set; }
      public string? CuentaPago { get; set; }
      public DateTime? FechaTransaccion { get; set; }
      public string? Observaciones { get; set; }
    }

    public class PolizaNoConsolidada
    {
      public int Transaccion_ID { get; set; }
      public DateTime Fecha { get; set; }
      public string? Concepto { get; set; }
      public decimal Monto { get; set; }
      public string? Cuenta { get; set; }
      public string? Tipo_Poliza { get; set; }
      public string? Forma_Pago { get; set; }
      public string? RFC { get; set; }
      public string? Observaciones { get; set; }
    }

    // Classes for Totals results (with only needed fields):
    public class DeclaracionTotales
    {
      public int CountCFDIs { get; set; }
      public string? SumSubTotal { get; set; }
      public string? SumDescuento { get; set; }
      public string? SumSubTotalDesc { get; set; }
      public string? SumActos16 { get; set; }
      public string? SumActos0 { get; set; }
      public string? SumIVA { get; set; }
      public string? SumIEPS { get; set; }
      public string? SumIVA_RETENIDO { get; set; }
      public string? SumISR_RETENIDO { get; set; }
      public string? SumIEPS_RETENIDO { get; set; }
      public string? SumTotal { get; set; }
    }

    public class DesfaseTotales
    {
      public int CountCFDIs { get; set; }
      public string? SumTotal { get; set; }
    }
  }
}
'@

# ------------- State (fields, sorting, pagination) -------------
Write-File -Name "DeclaracionPrevia.State.cs" -Content @'
using System;
using System.Collections.Generic;
using System.Linq;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia
  {
    // UI State
    private string? connectionString;

    // Filter state
    private List<string>? disponiblesRFCs;
    private List<int>? disponibleYears;
    private List<(int, string)>? disponibleMonths;
    private string? selectedRfc;
    private int selectedYear;
    private int selectedMonth;
    private bool isAnnual;

    // Data lists and other outputs
    private List<DeclaracionEmitida>? emitidas;
    private List<DeclaracionRecibida>? recibidas;
    private List<DesfaseItem>? desfase;
    private List<PolizaNoConsolidada>? polizasNoConsolidadas;
    private DeclaracionTotales? emitidasTotals;
    private DeclaracionTotales? recibidasTotals;
    private DesfaseTotales? desfaseTotals;
    private string? impuestosSummary;
    private string? bancosCajaSummary;

    // For UI selection and messages
    private DeclaracionEmitida? selectedEmitida;
    private DeclaracionRecibida? selectedRecibida;
    private string? statusMessage;
    private string? errorMessage;

    // Sorting state
    private Dictionary<string, string>? emitidasSortableFields;
    private Dictionary<string, string>? recibidasSortableFields;
    private string? emitidasSortColumn;
    private string? emitidasSortOrder;
    private string? recibidasSortColumn;
    private string? recibidasSortOrder;

    // Pagination state (simple implementation)
    private int pageSize = 50;
    private int emitidasCurrentPage = 1;
    private int emitidasPageCount = 1;
    private IEnumerable<DeclaracionEmitida> emitidasPage => emitidas?.Skip((emitidasCurrentPage - 1) * pageSize).Take(pageSize) ?? Enumerable.Empty<DeclaracionEmitida>();
    private int recibidasCurrentPage = 1;
    private int recibidasPageCount = 1;
    private IEnumerable<DeclaracionRecibida> recibidasPage => recibidas?.Skip((recibidasCurrentPage - 1) * pageSize).Take(pageSize) ?? Enumerable.Empty<DeclaracionRecibida>();
  }
}
'@

# ------------- Lifecycle + LoadAllData + date helpers -------------
Write-File -Name "DeclaracionPrevia.LifecycleAndLoad.cs" -Content @'
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
'@

# ------------- Filters + Sorting -------------
Write-File -Name "DeclaracionPrevia.FiltersAndSorting.cs" -Content @'
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia
  {
    // Filter change handlers:
    private async Task OnFiltersChangedAsync()
    {
      emitidasCurrentPage = 1;
      recibidasCurrentPage = 1;
      await LoadAllData();
    }

    private async Task OnFilterChanged(ChangeEventArgs e)
    {
      // Whenever any filter (RFC, Year, Month, Annual) changes, reload data:
      await OnFiltersChangedAsync();
    }

    // Sorting:
    private void ApplySorting()
    {
      if (emitidas != null)
      {
        // Sort emitidas list based on emitidasSortColumn and emitidasSortOrder
        System.Comparison<DeclaracionEmitida> comparison = (a, b) => 0;
        switch (emitidasSortColumn)
        {
          case "Fecha":
            comparison = (a, b) => a.Fecha.CompareTo(b.Fecha);
            break;
          case "RECEPTOR":
            comparison = (a, b) => string.Compare(a.RECEPTOR, b.RECEPTOR, System.StringComparison.CurrentCultureIgnoreCase);
            break;
          case "Total":
            comparison = (a, b) => a.Total.CompareTo(b.Total);
            break;
          case "FOLIO_FISCAL":
            comparison = (a, b) => string.Compare(a.FOLIO_FISCAL, b.FOLIO_FISCAL, System.StringComparison.CurrentCultureIgnoreCase);
            break;
        }
        emitidas.Sort(comparison);
        if (emitidasSortOrder == "DESC")
        {
          emitidas.Reverse();
        }
      }
      if (recibidas != null)
      {
        System.Comparison<DeclaracionRecibida> comparison = (a, b) => 0;
        switch (recibidasSortColumn)
        {
          case "Fecha":
            comparison = (a, b) => a.Fecha.CompareTo(b.Fecha);
            break;
          case "EMISOR":
            comparison = (a, b) => string.Compare(a.EMISOR, b.EMISOR, System.StringComparison.CurrentCultureIgnoreCase);
            break;
          case "Total":
            comparison = (a, b) => a.Total.CompareTo(b.Total);
            break;
          case "FOLIO_FISCAL":
            comparison = (a, b) => string.Compare(a.FOLIO_FISCAL, b.FOLIO_FISCAL, System.StringComparison.CurrentCultureIgnoreCase);
            break;
        }
        recibidas.Sort(comparison);
        if (recibidasSortOrder == "DESC")
        {
          recibidas.Reverse();
        }
      }
    }

    private async Task OnEmitidasSortsChangedAsync()
    {
      ApplySorting();
      emitidasCurrentPage = 1;
      await LoadAllData();
    }
    private async Task OnEmitidasSortChanged(ChangeEventArgs e)
    {
      await OnEmitidasSortsChangedAsync();
    }
    private async Task OnRecibidasSortChangedAsync()
    {
      ApplySorting();
      recibidasCurrentPage = 1;
      await LoadAllData();
    }
    private async Task OnRecibidasSortChanged(ChangeEventArgs e)
    {
      await OnRecibidasSortChangedAsync();
    }
  }
}
'@

# ------------- Selection + Toggle + Bulk exclude -------------
Write-File -Name "DeclaracionPrevia.SelectionAndToggle.cs" -Content @'
using System;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia
  {
    // Row selection:
    private void SelectEmitida(DeclaracionEmitida item)
    {
      selectedEmitida = item;
      selectedRecibida = null;
    }
    private void SelectRecibida(DeclaracionRecibida item)
    {
      selectedRecibida = item;
      selectedEmitida = null;
    }

    // Toggle Include/Exclude for selected invoice (Emitidas)
    private async Task ToggleEmitidaSelected()
    {
      if (selectedEmitida == null)
      {
        statusMessage = "Selecciona una factura emitida primero.";
        return;
      }
      try
      {
        using var conn = new SqlConnection(connectionString);
        string sql = "UPDATE Comprobante SET Incluir_En_Declaracion = CASE WHEN Incluir_En_Declaracion = 1 THEN 0 ELSE 1 END WHERE Comprobante_Id = @Id";
        await conn.ExecuteAsync(sql, new { Id = selectedEmitida.Comprobante_Id });
        // Refresh data (or at least refresh that one item):
        await LoadAllData();
        statusMessage = "Factura emitida marcada como " + (selectedEmitida.D == "✓" ? "EXCLUIDA" : "INCLUIDA") + " en la declaración.";
      }
      catch (Exception ex)
      {
        errorMessage = "Error al actualizar factura emitida: " + ex.Message;
      }
    }

    // Toggle Include/Exclude for selected invoice (Recibidas)
    private async Task ToggleRecibidaSelected()
    {
      if (selectedRecibida == null)
      {
        statusMessage = "Selecciona una factura recibida primero.";
        return;
      }
      try
      {
        using var conn = new SqlConnection(connectionString);
        string sql = "UPDATE Comprobante SET Incluir_En_Declaracion = CASE WHEN Incluir_En_Declaracion = 1 THEN 0 ELSE 1 END WHERE Comprobante_Id = @Id";
        await conn.ExecuteAsync(sql, new { Id = selectedRecibida.Comprobante_Id });
        await LoadAllData();
        statusMessage = "Factura recibida marcada como " + (selectedRecibida.D == "✓" ? "EXCLUIDA" : "INCLUIDA") + " en la declaración.";
      }
      catch (Exception ex)
      {
        errorMessage = "Error al actualizar factura recibida: " + ex.Message;
      }
    }

    // Exclude all "Pago" or "Devolución" type invoices in Recibidas list
    private async Task ExcludePagosYDevoluciones()
    {
      try
      {
        using var conn = new SqlConnection(connectionString);
        string sql = @"
                    UPDATE C
                    SET Incluir_En_Declaracion = 0
                    FROM Comprobante C
                    JOIN Receptor R ON C.Comprobante_ID = R.Comprobante_ID
                    WHERE C.Incluir_En_Declaracion = 1
                      AND (R.UsoCFDI = 'G02' OR R.UsoCFDI = 'CP01')
                      AND R.RFC = @RFC
                      AND (YEAR(C.Fecha) = @Year AND (@Month IS NULL OR MONTH(C.Fecha) = @Month))";
        int affected = await conn.ExecuteAsync(sql, new { RFC = selectedRfc, Year = selectedYear, Month = isAnnual ? (object)DBNull.Value : selectedMonth });
        await LoadAllData();
        if (affected > 0)
        {
          statusMessage = $"Se excluyeron {affected} comprobantes de tipo Pago/Devolución de la declaración.";
        }
        else
        {
          statusMessage = "No se encontraron CFDIs de tipo Pago o Devolución para excluir.";
        }
      }
      catch (Exception ex)
      {
        errorMessage = "Error al excluir pagos/devoluciones: " + ex.Message;
      }
    }
  }
}
'@

# ------------- Facturama cancel -------------
Write-File -Name "DeclaracionPrevia.FacturamaCancel.cs" -Content @'
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia
  {
    // Cancel selected Emitida CFDI via Facturama API
    private async Task CancelSelectedEmitidaCfdi()
    {
      if (selectedEmitida == null)
      {
        statusMessage = "Selecciona una factura emitida a cancelar.";
        return;
      }
      // Confirm with user:
      bool confirm = await JS.InvokeAsync<bool>("confirm", $"¿Seguro que desea solicitar la cancelación del CFDI con UUID:\n{selectedEmitida.FOLIO_FISCAL}?\nEsta acción no se puede deshacer.");
      if (!confirm)
      {
        return;
      }
      try
      {
        // Facturama API credentials (should be in config ideally)
        string facturamaUser = Configuration["Facturama:User"] ?? "jorgecontreras82";
        string facturamaPassword = Configuration["Facturama:Password"] ?? "Orion2020";
        string authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{facturamaUser}:{facturamaPassword}"));
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // 1. GET CFDI by UUID to retrieve its internal ID
        string queryUrl = $"https://api.facturama.mx/cfdi?type=issued&uuid={selectedEmitida.FOLIO_FISCAL}";
        var getResp = await client.GetAsync(queryUrl);
        if (!getResp.IsSuccessStatusCode)
        {
          errorMessage = $"Error al buscar CFDI en Facturama. Status: {(int)getResp.StatusCode} - {getResp.ReasonPhrase}";
          return;
        }
        string getBody = await getResp.Content.ReadAsStringAsync();
        string? cfdiId = null;
        try
        {
          using var jdoc = JsonDocument.Parse(getBody);
          if (jdoc.RootElement.ValueKind == JsonValueKind.Array && jdoc.RootElement.GetArrayLength() > 0)
          {
            cfdiId = jdoc.RootElement[0].GetProperty("Id").GetString();
          }
        }
        catch
        {
          // If parsing fails
          errorMessage = "No se pudo interpretar la respuesta de Facturama (CFDI no encontrado?).";
          return;
        }
        if (string.IsNullOrEmpty(cfdiId))
        {
          errorMessage = "No se encontró el CFDI en Facturama para ese UUID.";
          return;
        }
        // 2. DELETE request to cancel
        string cancelUrl = $"https://api.facturama.mx/cfdi/{cfdiId}?type=issued&motive=02";
        var deleteResp = await client.DeleteAsync(cancelUrl);
        string deleteBody = await deleteResp.Content.ReadAsStringAsync();
        if (!deleteResp.IsSuccessStatusCode)
        {
          errorMessage = $"Error al solicitar la cancelación. Status: {(int)deleteResp.StatusCode}. Detalles: {deleteBody}";
          return;
        }
        // Parse status if possible:
        string statusReturned = "Desconocido";
        try
        {
          using var jdoc2 = JsonDocument.Parse(deleteBody);
          if (jdoc2.RootElement.TryGetProperty("Status", out var statusProp))
          {
            statusReturned = statusProp.GetString() ?? statusReturned;
          }
        }
        catch { /* ignore parse errors */ }
        // Mark as excluded in DB:
        try
        {
          using var conn = new SqlConnection(connectionString);
          await conn.ExecuteAsync("UPDATE Comprobante SET Incluir_En_Declaracion = 0 WHERE Comprobante_Id = @Id", new { Id = selectedEmitida.Comprobante_Id });
        }
        catch { /* even if this fails, proceed */ }
        await LoadAllData();
        statusMessage = $"Cancelación solicitada para CFDI UUID {selectedEmitida.FOLIO_FISCAL}. Estado devuelto: {statusReturned}";
      }
      catch (Exception ex)
      {
        errorMessage = "Error en el proceso de cancelación: " + ex.Message;
      }
    }
  }
}
'@

# ------------- Exports (DIOT + Excel) -------------
Write-File -Name "DeclaracionPrevia.Exports.cs" -Content @'
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using OfficeOpenXml;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia
  {
    // Generate DIOT text file for the current period
    private async Task GenerateDIOT()
    {
      if (isAnnual)
      {
        // Typically DIOT is monthly; if annual, we might not allow
        errorMessage = "La DIOT solo se puede generar para un periodo mensual específico.";
        return;
      }
      try
      {
        using var conn = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        var lines = (await conn.QueryAsync<string>("EXEC dbo.GenerateDIOTTXT @Year, @Month, @receptor",
                        new { Year = selectedYear, Month = selectedMonth, receptor = selectedRfc })).ToList();
        if (lines == null || lines.Count == 0)
        {
          errorMessage = "No se obtuvieron datos para generar la DIOT.";
          return;
        }
        // Combine lines into one text blob
        string diotContent = string.Join("\r\n", lines);
        string fileName = $"DIOT-{selectedRfc}-{selectedYear}-{selectedMonth:D2}.txt";
        // Initiate download via JS (create a Blob and download)
        var contentBytes = Encoding.UTF8.GetBytes(diotContent);
        string base64 = Convert.ToBase64String(contentBytes);
        string dataUrl = $"data:text/plain;base64,{base64}";
        await JS.InvokeVoidAsync("triggerFileDownload", fileName, dataUrl);
        statusMessage = "Archivo DIOT generado y descargado.";
      }
      catch (Exception ex)
      {
        errorMessage = "Error al generar DIOT: " + ex.Message;
      }
    }

    // Export Emitidas list to Excel
    private async Task ExportExcelEmitidas()
    {
      await ExportExcel(includeEmitidas: true, includeRecibidas: false);
    }
    // Export Recibidas list to Excel
    private async Task ExportExcelRecibidas()
    {
      await ExportExcel(includeEmitidas: false, includeRecibidas: true);
    }
    // Combined export (if needed, can call include both)
    private async Task ExportExcel(bool includeEmitidas, bool includeRecibidas)
    {
      try
      {
        // Using EPPlus to create Excel in memory
        using var package = new OfficeOpenXml.ExcelPackage();
        if (includeEmitidas && emitidas != null)
        {
          var wsE = package.Workbook.Worksheets.Add("Emitidas");
          // Headers:
          string[] headersE = new string[] { "Comprobante_ID", "Incluido", "Fecha", "Mes", "Año", "Receptor", "SubTotal", "Descuento", "SubTotal_Desc", "Actos16", "Actos0", "IVA", "IEPS", "IVA_RETENIDO", "ISR_RETENIDO", "IEPS_RETENIDO", "Total", "UUID", "FormaPago", "TipoDeComprobante", "MetodoPago", "UsoCFDI", "FechaCancelacion", "Estatus", "Poliza", "SumaPolizas" };
          for (int j = 0; j < headersE.Length; j++)
            wsE.Cells[1, j + 1].Value = headersE[j];
          // Data rows:
          int row = 2;
          foreach (var it in emitidas)
          {
            wsE.Cells[row, 1].Value = it.Comprobante_Id;
            wsE.Cells[row, 2].Value = it.D;
            wsE.Cells[row, 3].Value = it.Fecha;
            wsE.Cells[row, 4].Value = it.MES_GLOBAL;
            wsE.Cells[row, 5].Value = it.ANIO_GLOBAL;
            wsE.Cells[row, 6].Value = it.RECEPTOR;
            wsE.Cells[row, 7].Value = it.SubTotal;
            wsE.Cells[row, 8].Value = it.Descuento;
            wsE.Cells[row, 9].Value = it.SubTotal_Desc;
            wsE.Cells[row, 10].Value = it.Actos_16;
            wsE.Cells[row, 11].Value = it.Actos_0;
            wsE.Cells[row, 12].Value = it.IVA;
            wsE.Cells[row, 13].Value = it.IEPS;
            wsE.Cells[row, 14].Value = it.IVA_RETENIDO;
            wsE.Cells[row, 15].Value = it.ISR_RETENIDO;
            wsE.Cells[row, 16].Value = it.IEPS_RETENIDO;
            wsE.Cells[row, 17].Value = it.Total;
            wsE.Cells[row, 18].Value = it.FOLIO_FISCAL;
            wsE.Cells[row, 19].Value = it.FormaPago;
            wsE.Cells[row, 20].Value = it.TipoDeComprobante;
            wsE.Cells[row, 21].Value = it.MetodoPago;
            wsE.Cells[row, 22].Value = it.UsoCFDI;
            wsE.Cells[row, 23].Value = it.FechaCancelacion?.ToString("yyyy-MM-dd");
            wsE.Cells[row, 24].Value = it.Estatus;
            wsE.Cells[row, 25].Value = it.Poliza;
            wsE.Cells[row, 26].Value = it.SumaPolizas;
            row++;
          }
          // Totals row:
          wsE.Cells[row, 1].Value = "Totals:";
          wsE.Cells[row, 7].Formula = $"SUM(G2:G{row - 1})";   // SubTotal
          wsE.Cells[row, 8].Formula = $"SUM(H2:H{row - 1})";
          wsE.Cells[row, 9].Formula = $"SUM(I2:I{row - 1})";
          wsE.Cells[row, 10].Formula = $"SUM(J2:J{row - 1})";
          wsE.Cells[row, 11].Formula = $"SUM(K2:K{row - 1})";
          wsE.Cells[row, 12].Formula = $"SUM(L2:L{row - 1})";
          wsE.Cells[row, 13].Formula = $"SUM(M2:M{row - 1})";
          wsE.Cells[row, 14].Formula = $"SUM(N2:N{row - 1})";
          wsE.Cells[row, 15].Formula = $"SUM(O2:O{row - 1})";
          wsE.Cells[row, 16].Formula = $"SUM(P2:P{row - 1})";
          wsE.Cells[row, 17].Formula = $"SUM(Q2:Q{row - 1})";  // Total
          for (int col = 7; col <= 17; col++)
            wsE.Column(col).Style.Numberformat.Format = "#,##0.00";
          wsE.Cells[1, 1, 1, headersE.Length].Style.Font.Bold = true;
          wsE.Cells.AutoFitColumns();
        }
        if (includeRecibidas && recibidas != null)
        {
          var wsR = package.Workbook.Worksheets.Add("Recibidas");
          string[] headersR = new string[] { "Comprobante_ID", "Incluido", "Fecha", "Mes", "Año", "Emisor", "SubTotal", "Descuento", "SubTotal_Desc", "Actos16", "Actos0", "IVA", "IEPS", "IVA_RETENIDO", "ISR_RETENIDO", "IEPS_RETENIDO", "Total", "UUID", "FormaPago", "TipoDeComprobante", "MetodoPago", "UsoCFDI", "FechaPago", "Estatus", "Transaccion_ID" };
          for (int j = 0; j < headersR.Length; j++)
            wsR.Cells[1, j + 1].Value = headersR[j];
          int row = 2;
          foreach (var it in recibidas)
          {
            wsR.Cells[row, 1].Value = it.Comprobante_Id;
            wsR.Cells[row, 2].Value = it.D;
            wsR.Cells[row, 3].Value = it.Fecha;
            wsR.Cells[row, 4].Value = it.MES_GLOBAL;
            wsR.Cells[row, 5].Value = it.ANIO_GLOBAL;
            wsR.Cells[row, 6].Value = it.EMISOR;
            wsR.Cells[row, 7].Value = it.SubTotal;
            wsR.Cells[row, 8].Value = it.Descuento;
            wsR.Cells[row, 9].Value = it.SubTotal_Desc;
            wsR.Cells[row, 10].Value = it.Actos_16;
            wsR.Cells[row, 11].Value = it.Actos_0;
            wsR.Cells[row, 12].Value = it.IVA;
            wsR.Cells[row, 13].Value = it.IEPS;
            wsR.Cells[row, 14].Value = it.IVA_RETENIDO;
            wsR.Cells[row, 15].Value = it.ISR_RETENIDO;
            wsR.Cells[row, 16].Value = it.IEPS_RETENIDO;
            wsR.Cells[row, 17].Value = it.Total;
            wsR.Cells[row, 18].Value = it.FOLIO_FISCAL;
            wsR.Cells[row, 19].Value = it.FormaPago;
            wsR.Cells[row, 20].Value = it.TipoDeComprobante;
            wsR.Cells[row, 21].Value = it.MetodoPago;
            wsR.Cells[row, 22].Value = it.UsoCFDI;
            wsR.Cells[row, 23].Value = it.FechaPago?.ToString("yyyy-MM-dd");
            wsR.Cells[row, 24].Value = it.Estatus;
            wsR.Cells[row, 25].Value = it.TransaccionVinculada;
            row++;
          }
          // Totals:
          wsR.Cells[row, 1].Value = "Totals:";
          wsR.Cells[row, 7].Formula = $"SUM(G2:G{row - 1})";
          wsR.Cells[row, 8].Formula = $"SUM(H2:H{row - 1})";
          wsR.Cells[row, 9].Formula = $"SUM(I2:I{row - 1})";
          wsR.Cells[row, 10].Formula = $"SUM(J2:J{row - 1})";
          wsR.Cells[row, 11].Formula = $"SUM(K2:K{row - 1})";
          wsR.Cells[row, 12].Formula = $"SUM(L2:L{row - 1})";
          wsR.Cells[row, 13].Formula = $"SUM(M2:M{row - 1})";
          wsR.Cells[row, 14].Formula = $"SUM(N2:N{row - 1})";
          wsR.Cells[row, 15].Formula = $"SUM(O2:O{row - 1})";
          wsR.Cells[row, 16].Formula = $"SUM(P2:P{row - 1})";
          wsR.Cells[row, 17].Formula = $"SUM(Q2:Q{row - 1})";
          for (int col = 7; col <= 17; col++)
            wsR.Column(col).Style.Numberformat.Format = "#,##0.00";
          wsR.Cells[1, 1, 1, headersR.Length].Style.Font.Bold = true;
          wsR.Cells.AutoFitColumns();
        }
        // Prepare file for download:
        byte[] fileBytes = package.GetAsByteArray();
        string fileName = "DeclaracionPrevia";
        if (includeEmitidas && includeRecibidas) fileName += "_Emitidas_Recibidas";
        else if (includeEmitidas) fileName += "_Emitidas";
        else if (includeRecibidas) fileName += "_Recibidas";
        fileName += $"_{selectedYear}{(isAnnual ? "" : "_" + selectedMonth.ToString("D2"))}.xlsx";
        string base64 = Convert.ToBase64String(fileBytes);
        string dataUrl = $"data:application/vnd.openxmlformats-officedocument.spreadsheetml.sheet;base64,{base64}";
        await JS.InvokeVoidAsync("triggerFileDownload", fileName, dataUrl);
        statusMessage = $"Archivo Excel '{fileName}' generado y descargado.";
      }
      catch (Exception ex)
      {
        errorMessage = "Error al exportar a Excel: " + ex.Message;
      }
    }
  }
}
'@

# ------------- Navigation + Linked transaction + Pagination -------------
Write-File -Name "DeclaracionPrevia.NavigationAndPagination.cs" -Content @'
using Dapper;
using Microsoft.Data.SqlClient;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia
  {
    // Navigation or open detail functions:
    private void OpenEmitidaDetails(DeclaracionEmitida item)
    {
      // For now, navigate to Comprobante detail page if exists
      if (item != null)
      {
        Nav.NavigateTo($"/cfdi/comprobante/{item.Comprobante_Id}");
      }
    }
    private void OpenRecibidaDetails(DeclaracionRecibida item)
    {
      if (item != null)
      {
        Nav.NavigateTo($"/cfdi/comprobante/{item.Comprobante_Id}");
      }
    }
    private void OpenLinkedTransaction(object item)
    {
      // item could be DeclaracionEmitida or DeclaracionRecibida, both potentially have linked Transaccion info
      long? transId = null;
      if (item is DeclaracionEmitida de)
      {
        // We need to find transaccion that corresponds. Possibly through Transaccion_Comprobante table:
        // If we had loaded TransaccionId via a query or stored it, we would use it.
        // The Access keydown event for Emitidas did: SELECT Transaccion_ID from Transaccion_Comprobante where Comprobante_ID = selected
        // We can quickly query that:
        try
        {
          using var conn = new SqlConnection(connectionString);
          transId = conn.ExecuteScalar<long?>("SELECT Transaccion_ID FROM Transaccion_Comprobante WHERE Comprobante_ID = @Cid", new { Cid = de.Comprobante_Id });
        }
        catch { transId = null; }
      }
      else if (item is DeclaracionRecibida dr)
      {
        transId = dr.TransaccionVinculada;
        if (!transId.HasValue)
        {
          // If not already provided, query similarly:
          try
          {
            using var conn = new SqlConnection(connectionString);
            transId = conn.ExecuteScalar<long?>("SELECT Transaccion_ID FROM Transaccion_Comprobante WHERE Comprobante_ID = @Cid", new { Cid = dr.Comprobante_Id });
          }
          catch { transId = null; }
        }
      }
      if (transId.HasValue)
      {
        Nav.NavigateTo($"/transacciones/{transId.Value}");
      }
      else
      {
        statusMessage = "No se encontró una Transacción vinculada a este CFDI.";
      }
    }

    // Pagination controls:
    private void NextEmitidasPage()
    {
      if (emitidasCurrentPage < emitidasPageCount)
      {
        emitidasCurrentPage++;
      }
    }
    private void PrevEmitidasPage()
    {
      if (emitidasCurrentPage > 1)
      {
        emitidasCurrentPage--;
      }
    }
    private void NextRecibidasPage()
    {
      if (recibidasCurrentPage < recibidasPageCount)
      {
        recibidasCurrentPage++;
      }
    }
    private void PrevRecibidasPage()
    {
      if (recibidasCurrentPage > 1)
      {
        recibidasCurrentPage--;
      }
    }
  }
}
'@

Write-Host "---------------------------------------------"
Write-Host "Split complete. Files created in:"
Write-Host "  $Dir"
Write-Host "Now build the solution. If needed, move the ORIGINAL_*.razor.cs backup elsewhere."
