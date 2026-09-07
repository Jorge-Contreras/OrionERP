using System.Globalization;
using Microsoft.JSInterop;
using OfficeOpenXml;
using OrionERP.Application.Features.ReportesFinancieros.Models;

namespace OrionERP.Web.Features.ReportesFinancieros.DeclaracionMensual;

public partial class DeclaracionMensualPage
{
  private const string FormatoMoneda = "#,##0.00";

  /// <summary>
  /// Una hoja por pestaña. Sirve para adjuntar el respaldo del mes al
  /// expediente de la declaración, que es como hoy se archiva el papel de
  /// trabajo en la carpeta de Finanzas.
  /// </summary>
  private async Task ExportarExcelAsync()
  {
    if (Reporte is null)
    {
      return;
    }

    IsWorking = true;
    await InvokeAsync(StateHasChanged);

    try
    {
      using var paquete = new ExcelPackage();

      EscribirRenglones(paquete, "ISR", Reporte.Isr);
      EscribirRenglones(paquete, "IVA", Reporte.Iva);
      EscribirConciliacion(paquete);
      EscribirHallazgos(paquete);
      EscribirEjercicio(paquete);

      var bytes = await paquete.GetAsByteArrayAsync();
      var dataUrl = "data:application/vnd.openxmlformats-officedocument.spreadsheetml.sheet;base64,"
                  + Convert.ToBase64String(bytes);

      await JS.InvokeVoidAsync("triggerFileDownload",
        $"Declaracion_{Rfc}_{Anio}_{Mes:00}.xlsx", dataUrl);
    }
    catch (Exception ex)
    {
      ErrorMessage = ex.Message;
    }
    finally
    {
      IsWorking = false;
      await InvokeAsync(StateHasChanged);
    }
  }

  private static void EscribirRenglones(
    ExcelPackage paquete, string hoja, IReadOnlyList<DeclaracionRenglonRow> filas)
  {
    var ws = paquete.Workbook.Worksheets.Add(hoja);
    var encabezados = new[] { "Concepto", "SAT", "Nuestro", "Declarado", "Dif. SAT", "Estado" };

    for (var c = 0; c < encabezados.Length; c++)
    {
      ws.Cells[1, c + 1].Value = encabezados[c];
      ws.Cells[1, c + 1].Style.Font.Bold = true;
    }

    var fila = 2;
    foreach (var renglon in filas)
    {
      ws.Cells[fila, 1].Value = renglon.Concepto;
      ws.Cells[fila, 2].Value = renglon.ValorSat;
      ws.Cells[fila, 3].Value = renglon.ValorNuestro;
      ws.Cells[fila, 4].Value = renglon.ValorDeclarado;
      ws.Cells[fila, 5].Value = renglon.DifSatNuestro;
      ws.Cells[fila, 6].Value = renglon.Estado;

      var formato = renglon.EsTasa ? "0.0000" : FormatoMoneda;
      ws.Cells[fila, 2, fila, 5].Style.Numberformat.Format = formato;

      if (renglon.EsTotal)
      {
        ws.Cells[fila, 1, fila, 6].Style.Font.Bold = true;
      }

      fila++;
    }

    ws.Cells.AutoFitColumns();
  }

  private void EscribirConciliacion(ExcelPackage paquete)
  {
    var ws = paquete.Workbook.Worksheets.Add("Conciliacion");
    var encabezados = new[] { "Concepto", "Cuenta", "CFDI", "Contabilidad", "Declarado", "Diferencia", "Estado" };

    for (var c = 0; c < encabezados.Length; c++)
    {
      ws.Cells[1, c + 1].Value = encabezados[c];
      ws.Cells[1, c + 1].Style.Font.Bold = true;
    }

    var fila = 2;
    foreach (var renglon in Reporte!.Conciliacion)
    {
      ws.Cells[fila, 1].Value = renglon.Concepto;
      ws.Cells[fila, 2].Value = renglon.Cuenta;
      ws.Cells[fila, 3].Value = renglon.ValorCfdi;
      ws.Cells[fila, 4].Value = renglon.ValorContable;
      ws.Cells[fila, 5].Value = renglon.ValorDeclarado;
      ws.Cells[fila, 6].Value = renglon.DifCfdiContable;
      ws.Cells[fila, 7].Value = renglon.NoComparable ? "Informativo" : renglon.Estado;
      ws.Cells[fila, 3, fila, 6].Style.Numberformat.Format = FormatoMoneda;
      fila++;
    }

    fila += 1;
    ws.Cells[fila, 1].Value = "Retenciones";
    ws.Cells[fila, 1].Style.Font.Bold = true;
    fila++;

    foreach (var renglon in Reporte.Retenciones)
    {
      ws.Cells[fila, 1].Value = renglon.Concepto;
      ws.Cells[fila, 2].Value = renglon.Cuenta;
      ws.Cells[fila, 3].Value = renglon.ValorCfdi;
      ws.Cells[fila, 4].Value = renglon.ValorContable;
      ws.Cells[fila, 6].Value = renglon.DifCfdiContable;
      ws.Cells[fila, 7].Value = renglon.Estado;
      ws.Cells[fila, 3, fila, 6].Style.Numberformat.Format = FormatoMoneda;
      fila++;
    }

    ws.Cells.AutoFitColumns();
  }

  private void EscribirHallazgos(ExcelPackage paquete)
  {
    var ws = paquete.Workbook.Worksheets.Add("Hallazgos");
    var encabezados = new[] { "Severidad", "Tipo", "Detalle", "Contraparte", "Fecha", "Monto", "UUID" };

    for (var c = 0; c < encabezados.Length; c++)
    {
      ws.Cells[1, c + 1].Value = encabezados[c];
      ws.Cells[1, c + 1].Style.Font.Bold = true;
    }

    var fila = 2;
    foreach (var renglon in Reporte!.Hallazgos)
    {
      ws.Cells[fila, 1].Value = renglon.Severidad;
      ws.Cells[fila, 2].Value = renglon.Tipo;
      ws.Cells[fila, 3].Value = renglon.Descripcion;
      ws.Cells[fila, 4].Value = renglon.Contraparte;
      ws.Cells[fila, 5].Value = renglon.Fecha?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
      ws.Cells[fila, 6].Value = renglon.Monto;
      ws.Cells[fila, 7].Value = renglon.FolioFiscal;
      ws.Cells[fila, 6].Style.Numberformat.Format = FormatoMoneda;
      fila++;
    }

    ws.Cells.AutoFitColumns();
  }

  private void EscribirEjercicio(ExcelPackage paquete)
  {
    var ws = paquete.Workbook.Worksheets.Add($"Ejercicio {Anio}");
    var encabezados = new[]
    {
      "Mes", "Ingresos", "Ingresos declarado", "Diferencia", "Acumulado", "Coeficiente",
      "ISR a cargo", "ISR declarado", "IVA a cargo", "IVA acreditable", "Saldo a favor",
      "Declaracion", "Operacion", "Requiere complementaria"
    };

    for (var c = 0; c < encabezados.Length; c++)
    {
      ws.Cells[1, c + 1].Value = encabezados[c];
      ws.Cells[1, c + 1].Style.Font.Bold = true;
    }

    var fila = 2;
    foreach (var renglon in Ejercicio)
    {
      ws.Cells[fila, 1].Value = renglon.NombreMes;
      ws.Cells[fila, 2].Value = renglon.Ingresos;
      ws.Cells[fila, 3].Value = renglon.IngresosDeclarado;
      ws.Cells[fila, 4].Value = renglon.DifIngresos;
      ws.Cells[fila, 5].Value = renglon.IngresosAcum;
      ws.Cells[fila, 6].Value = renglon.Coeficiente;
      ws.Cells[fila, 7].Value = renglon.IsrACargo;
      ws.Cells[fila, 8].Value = renglon.IsrACargoDeclarado;
      ws.Cells[fila, 9].Value = renglon.IvaACargo;
      ws.Cells[fila, 10].Value = renglon.IvaAcreditable;
      ws.Cells[fila, 11].Value = renglon.SaldoAFavor;
      ws.Cells[fila, 12].Value = renglon.TipoDeclaracion == "C" ? "Complementaria"
        : renglon.TieneDeclaracion ? "Normal" : "Sin presentar";
      ws.Cells[fila, 13].Value = renglon.NumeroOperacion;
      ws.Cells[fila, 14].Value = renglon.RequiereComplementaria ? "Si" : "No";

      ws.Cells[fila, 2, fila, 5].Style.Numberformat.Format = FormatoMoneda;
      ws.Cells[fila, 6].Style.Numberformat.Format = "0.0000";
      ws.Cells[fila, 7, fila, 11].Style.Numberformat.Format = FormatoMoneda;
      fila++;
    }

    ws.Cells.AutoFitColumns();
  }
}
