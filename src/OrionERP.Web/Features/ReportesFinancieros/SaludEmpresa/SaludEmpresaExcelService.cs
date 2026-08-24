using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace OrionERP.Web.Features.ReportesFinancieros.SaludEmpresa;

public sealed class SaludEmpresaExcelService : ISaludEmpresaExcelService
{
  public byte[] Generate(SaludEmpresaPdfDocumentModel model)
  {
    var sheets = new[]
    {
      BuildTrendSheet(model),
      BuildTargetSheet(model),
      BuildReconciliationSheet(model),
      BuildMethodologySheet(model)
    };

    using var output = new MemoryStream();
    using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
    {
      WriteText(archive, "[Content_Types].xml", ContentTypes(sheets.Length));
      WriteText(archive, "_rels/.rels", RootRelationships);
      WriteText(archive, "xl/workbook.xml", Workbook(sheets));
      WriteText(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationships(sheets.Length));
      WriteText(archive, "xl/styles.xml", Styles);
      for (var i = 0; i < sheets.Length; i++)
        WriteText(archive, $"xl/worksheets/sheet{i + 1}.xml", Worksheet(sheets[i]));
    }
    return output.ToArray();
  }

  private static SheetData BuildTrendSheet(SaludEmpresaPdfDocumentModel model)
  {
    var rows = model.Report.Trends.Select(row => new object?[]
    {
      row.Month, row.TotalOperatingRevenue, row.RevenueTarget, row.PreviousYearRevenue,
      row.NetResult, row.OperatingMarginPct, row.OccupancyPct, row.ADR, row.RevPAR
    }).ToList();
    return new("Tendencias",
      ["Mes", "Ingreso operativo", "Meta ingreso", "Año anterior", "Resultado neto", "Margen operativo %", "Ocupación %", "ADR", "RevPAR"], rows);
  }

  private static SheetData BuildTargetSheet(SaludEmpresaPdfDocumentModel model)
  {
    var rows = (model.Targets ?? []).Select(row => new object?[]
    {
      row.Month,row.RoomRevenueTarget,row.ComplementaryRevenueTarget,row.OccupancyPctTarget,row.AdrTarget,
      row.OperatingExpensesTarget,row.NetResultTarget,row.NetCashFlowTarget,row.ClosingCashTarget,row.Notes,row.UpdatedBy,row.UpdatedAtUtc
    }).ToList();
    return new("Metas",
      ["Mes", "Habitación", "Complementario", "Ocupación %", "ADR", "Gastos operativos", "Resultado neto", "Flujo neto", "Saldo efectivo", "Notas", "Actualizado por", "Actualizado UTC"], rows);
  }

  private static SheetData BuildReconciliationSheet(SaludEmpresaPdfDocumentModel model)
  {
    var rows = (model.Reconciliation ?? []).Select(row => new object?[]
    {
      row.Severity,row.Type,row.Item,row.EventDate,row.Amount,row.ReferenceAmount,row.NetEffect,row.Reference,row.Notes,
      row.ReservationId?.ToString(CultureInfo.InvariantCulture),row.TransactionId?.ToString(CultureInfo.InvariantCulture)
    }).ToList();
    return new("Conciliación",
      ["Severidad", "Tipo", "Observación", "Fecha", "Monto", "Referencia monto", "Efecto", "Referencia", "Notas", "Reservación", "Transacción"], rows);
  }

  private static SheetData BuildMethodologySheet(SaludEmpresaPdfDocumentModel model)
    => new("Metodología", ["Campo", "Valor"],
    [
      ["RFC", model.Rfc],
      ["Fecha de corte", model.Report.Metadata.CutoffDate],
      ["Estado", model.Report.Metadata.IsProvisional ? "Provisional" : "Cerrado"],
      ["Versión", model.Report.Metadata.MethodologyVersion],
      ["Aviso", "Cifras internas no auditadas."],
      ["Ocupación", "Noches vendidas / noches disponibles"],
      ["ADR", "Ingreso neto de habitación / noches vendidas"],
      ["RevPAR", "Ingreso neto de habitación / noches disponibles"],
      ["TRevPAR", "Habitación + extras + experiencias netos / noches disponibles"]
    ]);

  private static string Worksheet(SheetData sheet)
  {
    var builder = new StringBuilder();
    using var writer = XmlWriter.Create(builder, new XmlWriterSettings { OmitXmlDeclaration = true });
    writer.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
    writer.WriteStartElement("sheetViews"); writer.WriteStartElement("sheetView"); writer.WriteAttributeString("workbookViewId", "0");
    writer.WriteStartElement("pane"); writer.WriteAttributeString("ySplit", "1"); writer.WriteAttributeString("topLeftCell", "A2"); writer.WriteAttributeString("state", "frozen"); writer.WriteEndElement();
    writer.WriteEndElement(); writer.WriteEndElement();
    writer.WriteStartElement("cols");
    for (var i = 1; i <= sheet.Headers.Count; i++) { writer.WriteStartElement("col"); writer.WriteAttributeString("min", i.ToString()); writer.WriteAttributeString("max", i.ToString()); writer.WriteAttributeString("width", i <= 3 ? "22" : "17"); writer.WriteAttributeString("customWidth", "1"); writer.WriteEndElement(); }
    writer.WriteEndElement();
    writer.WriteStartElement("sheetData");
    WriteRow(writer, 1, sheet.Headers.Cast<object?>().ToArray(), header: true);
    for (var i = 0; i < sheet.Rows.Count; i++) WriteRow(writer, i + 2, sheet.Rows[i], header: false);
    writer.WriteEndElement();
    writer.WriteStartElement("autoFilter"); writer.WriteAttributeString("ref", $"A1:{ColumnName(sheet.Headers.Count)}{sheet.Rows.Count + 1}"); writer.WriteEndElement();
    writer.WriteEndElement(); writer.WriteEndDocument();
    writer.Flush();
    return builder.ToString();
  }

  private static void WriteRow(XmlWriter writer, int rowNumber, IReadOnlyList<object?> values, bool header)
  {
    writer.WriteStartElement("row"); writer.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture));
    for (var i = 0; i < values.Count; i++)
    {
      var value = values[i];
      writer.WriteStartElement("c"); writer.WriteAttributeString("r", $"{ColumnName(i + 1)}{rowNumber}");
      if (header) writer.WriteAttributeString("s", "1");
      if (value is DateTime date)
      {
        writer.WriteAttributeString("s", "2"); writer.WriteElementString("v", date.ToOADate().ToString(CultureInfo.InvariantCulture));
      }
      else if (value is decimal or double or float or int or long)
      {
        writer.WriteAttributeString("s", "3"); writer.WriteElementString("v", Convert.ToString(value, CultureInfo.InvariantCulture));
      }
      else
      {
        writer.WriteAttributeString("t", "inlineStr"); writer.WriteStartElement("is"); writer.WriteElementString("t", value?.ToString() ?? string.Empty); writer.WriteEndElement();
      }
      writer.WriteEndElement();
    }
    writer.WriteEndElement();
  }

  private static string Workbook(IReadOnlyList<SheetData> sheets)
  {
    var entries = string.Join(string.Empty, sheets.Select((sheet, i) => $"<sheet name=\"{XmlEscape(sheet.Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>"));
    return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>{entries}</sheets></workbook>";
  }

  private static string WorkbookRelationships(int count)
  {
    var entries = string.Join(string.Empty, Enumerable.Range(1, count).Select(i => $"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>"));
    return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">{entries}<Relationship Id=\"rId{count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>";
  }

  private static string ContentTypes(int count)
  {
    var entries = string.Join(string.Empty, Enumerable.Range(1, count).Select(i => $"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"));
    return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>{entries}</Types>";
  }

  private static void WriteText(ZipArchive archive, string path, string content)
  {
    var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
    writer.Write(content);
  }

  private static string ColumnName(int index)
  {
    var name = string.Empty;
    while (index > 0) { index--; name = (char)('A' + index % 26) + name; index /= 26; }
    return name;
  }

  private static string XmlEscape(string value)
    => System.Security.SecurityElement.Escape(value) ?? string.Empty;

  private sealed record SheetData(string Name, IReadOnlyList<string> Headers, IReadOnlyList<object?[]> Rows);

  private const string RootRelationships = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>";
  private const string Styles = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"dd/mm/yyyy\"/></numFmts><fonts count=\"2\"><font><sz val=\"10\"/><name val=\"Aptos\"/></font><font><b/><color rgb=\"FFFFFFFF\"/><sz val=\"10\"/><name val=\"Aptos\"/></font></fonts><fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF0B5A68\"/><bgColor indexed=\"64\"/></patternFill></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"4\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/><xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/><xf numFmtId=\"4\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/></cellXfs></styleSheet>";
}
