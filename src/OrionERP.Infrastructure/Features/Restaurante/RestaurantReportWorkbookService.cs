using ClosedXML.Excel;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

public sealed class RestaurantReportWorkbookService : IRestaurantReportWorkbookService
{
  private static readonly XLColor DarkGreen = XLColor.FromHtml("#173D32");
  private static readonly XLColor MediumGreen = XLColor.FromHtml("#26745F");
  private static readonly XLColor LightGreen = XLColor.FromHtml("#E8F5EF");
  private static readonly XLColor LightGray = XLColor.FromHtml("#EEF2F1");
  private static readonly XLColor LightRed = XLColor.FromHtml("#FCE7E5");
  private static readonly XLColor LightOrange = XLColor.FromHtml("#FDE8D2");
  private static readonly XLColor LightYellow = XLColor.FromHtml("#FFF4D8");
  private static readonly XLColor TextGray = XLColor.FromHtml("#53615C");

  private const string Money = "$#,##0.00";
  private const string Percent = "0.0\"%\"";

  public RestaurantReportWorkbook CreateAccountingWorkbook(
    RestaurantAccountingReportDto report,
    IReadOnlyList<RestaurantRecipeCostDto> recipeCosts,
    RestaurantDiagnosticRunDto? diagnostic,
    string rfc,
    string siteName)
  {
    ArgumentNullException.ThrowIfNull(report);

    using var workbook = new XLWorkbook();
    BuildSummarySheet(workbook.Worksheets.Add("Resumen"), report, rfc, siteName);
    BuildPnlSheet(workbook.Worksheets.Add("Estado de resultados"), report);
    BuildReconciliationSheet(workbook.Worksheets.Add("Conciliación"), report);
    BuildAgrupadorSheet(workbook.Worksheets.Add("Agrupadores"), report);
    BuildRecipeSheet(workbook.Worksheets.Add("Costo por receta"), recipeCosts);
    if (diagnostic is not null) BuildDiagnosticSheet(workbook.Worksheets.Add("Diagnóstico"), diagnostic);

    foreach (var sheet in workbook.Worksheets)
    {
      sheet.ShowGridLines = false;
      sheet.Style.Font.FontName = "Aptos";
      sheet.Style.Font.FontSize = 10;
    }

    using var stream = new MemoryStream();
    workbook.SaveAs(stream);
    return new RestaurantReportWorkbook
    {
      FileName = $"reportes-restaurante-{report.Summary.From:yyyyMMdd}-{report.Summary.To:yyyyMMdd}.xlsx",
      Content = stream.ToArray()
    };
  }

  private static void BuildSummarySheet(
    IXLWorksheet sheet,
    RestaurantAccountingReportDto report,
    string rfc,
    string siteName)
  {
    Title(sheet, "Reportes de Restaurante por agrupador SAT");
    sheet.Cell(2, 1).Value = $"{rfc} · {siteName} · {report.Summary.From:dd MMM yyyy} al {report.Summary.To:dd MMM yyyy}";
    sheet.Cell(2, 1).Style.Font.FontColor = TextGray;

    var row = 4;
    SectionHeader(sheet, row++, "Operación del punto de venta");
    row = KeyValue(sheet, row, "Órdenes pagadas", report.Summary.OrdenesPagadas);
    row = KeyValue(sheet, row, "Venta antes de impuesto", report.Summary.VentaNetaPos, Money);
    row = KeyValue(sheet, row, "IVA trasladado cobrado", report.Summary.IvaTrasladadoPos, Money);
    row = KeyValue(sheet, row, "Descuentos y promociones", report.Summary.DescuentosPos, Money);
    row = KeyValue(sheet, row, "Ticket promedio", report.Summary.TicketPromedio, Money);
    row = KeyValue(sheet, row, "Costo recalculado desde receta", report.Summary.CostoRecalculado, Money);
    row = KeyValue(sheet, row, "Margen bruto", report.Summary.MargenBruto, Money);
    row = KeyValue(sheet, row, "Costo de alimentos", report.Summary.FoodCostPorcentaje, Percent);

    row++;
    SectionHeader(sheet, row++, "Contabilidad del periodo");
    row = KeyValue(sheet, row, $"Ingreso ({Codes(report.Summary.AgrupadoresIngreso)})", report.Summary.IngresoContable, Money);
    row = KeyValue(sheet, row, "Costo y gasto registrado", report.Summary.GastoContable, Money);
    row = KeyValue(sheet, row, "Resultado del periodo", report.Summary.ResultadoContable, Money);
    row = KeyValue(sheet, row, "Cargos totales", report.Pnl.CargosTotales, Money);
    row = KeyValue(sheet, row, "Abonos totales", report.Pnl.AbonosTotales, Money);
    row = KeyValue(sheet, row, "Balanza cuadrada", report.Pnl.Cuadrada ? "Sí" : "No");

    row++;
    SectionHeader(sheet, row++, "Trazabilidad");
    row = KeyValue(sheet, row, "Órdenes ligadas a póliza", report.Reconciliation.OrdenesLigadas);
    row = KeyValue(sheet, row, "Órdenes sin ligar", report.Reconciliation.OrdenesSinLigar);
    row = KeyValue(sheet, row, "Días con venta", report.Reconciliation.DiasConVenta);
    row = KeyValue(sheet, row, "Diferencia de caja neta", report.Reconciliation.DiferenciaCajaNeta, Money);
    row = KeyValue(sheet, row, "Turnos sin aprobar", report.Reconciliation.TurnosSinAprobar);

    if (report.Map.FueraDelMapeo.Count > 0)
    {
      row++;
      SectionHeader(sheet, row++, "Agrupadores con movimiento fuera del mapeo");
      foreach (var agrupador in report.Map.FueraDelMapeo)
      {
        sheet.Cell(row, 1).Value = $"{agrupador.Nivel1} · {agrupador.Descripcion}";
        sheet.Cell(row, 2).Value = agrupador.Cargos + agrupador.Abonos;
        sheet.Cell(row, 2).Style.NumberFormat.Format = Money;
        sheet.Range(row, 1, row, 2).Style.Fill.BackgroundColor = LightYellow;
        row++;
      }
    }

    sheet.Column(1).Width = 44;
    sheet.Column(2).Width = 20;
  }

  private static void BuildPnlSheet(IXLWorksheet sheet, RestaurantAccountingReportDto report)
  {
    Title(sheet, "Estado de resultados por agrupador SAT");
    var headers = new[] { "Concepto", "Agrupadores nivel 1", "Periodo", "Periodo anterior", "Acumulado del ejercicio", "% sobre venta", "Movimientos" };
    HeaderRow(sheet, 3, headers);

    var row = 4;
    foreach (var line in report.Pnl.Rows)
    {
      sheet.Cell(row, 1).Value = line.Etiqueta;
      sheet.Cell(row, 2).Value = Codes(line.Agrupadores);
      sheet.Cell(row, 3).Value = line.Periodo;
      sheet.Cell(row, 4).Value = line.PeriodoAnterior;
      sheet.Cell(row, 5).Value = line.Acumulado;
      sheet.Cell(row, 6).Value = line.PorcentajeSobreVenta;
      sheet.Cell(row, 7).Value = line.Movimientos;
      sheet.Range(row, 3, row, 5).Style.NumberFormat.Format = Money;
      sheet.Cell(row, 6).Style.NumberFormat.Format = Percent;
      if (line.Movimientos == 0) sheet.Range(row, 1, row, 7).Style.Font.FontColor = TextGray;
      row++;
    }

    row++;
    Total(sheet, row++, "Ingresos", report.Pnl.Ingresos);
    Total(sheet, row++, "Costo", -report.Pnl.Costo);
    Total(sheet, row++, "Margen bruto", report.Pnl.MargenBruto);
    Total(sheet, row++, "Gastos de operación", -report.Pnl.Gastos);
    Total(sheet, row, "Resultado del periodo", report.Pnl.Resultado);

    sheet.Column(1).Width = 32;
    sheet.Column(2).Width = 26;
    for (var column = 3; column <= 7; column++) sheet.Column(column).Width = 18;
    sheet.SheetView.FreezeRows(3);
  }

  private static void BuildReconciliationSheet(IXLWorksheet sheet, RestaurantAccountingReportDto report)
  {
    Title(sheet, "Conciliación entre la operación y los libros");
    HeaderRow(sheet, 3, ["Concepto", "Punto de venta", "Contabilidad", "Diferencia", "Agrupadores", "Estado", "Nota"]);

    var row = 4;
    foreach (var line in report.Reconciliation.Rows)
    {
      sheet.Cell(row, 1).Value = line.Concepto;
      sheet.Cell(row, 2).Value = line.Operacion;
      sheet.Cell(row, 3).Value = line.Contabilidad;
      sheet.Cell(row, 4).Value = line.Diferencia;
      sheet.Cell(row, 5).Value = Codes(line.Agrupadores);
      sheet.Cell(row, 6).Value = line.NoComparable ? "No comparable" : line.Conciliado ? "Conciliado" : "Con diferencia";
      sheet.Cell(row, 7).Value = line.AgrupadoresSinMovimiento ? "El agrupador no tiene movimientos en el periodo." : line.Detalle;
      sheet.Range(row, 2, row, 4).Style.NumberFormat.Format = Money;
      if (!line.NoComparable && !line.Conciliado)
        sheet.Range(row, 1, row, 7).Style.Fill.BackgroundColor = line.AgrupadoresSinMovimiento ? LightRed : LightOrange;
      row++;
    }

    row += 2;
    SectionHeader(sheet, row++, "Turnos de caja");
    row = KeyValue(sheet, row, "Diferencia neta", report.Reconciliation.DiferenciaCajaNeta, Money);
    row = KeyValue(sheet, row, "Diferencia absoluta", report.Reconciliation.DiferenciaCajaAbsoluta, Money);
    row = KeyValue(sheet, row, "Turnos con diferencia", report.Reconciliation.TurnosConDiferencia);
    KeyValue(sheet, row, "Turnos sin aprobar", report.Reconciliation.TurnosSinAprobar);

    sheet.Column(1).Width = 32;
    for (var column = 2; column <= 5; column++) sheet.Column(column).Width = 18;
    sheet.Column(6).Width = 16;
    sheet.Column(7).Width = 60;
  }

  private static void BuildAgrupadorSheet(IXLWorksheet sheet, RestaurantAccountingReportDto report)
  {
    Title(sheet, "Agrupadores nivel 1 con movimiento");
    HeaderRow(sheet, 3, ["Nivel 1", "Descripción SAT", "Cargos", "Abonos", "Saldo", "Movimientos", "En el mapeo"]);

    var row = 4;
    foreach (var agrupador in report.Agrupadores)
    {
      sheet.Cell(row, 1).Value = agrupador.Nivel1;
      sheet.Cell(row, 2).Value = agrupador.Descripcion;
      sheet.Cell(row, 3).Value = agrupador.Cargos;
      sheet.Cell(row, 4).Value = agrupador.Abonos;
      sheet.Cell(row, 5).Value = agrupador.Saldo;
      sheet.Cell(row, 6).Value = agrupador.Movimientos;
      sheet.Cell(row, 7).Value = agrupador.Incluido ? "Sí" : "No";
      sheet.Range(row, 3, row, 5).Style.NumberFormat.Format = Money;
      if (!agrupador.Incluido) sheet.Range(row, 1, row, 7).Style.Fill.BackgroundColor = LightYellow;
      row++;
    }

    row += 2;
    SectionHeader(sheet, row++, "Mapeo de conceptos");
    HeaderRow(sheet, row++, ["Concepto", "Agrupadores incluidos", "Signo"]);
    foreach (var concepto in report.Map.Conceptos)
    {
      sheet.Cell(row, 1).Value = concepto.Etiqueta;
      sheet.Cell(row, 2).Value = Codes(concepto.CodigosIncluidos);
      sheet.Cell(row, 3).Value = concepto.Signo > 0 ? "Suma" : "Resta";
      row++;
    }

    sheet.Column(1).Width = 32;
    sheet.Column(2).Width = 46;
    for (var column = 3; column <= 7; column++) sheet.Column(column).Width = 16;
    sheet.SheetView.FreezeRows(3);
  }

  private static void BuildRecipeSheet(IXLWorksheet sheet, IReadOnlyList<RestaurantRecipeCostDto> recipeCosts)
  {
    Title(sheet, "Costo por receta recalculado");
    sheet.Cell(2, 1).Value = "El costo se recalcula desde la receta activa con los precios de hoy; el costo guardado se muestra sólo para comparar.";
    sheet.Cell(2, 1).Style.Font.FontColor = TextGray;
    HeaderRow(sheet, 3, ["Producto", "Unidades vendidas", "Venta", "Precio de lista", "Costo recalculado", "Costo guardado", "Deriva", "Costo de lo vendido", "% costo", "Rendimiento", "Componentes sin conversión"]);

    var row = 4;
    foreach (var cost in recipeCosts)
    {
      sheet.Cell(row, 1).Value = cost.Producto;
      sheet.Cell(row, 2).Value = cost.UnidadesVendidas;
      sheet.Cell(row, 3).Value = cost.Venta;
      sheet.Cell(row, 4).Value = cost.PrecioLista;
      sheet.Cell(row, 5).Value = cost.CostoRecalculado;
      sheet.Cell(row, 6).Value = cost.CostoCongelado;
      sheet.Cell(row, 7).Value = cost.Deriva;
      sheet.Cell(row, 8).Value = cost.CostoVendido;
      sheet.Cell(row, 9).Value = cost.FoodCostPorcentaje;
      sheet.Cell(row, 10).Value = cost.TieneReceta
        ? $"{cost.RendimientoReceta:0.##} {cost.UnidadRendimiento}".Trim()
        : "Sin receta";
      sheet.Cell(row, 11).Value = cost.ComponentesSinConversion;
      sheet.Range(row, 3, row, 8).Style.NumberFormat.Format = Money;
      sheet.Cell(row, 9).Style.NumberFormat.Format = Percent;

      if (!cost.TieneReceta || cost.CostoRecalculado <= 0.01m)
        sheet.Range(row, 1, row, 11).Style.Fill.BackgroundColor = LightRed;
      else if (cost.FoodCostPorcentaje > 50)
        sheet.Range(row, 1, row, 11).Style.Fill.BackgroundColor = LightOrange;
      else if (cost.FoodCostPorcentaje <= 35)
        sheet.Range(row, 1, row, 11).Style.Fill.BackgroundColor = LightGreen;
      row++;
    }

    sheet.Column(1).Width = 42;
    for (var column = 2; column <= 11; column++) sheet.Column(column).Width = 17;
    sheet.SheetView.FreezeRows(3);
  }

  private static void BuildDiagnosticSheet(IXLWorksheet sheet, RestaurantDiagnosticRunDto run)
  {
    Title(sheet, "Diagnóstico contable y fiscal");
    sheet.Cell(2, 1).Value = $"Corrida del {run.EjecutadoEn:dd MMM yyyy HH:mm} por {run.EjecutadoPor} · {run.HallazgosTotal} hallazgo(s) · {run.MontoExpuesto:C} expuestos";
    sheet.Cell(2, 1).Style.Font.FontColor = TextGray;
    HeaderRow(sheet, 3, ["Regla", "Severidad", "Hallazgo", "Detalle", "Agrupadores", "Monto expuesto", "Conteo", "Acción sugerida", "Estado"]);

    var row = 4;
    foreach (var finding in run.Findings)
    {
      sheet.Cell(row, 1).Value = finding.ReglaClave;
      sheet.Cell(row, 2).Value = finding.Severidad;
      sheet.Cell(row, 3).Value = finding.Titulo;
      sheet.Cell(row, 4).Value = finding.Detalle;
      sheet.Cell(row, 5).Value = finding.Agrupadores ?? string.Empty;
      sheet.Cell(row, 6).Value = finding.MontoExpuesto;
      sheet.Cell(row, 7).Value = finding.Conteo;
      sheet.Cell(row, 8).Value = finding.AccionSugerida ?? string.Empty;
      sheet.Cell(row, 9).Value = finding.Estado;
      sheet.Cell(row, 6).Style.NumberFormat.Format = Money;
      sheet.Cell(row, 4).Style.Alignment.WrapText = true;
      sheet.Cell(row, 8).Style.Alignment.WrapText = true;
      sheet.Range(row, 1, row, 9).Style.Fill.BackgroundColor = finding.Severidad switch
      {
        RestaurantDiagnosticSeverities.Critica => LightRed,
        RestaurantDiagnosticSeverities.Alta => LightOrange,
        RestaurantDiagnosticSeverities.Media => LightYellow,
        RestaurantDiagnosticSeverities.Informativa => LightGreen,
        _ => LightGray
      };
      row++;
    }

    sheet.Column(1).Width = 8;
    sheet.Column(2).Width = 13;
    sheet.Column(3).Width = 44;
    sheet.Column(4).Width = 80;
    sheet.Column(5).Width = 20;
    sheet.Column(6).Width = 18;
    sheet.Column(7).Width = 10;
    sheet.Column(8).Width = 60;
    sheet.Column(9).Width = 12;
    sheet.SheetView.FreezeRows(3);
  }

  // ------------------------------------------------------------------
  // Utilidades de formato
  // ------------------------------------------------------------------

  private static void Title(IXLWorksheet sheet, string text)
  {
    var cell = sheet.Cell(1, 1);
    cell.Value = text;
    cell.Style.Font.Bold = true;
    cell.Style.Font.FontSize = 15;
    cell.Style.Font.FontColor = DarkGreen;
  }

  private static void SectionHeader(IXLWorksheet sheet, int row, string text)
  {
    var cell = sheet.Cell(row, 1);
    cell.Value = text;
    cell.Style.Font.Bold = true;
    cell.Style.Font.FontColor = MediumGreen;
  }

  private static void HeaderRow(IXLWorksheet sheet, int row, string[] headers)
  {
    for (var index = 0; index < headers.Length; index++)
    {
      var cell = sheet.Cell(row, index + 1);
      cell.Value = headers[index];
      cell.Style.Font.Bold = true;
      cell.Style.Font.FontColor = XLColor.White;
      cell.Style.Fill.BackgroundColor = DarkGreen;
      cell.Style.Alignment.WrapText = true;
    }
  }

  private static int KeyValue(IXLWorksheet sheet, int row, string label, object value, string? format = null)
  {
    sheet.Cell(row, 1).Value = label;
    sheet.Cell(row, 2).Value = XLCellValue.FromObject(value);
    if (format is not null) sheet.Cell(row, 2).Style.NumberFormat.Format = format;
    sheet.Range(row, 1, row, 2).Style.Fill.BackgroundColor = LightGray;
    return row + 1;
  }

  private static void Total(IXLWorksheet sheet, int row, string label, decimal value)
  {
    sheet.Cell(row, 1).Value = label;
    sheet.Cell(row, 3).Value = value;
    sheet.Cell(row, 3).Style.NumberFormat.Format = Money;
    sheet.Range(row, 1, row, 7).Style.Font.Bold = true;
    sheet.Range(row, 1, row, 7).Style.Fill.BackgroundColor = LightGreen;
  }

  private static string Codes(IReadOnlyList<string> codes) => codes.Count == 0 ? "—" : string.Join(" · ", codes);
}
