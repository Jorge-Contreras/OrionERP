using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using OfficeOpenXml;
using Microsoft.JSInterop;

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

          // Headers aligned to DeclaracionRecibida
          string[] headersR = new string[]
          {
        "Comprobante_ID", "Incluido", "Fecha", "Mes", "Año", "Emisor",
        "SubTotal", "Descuento", "SubTotal_Desc", "Actos16", "Actos0",
        "IVA", "IEPS", "IVA_RETENIDO", "ISR_RETENIDO", "IEPS_RETENIDO",
        "Total", "UUID", "FormaPago", "TipoDeComprobante", "MetodoPago",
        "UsoCFDI", "FechaCancelacion", "Estatus", "Transacción Fechas",
        "Poliza", "SumaPolizas"
          };

          // Write headers
          for (int j = 0; j < headersR.Length; j++)
            wsR.Cells[1, j + 1].Value = headersR[j];

          int row = 2;
          foreach (var it in recibidas)
          {
            wsR.Cells[row, 1].Value = it.Comprobante_Id;
            wsR.Cells[row, 2].Value = it.D;
            wsR.Cells[row, 3].Value = it.Fecha; // you can also set a date format below
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
            wsR.Cells[row, 23].Value = it.FechaCancelacion?.ToString("yyyy-MM-dd");
            wsR.Cells[row, 24].Value = it.Estatus;
            wsR.Cells[row, 25].Value = it.fechastransacciones;
            wsR.Cells[row, 26].Value = it.Poliza;
            wsR.Cells[row, 27].Value = it.SumaPolizas;
            row++;
          }

          // Totals row
          wsR.Cells[row, 1].Value = "Totals:";

          // Sum numeric money columns (G..Q = 7..17)
          wsR.Cells[row, 7].Formula = $"SUM(G2:G{row - 1})";   // SubTotal
          wsR.Cells[row, 8].Formula = $"SUM(H2:H{row - 1})";   // Descuento
          wsR.Cells[row, 9].Formula = $"SUM(I2:I{row - 1})";   // SubTotal_Desc
          wsR.Cells[row, 10].Formula = $"SUM(J2:J{row - 1})";   // Actos16
          wsR.Cells[row, 11].Formula = $"SUM(K2:K{row - 1})";   // Actos0
          wsR.Cells[row, 12].Formula = $"SUM(L2:L{row - 1})";   // IVA
          wsR.Cells[row, 13].Formula = $"SUM(M2:M{row - 1})";   // IEPS
          wsR.Cells[row, 14].Formula = $"SUM(N2:N{row - 1})";   // IVA_RETENIDO
          wsR.Cells[row, 15].Formula = $"SUM(O2:O{row - 1})";   // ISR_RETENIDO
          wsR.Cells[row, 16].Formula = $"SUM(P2:P{row - 1})";   // IEPS_RETENIDO
          wsR.Cells[row, 17].Formula = $"SUM(Q2:Q{row - 1})";   // Total

          // Number formats
          for (int col = 7; col <= 17; col++)
            wsR.Column(col).Style.Numberformat.Format = "#,##0.00";

          // Optional: integer format for SumaPolizas
          wsR.Column(27).Style.Numberformat.Format = "#,##0";

          // Optional: date formats
          wsR.Column(3).Style.Numberformat.Format = "yyyy-mm-dd"; // Fecha
                                                                  // Column 23 is written as string above; if you store DateTime instead, format it:
                                                                  // wsR.Column(23).Style.Numberformat.Format = "yyyy-mm-dd";

          // Header style and autofit
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
