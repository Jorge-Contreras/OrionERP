using OfficeOpenXml;
using System;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace OrionERP.Web.Features.ReportesFinancieros.HojaTrabajo
{
    public partial class HojaTrabajoPage
    {
        private async Task ExportToExcel(IJSRuntime js)
        {
            if (IsExporting)
                return;

            IsExporting = true;

            try
            {
                using var package = new ExcelPackage();
                var worksheet = package.Workbook.Worksheets.Add("Hoja de Trabajo");

                // Headers
                string[] headers = { "Descripción", "ENERO", "FEBRERO", "MARZO", "ABRIL", "MAYO", "JUNIO", "JULIO", "AGOSTO", "SEPTIEMBRE", "OCTUBRE", "NOVIEMBRE", "DICIEMBRE" };
                for (int i = 0; i < headers.Length; i++)
                {
                    worksheet.Cells[1, i + 1].Value = headers[i];
                }

                // Data
                for (int i = 0; i < HojaTrabajoData.Count; i++)
                {
                    var row = HojaTrabajoData[i];
                    worksheet.Cells[i + 2, 1].Value = row.Descripcion;
                    worksheet.Cells[i + 2, 2].Value = row.ENERO;
                    worksheet.Cells[i + 2, 3].Value = row.FEBRERO;
                    worksheet.Cells[i + 2, 4].Value = row.MARZO;
                    worksheet.Cells[i + 2, 5].Value = row.ABRIL;
                    worksheet.Cells[i + 2, 6].Value = row.MAYO;
                    worksheet.Cells[i + 2, 7].Value = row.JUNIO;
                    worksheet.Cells[i + 2, 8].Value = row.JULIO;
                    worksheet.Cells[i + 2, 9].Value = row.AGOSTO;
                    worksheet.Cells[i + 2, 10].Value = row.SEPTIEMBRE;
                    worksheet.Cells[i + 2, 11].Value = row.OCTUBRE;
                    worksheet.Cells[i + 2, 12].Value = row.NOVIEMBRE;
                    worksheet.Cells[i + 2, 13].Value = row.DICIEMBRE;
                }

                worksheet.Cells.AutoFitColumns();

                var fileBytes = await package.GetAsByteArrayAsync();
                var fileName = $"HojaTrabajoIVA_{Anio}_{CurrentRfc}.xlsx";
                var base64 = Convert.ToBase64String(fileBytes);
                var dataUrl = $"data:application/vnd.openxmlformats-officedocument.spreadsheetml.sheet;base64,{base64}";

                await js.InvokeVoidAsync("triggerFileDownload", fileName, dataUrl);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Error al exportar a Excel: {ex.Message}";
            }
            finally
            {
                IsExporting = false;
            }
        }
    }
}
