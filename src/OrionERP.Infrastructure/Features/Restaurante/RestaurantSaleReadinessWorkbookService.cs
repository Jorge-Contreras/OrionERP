using System.Globalization;
using ClosedXML.Excel;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

public sealed class RestaurantSaleReadinessWorkbookService : IRestaurantSaleReadinessWorkbookService
{
  private static readonly XLColor DarkGreen = XLColor.FromHtml("#173D32");
  private static readonly XLColor MediumGreen = XLColor.FromHtml("#26745F");
  private static readonly XLColor LightGreen = XLColor.FromHtml("#E8F5EF");
  private static readonly XLColor LightYellow = XLColor.FromHtml("#FFF4D8");
  private static readonly XLColor LightOrange = XLColor.FromHtml("#FDE8D2");
  private static readonly XLColor LightRed = XLColor.FromHtml("#FCE7E5");
  private static readonly XLColor LightGray = XLColor.FromHtml("#EEF2F1");
  private static readonly XLColor TextGray = XLColor.FromHtml("#53615C");

  public RestaurantSaleReadinessWorkbook Create(RestaurantSaleReadinessReport report)
  {
    ArgumentNullException.ThrowIfNull(report);
    using var workbook = new XLWorkbook();
    var summary = workbook.Worksheets.Add("Resumen");
    var products = workbook.Worksheets.Add("Productos");
    var ingredients = workbook.Worksheets.Add("Ingredientes");
    var bom = workbook.Worksheets.Add("BOM y conversiones");
    var modifiers = workbook.Worksheets.Add("Modificadores");
    var environment = workbook.Worksheets.Add("Entorno POS");

    BuildProductsSheet(products, report);
    BuildIngredientsSheet(ingredients, report);
    BuildBomSheet(bom, report);
    BuildModifiersSheet(modifiers, report);
    BuildEnvironmentSheet(environment, report);
    BuildSummarySheet(summary, report);

    foreach (var sheet in workbook.Worksheets)
    {
      sheet.ShowGridLines = false;
      sheet.Style.Font.FontName = "Aptos";
      sheet.Style.Font.FontSize = 10;
    }
    summary.Position = 1;
    products.Position = 2;
    ingredients.Position = 3;
    bom.Position = 4;
    modifiers.Position = 5;
    environment.Position = 6;

    using var stream = new MemoryStream();
    workbook.SaveAs(stream);
    return new RestaurantSaleReadinessWorkbook
    {
      FileName = $"diagnostico-venta-{SanitizeFileSegment(report.Rfc)}-{SanitizeFileSegment(report.SiteCode)}-{report.GeneratedAtLocal:yyyyMMdd-HHmm}.xlsx",
      Bytes = stream.ToArray()
    };
  }

  private static void BuildSummarySheet(IXLWorksheet sheet, RestaurantSaleReadinessReport report)
  {
    sheet.SheetView.FreezeRows(1);
    sheet.TabColor = MediumGreen;
    sheet.Range("A1:H1").Merge();
    sheet.Cell("A1").Value = "Diagnóstico preventivo de venta · POS Restaurante";
    StyleTitle(sheet.Range("A1:H1"));

    var metadata = new object?[][]
    {
      ["RFC", report.Rfc, "Sede", $"{report.SiteName} ({report.SiteCode})"],
      ["Menú evaluado", report.MenuName, "Origen", report.UsesFallbackCatalog ? "Catálogo activo de respaldo" : "Menú publicado vigente"],
      ["Generado (hora local)", report.GeneratedAtLocal.DateTime, "Zona horaria", report.SiteTimeZoneId],
      ["Generado (UTC)", report.GeneratedAtUtc.UtcDateTime, "Cantidad simulada", report.SimulatedQuantity],
      ["Déficit con supervisor", report.AllowSupervisorDeficit, "Naturaleza", "Instantánea de solo lectura; no reserva inventario"]
    };
    for (var row = 0; row < metadata.Length; row++)
    {
      SetCellValue(sheet.Cell(row + 3, 1), metadata[row][0]);
      SetCellValue(sheet.Cell(row + 3, 2), metadata[row][1]);
      SetCellValue(sheet.Cell(row + 3, 4), metadata[row][2]);
      SetCellValue(sheet.Cell(row + 3, 5), metadata[row][3]);
    }
    foreach (var labelCell in new[] { "A3", "A4", "A5", "A6", "A7", "D3", "D4", "D5", "D6", "D7" })
    {
      sheet.Cell(labelCell).Style.Font.Bold = true;
      sheet.Cell(labelCell).Style.Font.FontColor = TextGray;
    }
    sheet.Range("A3:B7").Style.Fill.BackgroundColor = XLColor.White;
    sheet.Range("D3:E7").Style.Fill.BackgroundColor = XLColor.White;
    sheet.Range("A3:B7").Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
    sheet.Range("A3:B7").Style.Border.OutsideBorderColor = XLColor.FromHtml("#D5DFDC");
    sheet.Range("D3:E7").Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
    sheet.Range("D3:E7").Style.Border.OutsideBorderColor = XLColor.FromHtml("#D5DFDC");
    sheet.Cell("B5").Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
    sheet.Cell("B6").Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
    sheet.Cell("E6").Style.NumberFormat.Format = "0.0000";

    sheet.Range("A9:H9").Merge();
    sheet.Cell("A9").Value = "Semáforo del menú";
    StyleSectionHeader(sheet.Range("A9:H9"));
    var productLastRow = Math.Max(5, report.Products.Count + 4);
    var metrics = new[]
    {
      ("Productos evaluados", $"=COUNTA('Productos'!$F$5:$F${productLastRow})", LightGray),
      ("Listos", $"=COUNTIF('Productos'!$A$5:$A${productLastRow},\"{RestaurantSaleReadinessStatuses.Ready}\")", LightGreen),
      ("Advertencias", $"=COUNTIF('Productos'!$A$5:$A${productLastRow},\"{RestaurantSaleReadinessStatuses.Warning}\")", LightYellow),
      ("Requieren supervisor", $"=COUNTIF('Productos'!$A$5:$A${productLastRow},\"{RestaurantSaleReadinessStatuses.SupervisorRequired}\")", LightOrange),
      ("Bloqueados", $"=COUNTIF('Productos'!$A$5:$A${productLastRow},\"BLOQUEADO*\")", LightRed),
      ("Agotados", $"=COUNTIF('Productos'!$A$5:$A${productLastRow},\"{RestaurantSaleReadinessStatuses.SoldOut}\")", LightGray)
    };
    for (var index = 0; index < metrics.Length; index++)
    {
      var column = index + 1;
      sheet.Cell(10, column).Value = metrics[index].Item1;
      sheet.Cell(11, column).FormulaA1 = metrics[index].Item2;
      sheet.Range(10, column, 11, column).Style.Fill.BackgroundColor = metrics[index].Item3;
      sheet.Range(10, column, 11, column).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
      sheet.Range(10, column, 11, column).Style.Border.OutsideBorderColor = XLColor.FromHtml("#D5DFDC");
      sheet.Cell(10, column).Style.Font.Bold = true;
      sheet.Cell(10, column).Style.Font.FontColor = TextGray;
      sheet.Cell(10, column).Style.Alignment.WrapText = true;
      sheet.Cell(11, column).Style.Font.Bold = true;
      sheet.Cell(11, column).Style.Font.FontSize = 18;
      sheet.Cell(11, column).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    }

    sheet.Range("A13:H13").Merge();
    sheet.Cell("A13").Value = "Acciones prioritarias";
    StyleSectionHeader(sheet.Range("A13:H13"));
    var headers = new[] { "Severidad", "SKU", "Producto", "Material ID", "Material", "Problema", "Faltante", "Acción recomendada" };
    var actionRows = report.Actions.Select(action => new object?[]
    {
      action.Severity, action.ProductSku, action.ProductName, action.MaterialId, action.Material,
      action.Issue, action.ShortageQuantity, action.RecommendedAction
    }).ToList();
    WriteTable(sheet, 14, headers, actionRows, "ReadinessActions", 1);
    if (actionRows.Count == 0)
    {
      sheet.Cell(15, 1).Value = RestaurantSaleReadinessSeverities.Ready;
      sheet.Cell(15, 6).Value = "No se encontraron acciones preventivas para la simulación base.";
      sheet.Cell(15, 8).Value = "Vuelve a generar el reporte cerca del inicio del servicio.";
    }
    sheet.Column(7).Style.NumberFormat.Format = "0.0000";
    sheet.Columns(1, 8).AdjustToContents();
    SetWidths(sheet, new Dictionary<int, double>
    {
      [1] = 20, [2] = 18, [3] = 28, [4] = 12, [5] = 30, [6] = 55, [7] = 14, [8] = 55
    });
    sheet.RangeUsed()?.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
    sheet.Columns(3, 8).Style.Alignment.WrapText = true;
    ApplyStatusFormatting(sheet, 15, Math.Max(15, 14 + actionRows.Count), 1);

    var noteRow = Math.Max(17, 16 + actionRows.Count);
    sheet.Range(noteRow, 1, noteRow + 2, 8).Merge();
    sheet.Cell(noteRow, 1).Value =
      "Cómo leerlo: cada producto se probó de forma independiente con una unidad. Los productos compiten por ingredientes compartidos, " +
      "por lo que este archivo no garantiza disponibilidad futura. Los errores de cliente, pagos, promociones, entrega o PIN dependen de los datos capturados al cobrar y se documentan en Entorno POS.";
    sheet.Cell(noteRow, 1).Style.Fill.BackgroundColor = LightYellow;
    sheet.Cell(noteRow, 1).Style.Font.FontColor = XLColor.FromHtml("#6F4C00");
    sheet.Cell(noteRow, 1).Style.Alignment.WrapText = true;
    sheet.Cell(noteRow, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
    sheet.Row(noteRow).Height = 42;
  }

  private static void BuildProductsSheet(IXLWorksheet sheet, RestaurantSaleReadinessReport report)
  {
    var headers = new[]
    {
      "Estado", "Venta sin override", "Requiere supervisor", "Secciones", "ProductId", "SKU", "Producto", "Precio",
      "MaterialId", "Código material", "Material", "Modalidad", "Estación", "Activo", "Agotado", "Ingredientes",
      "Unidades vendibles estimadas", "Cuello de botella", "Errores", "Advertencias", "Mensaje POS previsto", "Acción sugerida"
    };
    var rows = report.Products.Select(product => new object?[]
    {
      product.Status, product.CanSellWithoutOverride, product.RequiresSupervisor, product.Sections, product.ProductId,
      product.Sku, product.ProductName, product.Price, product.MaterialId, product.MaterialCode, product.MaterialName,
      product.FulfillmentMode, product.KitchenStationName, product.IsActive, product.IsSoldOut, product.LeafIngredientCount,
      product.EstimatedSellableUnits, product.BottleneckMaterial, product.ErrorCount, product.WarningCount,
      product.PredictedPosMessage, product.SuggestedAction
    }).ToList();
    PrepareDataSheet(sheet, "Productos del menú", "Una fila por producto distinto visible en el menú actual. Simulación: 1 unidad.");
    WriteTable(sheet, 4, headers, rows, "ReadinessProducts", 1);
    sheet.Column(8).Style.NumberFormat.Format = "$#,##0.00";
    sheet.Column(17).Style.NumberFormat.Format = "#,##0";
    ApplyStatusFormatting(sheet, 5, Math.Max(5, rows.Count + 4), 1);
    SetWidths(sheet, new Dictionary<int, double>
    {
      [1] = 25, [2] = 14, [3] = 16, [4] = 25, [5] = 12, [6] = 18, [7] = 30, [8] = 13,
      [9] = 12, [10] = 18, [11] = 30, [12] = 18, [13] = 22, [14] = 10, [15] = 10, [16] = 12,
      [17] = 18, [18] = 30, [19] = 10, [20] = 12, [21] = 60, [22] = 50
    });
    sheet.Columns(4, 22).Style.Alignment.WrapText = true;
  }

  private static void BuildIngredientsSheet(IXLWorksheet sheet, RestaurantSaleReadinessReport report)
  {
    var headers = new[]
    {
      "Estado", "SKU", "Producto", "MaterialId", "Código material", "Ingrediente", "Unidad base", "Ruta BOM", "Profundidad",
      "Control por lote", "Requerido por venta", "Existencia", "Reservado", "Disponible utilizable", "Lotes excluidos",
      "Disponible después de venta", "Mínimo", "Faltante", "Unidades vendibles", "Ubicaciones disponibles", "Mensaje POS previsto"
    };
    var rows = report.Ingredients.Select(ingredient => new object?[]
    {
      ingredient.Status, ingredient.ProductSku, ingredient.ProductName, ingredient.MaterialId, ingredient.MaterialCode,
      ingredient.MaterialName, ingredient.BaseUnit, ingredient.BomPath, ingredient.BomDepth, ingredient.TrackLots,
      ingredient.RequiredQuantity, ingredient.StockQuantity, ingredient.ReservedQuantity, ingredient.UsableQuantity,
      ingredient.ExcludedLotQuantity, ingredient.ProjectedUsableQuantity, ingredient.MinimumQuantity, ingredient.ShortageQuantity,
      ingredient.EstimatedSellableUnits, ingredient.LocationSummary, ingredient.PredictedPosMessage
    }).ToList();
    PrepareDataSheet(sheet, "Ingredientes requeridos", "Disponibilidad calculada como existencia menos reservas; lotes bloqueados o vencidos no son utilizables.");
    WriteTable(sheet, 4, headers, rows, "ReadinessIngredients", 1);
    foreach (var column in new[] { 11, 12, 13, 14, 15, 16, 17, 18 }) sheet.Column(column).Style.NumberFormat.Format = "0.0000";
    sheet.Column(19).Style.NumberFormat.Format = "#,##0";
    ApplyStatusFormatting(sheet, 5, Math.Max(5, rows.Count + 4), 1);
    SetWidths(sheet, new Dictionary<int, double>
    {
      [1] = 25, [2] = 18, [3] = 28, [4] = 12, [5] = 18, [6] = 30, [7] = 12, [8] = 65,
      [9] = 11, [10] = 14, [11] = 16, [12] = 14, [13] = 14, [14] = 18, [15] = 16, [16] = 20,
      [17] = 14, [18] = 14, [19] = 16, [20] = 55, [21] = 60
    });
    sheet.Columns(3, 21).Style.Alignment.WrapText = true;
  }

  private static void BuildBomSheet(IXLWorksheet sheet, RestaurantSaleReadinessReport report)
  {
    var headers = new[]
    {
      "Estado", "SKU", "Producto", "Profundidad", "Ruta", "Material padre ID", "Código padre", "Material padre",
      "BOM versión ID", "Versión", "Rendimiento", "Unidad rendimiento", "Componente ID", "Código componente",
      "Componente", "Modalidad componente", "Cantidad componente", "Unidad componente", "Merma %", "Factor conversión",
      "Cantidad base requerida", "Mensaje"
    };
    var rows = report.BomRows.Select(row => new object?[]
    {
      row.Status, row.ProductSku, row.ProductName, row.Depth, row.Path, row.ParentMaterialId, row.ParentMaterialCode,
      row.ParentMaterialName, row.BomVersionId, row.BomVersionNumber, row.YieldQuantity, row.YieldUnit,
      row.ComponentMaterialId, row.ComponentMaterialCode, row.ComponentMaterialName, row.ComponentFulfillmentMode,
      row.ComponentQuantity, row.ComponentUnit, row.ExpectedWastePercent.HasValue ? row.ExpectedWastePercent.Value / 100m : null,
      row.ConversionFactor, row.RequiredBaseQuantity, row.Message
    }).ToList();
    PrepareDataSheet(sheet, "BOM y conversiones", "Árbol completo de fabricación bajo pedido. La ruta permite localizar dependencias indirectas.");
    WriteTable(sheet, 4, headers, rows, "ReadinessBom", 1);
    foreach (var column in new[] { 11, 17, 20, 21 }) sheet.Column(column).Style.NumberFormat.Format = "0.0000";
    sheet.Column(19).Style.NumberFormat.Format = "0.00%";
    ApplyStatusFormatting(sheet, 5, Math.Max(5, rows.Count + 4), 1);
    SetWidths(sheet, new Dictionary<int, double>
    {
      [1] = 25, [2] = 18, [3] = 28, [4] = 11, [5] = 70, [6] = 14, [7] = 18, [8] = 30,
      [9] = 15, [10] = 10, [11] = 14, [12] = 14, [13] = 14, [14] = 18, [15] = 30, [16] = 20,
      [17] = 16, [18] = 15, [19] = 12, [20] = 16, [21] = 20, [22] = 60
    });
    sheet.Columns(3, 22).Style.Alignment.WrapText = true;
  }

  private static void BuildModifiersSheet(IXLWorksheet sheet, RestaurantSaleReadinessReport report)
  {
    var headers = new[]
    {
      "Estado", "SKU", "Producto", "Grupo ID", "Grupo", "Mínimo", "Máximo", "Opción ID", "Opción", "Precio adicional",
      "MaterialId", "Código material", "Ingrediente", "Delta", "Unidad delta", "Factor conversión", "Impacto unidad base",
      "Disponible después del producto base", "Mensaje"
    };
    var rows = report.Modifiers.Select(row => new object?[]
    {
      row.Status, row.ProductSku, row.ProductName, row.GroupId, row.GroupName, row.MinSelections, row.MaxSelections,
      row.OptionId, row.OptionName, row.PriceDelta, row.MaterialId, row.MaterialCode, row.MaterialName, row.QuantityDelta,
      row.DeltaUnit, row.ConversionFactor, row.BaseQuantityImpact, row.AvailableAfterBaseProduct, row.Message
    }).ToList();
    PrepareDataSheet(
      sheet,
      "Modificadores",
      rows.Count == 0
        ? "El menú evaluado no contiene modificadores activos."
        : "Cada opción se prueba de forma independiente sobre los requerimientos del producto base.");
    WriteTable(sheet, 4, headers, rows, "ReadinessModifiers", 1);
    sheet.Column(10).Style.NumberFormat.Format = "$#,##0.00";
    foreach (var column in new[] { 14, 16, 17, 18 }) sheet.Column(column).Style.NumberFormat.Format = "0.0000";
    ApplyStatusFormatting(sheet, 5, Math.Max(5, rows.Count + 4), 1);
    SetWidths(sheet, new Dictionary<int, double>
    {
      [1] = 25, [2] = 18, [3] = 28, [4] = 12, [5] = 28, [6] = 10, [7] = 10, [8] = 12,
      [9] = 28, [10] = 15, [11] = 12, [12] = 18, [13] = 30, [14] = 14, [15] = 14, [16] = 16,
      [17] = 18, [18] = 22, [19] = 60
    });
    sheet.Columns(3, 19).Style.Alignment.WrapText = true;
  }

  private static void BuildEnvironmentSheet(IXLWorksheet sheet, RestaurantSaleReadinessReport report)
  {
    var headers = new[] { "Estado", "Área", "Validación", "Detalle", "Acción recomendada" };
    var rows = report.EnvironmentChecks.Select(check => new object?[]
    {
      check.Status, check.Area, check.Check, check.Detail, check.RecommendedAction
    }).ToList();
    PrepareDataSheet(sheet, "Entorno POS", "Condiciones globales y límites de la simulación que no dependen de un producto específico.");
    WriteTable(sheet, 4, headers, rows, "ReadinessEnvironment", 1);
    ApplyStatusFormatting(sheet, 5, Math.Max(5, rows.Count + 4), 1);
    SetWidths(sheet, new Dictionary<int, double> { [1] = 25, [2] = 18, [3] = 30, [4] = 80, [5] = 60 });
    sheet.Columns(2, 5).Style.Alignment.WrapText = true;
  }

  private static void PrepareDataSheet(IXLWorksheet sheet, string title, string subtitle)
  {
    sheet.TabColor = MediumGreen;
    sheet.Range("A1:F1").Merge();
    sheet.Cell("A1").Value = title;
    StyleTitle(sheet.Range("A1:F1"));
    sheet.Range("A2:F2").Merge();
    sheet.Cell("A2").Value = subtitle;
    sheet.Cell("A2").Style.Font.FontColor = TextGray;
    sheet.Cell("A2").Style.Alignment.WrapText = true;
    sheet.Row(2).Height = 28;
    sheet.SheetView.FreezeRows(4);
  }

  private static void WriteTable(
    IXLWorksheet sheet,
    int headerRow,
    IReadOnlyList<string> headers,
    IReadOnlyList<object?[]> rows,
    string tableName,
    int statusColumn)
  {
    for (var column = 0; column < headers.Count; column++) sheet.Cell(headerRow, column + 1).Value = headers[column];
    for (var row = 0; row < rows.Count; row++)
      for (var column = 0; column < headers.Count; column++)
        SetCellValue(sheet.Cell(headerRow + row + 1, column + 1), rows[row][column]);

    var lastRow = Math.Max(headerRow + 1, headerRow + rows.Count);
    var header = sheet.Range(headerRow, 1, headerRow, headers.Count);
    header.Style.Fill.BackgroundColor = DarkGreen;
    header.Style.Font.Bold = true;
    header.Style.Font.FontColor = XLColor.White;
    header.Style.Alignment.WrapText = true;
    header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
    sheet.Row(headerRow).Height = 32;
    if (rows.Count > 0)
    {
      var table = sheet.Range(headerRow, 1, lastRow, headers.Count).CreateTable(tableName);
      table.Theme = XLTableTheme.TableStyleMedium2;
      table.ShowAutoFilter = true;
    }
    else
    {
      sheet.Range(headerRow, 1, headerRow, headers.Count).SetAutoFilter();
    }
    sheet.Range(headerRow + 1, 1, lastRow, headers.Count).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
    if (statusColumn > 0) ApplyStatusFormatting(sheet, headerRow + 1, lastRow, statusColumn);
  }

  private static void ApplyStatusFormatting(IXLWorksheet sheet, int firstRow, int lastRow, int column)
  {
    if (lastRow < firstRow) return;
    var range = sheet.Range(firstRow, column, lastRow, column);
    range.Style.Font.Bold = true;
    range.Style.Alignment.WrapText = true;
    range.AddConditionalFormat().WhenEquals(RestaurantSaleReadinessStatuses.Ready).Fill.SetBackgroundColor(LightGreen);
    range.AddConditionalFormat().WhenEquals(RestaurantSaleReadinessStatuses.Warning).Fill.SetBackgroundColor(LightYellow);
    range.AddConditionalFormat().WhenEquals(RestaurantSaleReadinessStatuses.SupervisorRequired).Fill.SetBackgroundColor(LightOrange);
    range.AddConditionalFormat().WhenEquals(RestaurantSaleReadinessStatuses.InventoryBlocked).Fill.SetBackgroundColor(LightRed);
    range.AddConditionalFormat().WhenEquals(RestaurantSaleReadinessStatuses.ConfigurationBlocked).Fill.SetBackgroundColor(LightRed);
    range.AddConditionalFormat().WhenEquals(RestaurantSaleReadinessStatuses.SoldOut).Fill.SetBackgroundColor(LightGray);
    range.AddConditionalFormat().WhenEquals(RestaurantSaleReadinessSeverities.Error).Fill.SetBackgroundColor(LightRed);
  }

  private static void StyleTitle(IXLRange range)
  {
    range.Style.Fill.BackgroundColor = DarkGreen;
    range.Style.Font.FontColor = XLColor.White;
    range.Style.Font.Bold = true;
    range.Style.Font.FontSize = 16;
    range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
    range.FirstRow().WorksheetRow().Height = 34;
  }

  private static void StyleSectionHeader(IXLRange range)
  {
    range.Style.Fill.BackgroundColor = MediumGreen;
    range.Style.Font.FontColor = XLColor.White;
    range.Style.Font.Bold = true;
    range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
    range.FirstRow().WorksheetRow().Height = 24;
  }

  private static void SetWidths(IXLWorksheet sheet, IReadOnlyDictionary<int, double> widths)
  {
    foreach (var width in widths) sheet.Column(width.Key).Width = width.Value;
  }

  private static void SetCellValue(IXLCell cell, object? value)
  {
    switch (value)
    {
      case null: cell.Clear(); break;
      case int number: cell.Value = number; break;
      case long number: cell.Value = number; break;
      case decimal number: cell.Value = number; break;
      case double number: cell.Value = number; break;
      case bool boolean: cell.Value = boolean; break;
      case DateTime dateTime: cell.Value = dateTime; cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm"; break;
      case DateOnly date: cell.Value = date.ToDateTime(TimeOnly.MinValue); cell.Style.DateFormat.Format = "yyyy-mm-dd"; break;
      case string text when string.IsNullOrWhiteSpace(text): cell.Clear(); break;
      default: cell.Value = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty; break;
    }
  }

  private static string SanitizeFileSegment(string? value)
  {
    var sanitized = string.IsNullOrWhiteSpace(value) ? "NA" : value.Trim();
    foreach (var invalid in Path.GetInvalidFileNameChars()) sanitized = sanitized.Replace(invalid, '_');
    return sanitized.Replace(' ', '-');
  }
}
