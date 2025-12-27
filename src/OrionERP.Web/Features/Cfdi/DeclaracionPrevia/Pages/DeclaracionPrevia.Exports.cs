using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using OfficeOpenXml;
using OrionERP.Application.Features.Cfdi.DeclaracionPrevia;

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
        SetErrorMessage("La DIOT solo se puede generar para un periodo mensual específico.");
        return;
      }
      try
      {
        var lines = (await DeclaracionService.GenerateDiotAsync(selectedRfc ?? string.Empty, selectedYear, selectedMonth)).ToList();
        if (lines == null || lines.Count == 0)
        {
          SetErrorMessage("No se obtuvieron datos para generar la DIOT.");
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
        SetErrorMessage("Error al generar DIOT: " + ex.Message);
      }
    }

    private async Task ExportExcelEmitidas() => await ExportExcel(includeEmitidas: true);

    private async Task ExportExcelRecibidas() => await ExportExcel(includeRecibidas: true);

    private async Task ExportExcelNominaEmitida() => await ExportExcel(includeNominaEmitida: true);

    private async Task ExportExcelNominaRecibida() => await ExportExcel(includeNominaRecibida: true);

    private async Task ExportExcelTipoEEmitidas() => await ExportExcel(includeTipoEEmitidas: true);

    private async Task ExportExcelTipoERecibidas() => await ExportExcel(includeTipoERecibidas: true);

    private async Task ExportExcelComplementosEmitidos() => await ExportExcelComplementos(includeComplementosEmitidos: true);

    private async Task ExportExcelComplementosRecibidos() => await ExportExcelComplementos(includeComplementosRecibidos: true);

    private async Task ExportExcel(
      bool includeEmitidas = false,
      bool includeRecibidas = false,
      bool includeNominaEmitida = false,
      bool includeNominaRecibida = false,
      bool includeTipoEEmitidas = false,
      bool includeTipoERecibidas = false)
    {
      try
      {
        using var package = new OfficeOpenXml.ExcelPackage();
        var includedSheets = new List<string>();

        if (includeEmitidas && AddWorksheet(package, "Emitidas", emitidasBase))
        {
          includedSheets.Add("Emitidas");
        }

        if (includeRecibidas && AddWorksheet(package, "Recibidas", recibidasBase))
        {
          includedSheets.Add("Recibidas");
        }

        if (includeNominaEmitida && AddWorksheet(package, "Nómina Emitida", emitidasNominaBase))
        {
          includedSheets.Add("NominaEmitida");
        }

        if (includeNominaRecibida && AddWorksheet(package, "Nómina Recibida", recibidasNominaBase))
        {
          includedSheets.Add("NominaRecibida");
        }

        if (includeTipoEEmitidas && AddWorksheet(package, "Tipo E Emitidas", tipoEEmitidasBase))
        {
          includedSheets.Add("TipoEEmitidas");
        }

        if (includeTipoERecibidas && AddWorksheet(package, "Tipo E Recibidas", tipoERecibidasBase))
        {
          includedSheets.Add("TipoERecibidas");
        }

        if (includedSheets.Count == 0)
        {
          SetErrorMessage("No hay datos para exportar.");
          return;
        }

        byte[] fileBytes = package.GetAsByteArray();
        string fileName = "DeclaracionPrevia";
        fileName += $"_{string.Join("_", includedSheets)}_{selectedYear}{(isAnnual ? string.Empty : "_" + selectedMonth.ToString("D2"))}.xlsx";
        string base64 = Convert.ToBase64String(fileBytes);
        string dataUrl = $"data:application/vnd.openxmlformats-officedocument.spreadsheetml.sheet;base64,{base64}";
        await JS.InvokeVoidAsync("triggerFileDownload", fileName, dataUrl);
        statusMessage = $"Archivo Excel '{fileName}' generado y descargado.";
      }
      catch (Exception ex)
      {
        SetErrorMessage("Error al exportar a Excel: " + ex.Message);
      }
    }

    private async Task ExportExcelComplementos(bool includeComplementosEmitidos = false, bool includeComplementosRecibidos = false)
    {
      try
      {
        using var package = new OfficeOpenXml.ExcelPackage();
        var includedSheets = new List<string>();

        if (includeComplementosEmitidos && AddComplementosWorksheet(package, "Complementos Emitidos", complementosEmitidosBase))
        {
          includedSheets.Add("ComplementosEmitidos");
        }

        if (includeComplementosRecibidos && AddComplementosWorksheet(package, "Complementos Recibidos", complementosRecibidosBase))
        {
          includedSheets.Add("ComplementosRecibidos");
        }

        if (includedSheets.Count == 0)
        {
          SetErrorMessage("No hay datos para exportar.");
          return;
        }

        byte[] fileBytes = package.GetAsByteArray();
        string fileName = "DeclaracionPrevia_Complementos";
        fileName += $"_{string.Join("_", includedSheets)}_{selectedYear}{(isAnnual ? string.Empty : "_" + selectedMonth.ToString("D2"))}.xlsx";
        string base64 = Convert.ToBase64String(fileBytes);
        string dataUrl = $"data:application/vnd.openxmlformats-officedocument.spreadsheetml.sheet;base64,{base64}";
        await JS.InvokeVoidAsync("triggerFileDownload", fileName, dataUrl);
        statusMessage = $"Archivo Excel '{fileName}' generado y descargado.";
      }
      catch (Exception ex)
      {
        SetErrorMessage("Error al exportar complementos a Excel: " + ex.Message);
      }
    }

    private bool AddWorksheet(OfficeOpenXml.ExcelPackage package, string sheetName, IEnumerable<DeclaracionCfdiBase>? items)
    {
      if (items == null || !items.Any())
      {
        return false;
      }

      var worksheet = package.Workbook.Worksheets.Add(sheetName);
      string[] headers =
      {
        "Comprobante_ID", "Incluido", "Fecha", "Mes", "Año", "Emisor", "RFC Emisor", "Receptor",
        "RFC Receptor", "SubTotal", "Descuento", "SubTotal_Desc", "Actos16", "Actos0", "IVA",
        "IEPS", "IVA_RETENIDO", "ISR_RETENIDO", "IEPS_RETENIDO", "Total", "UUID", "FormaPago",
        "TipoDeComprobante", "MetodoPago", "UsoCFDI", "FechaCancelacion", "Estatus", "Transacción Fechas",
        "Poliza", "SumaPolizas", "XML_Attachment_ID", "EsEmitida", "EsRecibida"
      };

      for (int j = 0; j < headers.Length; j++)
      {
        worksheet.Cells[1, j + 1].Value = headers[j];
      }

      int row = 2;
      foreach (var item in items)
      {
        worksheet.Cells[row, 1].Value = item.Comprobante_Id;
        worksheet.Cells[row, 2].Value = item.D;
        worksheet.Cells[row, 3].Value = item.Fecha;
        worksheet.Cells[row, 4].Value = item.MES_GLOBAL;
        worksheet.Cells[row, 5].Value = item.ANIO_GLOBAL;
        worksheet.Cells[row, 6].Value = item.EMISOR;
        worksheet.Cells[row, 7].Value = item.RFC_EMISOR;
        worksheet.Cells[row, 8].Value = item.RECEPTOR;
        worksheet.Cells[row, 9].Value = item.RFC_RECEPTOR;
        worksheet.Cells[row, 10].Value = item.SubTotal;
        worksheet.Cells[row, 11].Value = item.Descuento;
        worksheet.Cells[row, 12].Value = item.SubTotal_Desc;
        worksheet.Cells[row, 13].Value = item.Actos_16;
        worksheet.Cells[row, 14].Value = item.Actos_0;
        worksheet.Cells[row, 15].Value = item.IVA;
        worksheet.Cells[row, 16].Value = item.IEPS;
        worksheet.Cells[row, 17].Value = item.IVA_RETENIDO;
        worksheet.Cells[row, 18].Value = item.ISR_RETENIDO;
        worksheet.Cells[row, 19].Value = item.IEPS_RETENIDO;
        worksheet.Cells[row, 20].Value = item.Total;
        worksheet.Cells[row, 21].Value = item.FOLIO_FISCAL;
        worksheet.Cells[row, 22].Value = item.FormaPago;
        worksheet.Cells[row, 23].Value = item.TipoDeComprobante;
        worksheet.Cells[row, 24].Value = item.MetodoPago;
        worksheet.Cells[row, 25].Value = item.UsoCFDI;
        worksheet.Cells[row, 26].Value = item.FechaCancelacion;
        worksheet.Cells[row, 27].Value = item.Estatus;
        worksheet.Cells[row, 28].Value = item.fechastransacciones;
        worksheet.Cells[row, 29].Value = item.Poliza;
        worksheet.Cells[row, 30].Value = item.SumaPolizas;
        worksheet.Cells[row, 31].Value = item.XML_Attachment_ID;
        worksheet.Cells[row, 32].Value = item.EsEmitida;
        worksheet.Cells[row, 33].Value = item.EsRecibida;
        row++;
      }

      worksheet.Cells[row, 1].Value = "Totals:";
      worksheet.Cells[row, 10].Formula = $"SUM(J2:J{row - 1})";
      worksheet.Cells[row, 11].Formula = $"SUM(K2:K{row - 1})";
      worksheet.Cells[row, 12].Formula = $"SUM(L2:L{row - 1})";
      worksheet.Cells[row, 13].Formula = $"SUM(M2:M{row - 1})";
      worksheet.Cells[row, 14].Formula = $"SUM(N2:N{row - 1})";
      worksheet.Cells[row, 15].Formula = $"SUM(O2:O{row - 1})";
      worksheet.Cells[row, 16].Formula = $"SUM(P2:P{row - 1})";
      worksheet.Cells[row, 17].Formula = $"SUM(Q2:Q{row - 1})";
      worksheet.Cells[row, 18].Formula = $"SUM(R2:R{row - 1})";
      worksheet.Cells[row, 19].Formula = $"SUM(S2:S{row - 1})";
      worksheet.Cells[row, 20].Formula = $"SUM(T2:T{row - 1})";
      worksheet.Cells[row, 30].Formula = $"SUM(AD2:AD{row - 1})";

      for (int col = 10; col <= 20; col++)
      {
        worksheet.Column(col).Style.Numberformat.Format = "#,##0.00";
      }

      worksheet.Column(30).Style.Numberformat.Format = "#,##0";
      worksheet.Column(3).Style.Numberformat.Format = "yyyy-mm-dd";
      worksheet.Column(26).Style.Numberformat.Format = "yyyy-mm-dd";

      worksheet.Cells[1, 1, 1, headers.Length].Style.Font.Bold = true;
      worksheet.Cells.AutoFitColumns();

      return true;
    }

    private static bool IsIncludedComplemento(DeclaracionComplementoBase item) =>
      !string.Equals(item.D?.Trim(), "X", StringComparison.OrdinalIgnoreCase);

    private bool AddComplementosWorksheet(OfficeOpenXml.ExcelPackage package, string sheetName, IEnumerable<DeclaracionComplementoBase>? items)
    {
      var includedItems = items?
        .Where(IsIncludedComplemento)
        .ToList();

      if (includedItems == null || includedItems.Count == 0)
      {
        return false;
      }

      var worksheet = package.Workbook.Worksheets.Add(sheetName);
      string[] headers =
      {
        "Comprobante_ID", "Poliza", "Folio", "Incluido", "Polizas", "FechaPago", "Mes", "Año",
        "NumParcialidad", "ImpSaldoAnt", "ImpPagado", "ImpSaldoInsoluto", "Comp_Actos16", "Comp_IVA",
        "MontoPago", "ComprobanteUUID", "EmisorRfc", "ReceptorRfc", "Pago_Id", "FormaDePagoP", "MonedaP",
        "DoctoRelacionado_Id", "UUID_DoctoRelacionado", "MonedaDR", "XML_Attachment_ID", "EsEmitida", "EsRecibida"
      };

      for (int j = 0; j < headers.Length; j++)
      {
        worksheet.Cells[1, j + 1].Value = headers[j];
      }

      int row = 2;
      foreach (var item in includedItems)
      {
        worksheet.Cells[row, 1].Value = item.Comprobante_Id;
        worksheet.Cells[row, 2].Value = item.Poliza;
        worksheet.Cells[row, 3].Value = item.Folio;
        worksheet.Cells[row, 4].Value = item.D;
        worksheet.Cells[row, 5].Value = item.Polizas;
        worksheet.Cells[row, 6].Value = item.FechaPago;
        worksheet.Cells[row, 7].Value = item.MES_GLOBAL;
        worksheet.Cells[row, 8].Value = item.ANIO_GLOBAL;
        worksheet.Cells[row, 9].Value = item.NumParcialidad;
        worksheet.Cells[row, 10].Value = item.ImpSaldoAnt;
        worksheet.Cells[row, 11].Value = item.ImpPagado;
        worksheet.Cells[row, 12].Value = item.ImpSaldoInsoluto;
        worksheet.Cells[row, 13].Value = item.Comp_Actos16;
        worksheet.Cells[row, 14].Value = item.Comp_IVA;
        worksheet.Cells[row, 15].Value = item.MontoPago;
        worksheet.Cells[row, 16].Value = item.ComprobanteUUID;
        worksheet.Cells[row, 17].Value = item.EmisorRfc;
        worksheet.Cells[row, 18].Value = item.ReceptorRfc;
        worksheet.Cells[row, 19].Value = item.Pago_Id;
        worksheet.Cells[row, 20].Value = item.FormaDePagoP;
        worksheet.Cells[row, 21].Value = item.MonedaP;
        worksheet.Cells[row, 22].Value = item.DoctoRelacionado_Id;
        worksheet.Cells[row, 23].Value = item.UUID_DoctoRelacionado;
        worksheet.Cells[row, 24].Value = item.MonedaDR;
        worksheet.Cells[row, 25].Value = item.XML_Attachment_ID;
        worksheet.Cells[row, 26].Value = item.EsEmitida;
        worksheet.Cells[row, 27].Value = item.EsRecibida;
        row++;
      }

      worksheet.Cells[row, 1].Value = "Totals:";
      worksheet.Cells[row, 10].Formula = $"SUM(J2:J{row - 1})";
      worksheet.Cells[row, 11].Formula = $"SUM(K2:K{row - 1})";
      worksheet.Cells[row, 12].Formula = $"SUM(L2:L{row - 1})";
      worksheet.Cells[row, 13].Formula = $"SUM(M2:M{row - 1})";
      worksheet.Cells[row, 14].Formula = $"SUM(N2:N{row - 1})";
      worksheet.Cells[row, 15].Formula = $"SUM(O2:O{row - 1})";

      for (int col = 10; col <= 15; col++)
      {
        worksheet.Column(col).Style.Numberformat.Format = "#,##0.000000";
      }

      worksheet.Column(6).Style.Numberformat.Format = "yyyy-mm-dd";
      worksheet.Cells[1, 1, 1, headers.Length].Style.Font.Bold = true;
      worksheet.Cells.AutoFitColumns();

      return true;
    }
  }
}
