using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.CapitalHumano.Workforce;

namespace OrionERP.Infrastructure.Features.CapitalHumano.Workforce;

public sealed class PrenominaExportService : WorkforceServiceBase, IPrenominaExportService
{
  private const string LayoutVersion = "1.0";

  public PrenominaExportService(IDbConnectionFactory connectionFactory, ICurrentEmployeeAccessor currentEmployeeAccessor)
    : base(connectionFactory, currentEmployeeAccessor) { }

  public async Task<PrenominaExportBundle> GenerateAsync(long periodId, string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoNomina");
    using var connection = CreateOpenConnection();
    var existingId = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
      "SELECT TOP(1) Id FROM rh.PrenominaExport WHERE PeriodId=@PeriodId ORDER BY Id;", new { PeriodId = periodId }, cancellationToken: ct));
    if (existingId.HasValue)
      return (await ReadBundleAsync(connection, existingId.Value, normalizedRfc, ct))!;

    var period = await PrenominaService.GetPeriodAsync(connection, null, periodId, normalizedRfc, false, ct)
      ?? throw new KeyNotFoundException("El periodo no existe.");
    if (period.Status is not (PrenominaStatuses.Locked or PrenominaStatuses.Exported or PrenominaStatuses.Reopened))
      throw new InvalidOperationException("El periodo debe estar bloqueado antes de exportar.");
    var lines = (await connection.QueryAsync<PrenominaLineDto>(new CommandDefinition(
      "SELECT EmployeeId,EmployeeName,ScheduledMinutes,WorkedMinutes,OvertimeApprovedMinutes,PaidLeaveDays,UnpaidLeaveDays,ExceptionCount FROM rh.PrenominaSnapshotLine WHERE PeriodId=@PeriodId ORDER BY EmployeeName,EmployeeId;",
      new { PeriodId = periodId }, cancellationToken: ct))).AsList();
    if (lines.Count == 0) throw new InvalidOperationException("El snapshot bloqueado no contiene lineas.");

    var incidents = (await connection.QueryAsync<IncidentRow>(new CommandDefinition(
      """
      SELECT x.EmployeeId,x.WorkDate,x.ExceptionType,x.Detail,x.[Status]
      FROM rh.AttendanceException x WHERE x.Rfc=@Rfc AND x.WorkDate BETWEEN @FromDate AND @ToDate
        AND EXISTS(SELECT 1 FROM rh.PrenominaSnapshotLine l WHERE l.PeriodId=@PeriodId AND l.EmployeeId=x.EmployeeId)
      ORDER BY x.WorkDate,x.EmployeeId,x.Id;
      """, new { Rfc = normalizedRfc, period.FromDate, period.ToDate, PeriodId = periodId }, cancellationToken: ct))).AsList();
    var absences = (await connection.QueryAsync<AbsenceRow>(new CommandDefinition(
      """
      SELECT l.EmployeeId,t.Code,t.[Name] LeaveType,l.StartDate,l.EndDate,l.RequestedDays,t.IsPaid,l.[Status]
      FROM rh.LeaveRequest l INNER JOIN rh.LeaveType t ON t.Id=l.LeaveTypeId
      WHERE l.Rfc=@Rfc AND l.StartDate<=@ToDate AND l.EndDate>=@FromDate
        AND EXISTS(SELECT 1 FROM rh.PrenominaSnapshotLine s WHERE s.PeriodId=@PeriodId AND s.EmployeeId=l.EmployeeId)
      ORDER BY l.StartDate,l.EmployeeId,l.Id;
      """, new { Rfc = normalizedRfc, period.FromDate, period.ToDate, PeriodId = periodId }, cancellationToken: ct))).AsList();
    var validations = (await connection.QueryAsync<ValidationRow>(new CommandDefinition(
      "SELECT IsValid,ErrorsJson,WarningsJson,ValidatedAtUtc,ValidatedBy FROM rh.PrenominaValidationResult WHERE PeriodId=@PeriodId ORDER BY Id;",
      new { PeriodId = periodId }, cancellationToken: ct))).AsList();

    var xlsx = BuildWorkbook(period, lines, incidents, absences, validations);
    var csvFiles = BuildCsvFiles(period, lines, incidents, absences, validations);
    var generatedAt = DateTime.UtcNow;
    var zip = BuildZip(period, csvFiles, generatedAt);
    var xlsxHash = Sha256(xlsx);
    var zipHash = Sha256(zip);
    var baseName = $"pre-nomina-{period.FromDate:yyyyMMdd}-{period.ToDate:yyyyMMdd}-v{period.Version}";
    var exportId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
      """
      INSERT INTO rh.PrenominaExport
        (PeriodId,LayoutVersion,XlsxFileName,XlsxContent,XlsxSha256,ZipFileName,ZipContent,ZipSha256,CreatedBy)
      VALUES (@PeriodId,@LayoutVersion,@XlsxFileName,@XlsxContent,@XlsxSha256,@ZipFileName,@ZipContent,@ZipSha256,@Actor);
      UPDATE rh.PrenominaPeriod SET [Status]='EXPORTED' WHERE Id=@PeriodId AND [Status]='LOCKED';
      SELECT CAST(SCOPE_IDENTITY() AS bigint);
      """, new { PeriodId = periodId, LayoutVersion, XlsxFileName = $"{baseName}.xlsx", XlsxContent = xlsx, XlsxSha256 = xlsxHash, ZipFileName = $"{baseName}-csv.zip", ZipContent = zip, ZipSha256 = zipHash, Actor = actor.UserName }, cancellationToken: ct));
    return new PrenominaExportBundle { ExportId = exportId, XlsxFileName = $"{baseName}.xlsx", XlsxBytes = xlsx, ZipFileName = $"{baseName}-csv.zip", ZipBytes = zip, XlsxSha256 = xlsxHash, ZipSha256 = zipHash };
  }

  public async Task<PrenominaExportBundle?> GetAsync(long exportId, string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    await RequireActorAsync(normalizedRfc, false, ct, "CapitalHumanoAdmin", "CapitalHumanoNomina");
    using var connection = CreateOpenConnection();
    return await ReadBundleAsync(connection, exportId, normalizedRfc, ct);
  }

  private static async Task<PrenominaExportBundle?> ReadBundleAsync(IDbConnection connection, long exportId, string rfc, CancellationToken ct)
    => await connection.QuerySingleOrDefaultAsync<PrenominaExportBundle>(new CommandDefinition(
      """
      SELECT e.Id ExportId,e.XlsxFileName,e.XlsxContent XlsxBytes,e.ZipFileName,e.ZipContent ZipBytes,e.XlsxSha256,e.ZipSha256
      FROM rh.PrenominaExport e INNER JOIN rh.PrenominaPeriod p ON p.Id=e.PeriodId WHERE e.Id=@Id AND p.Rfc=@Rfc;
      """, new { Id = exportId, Rfc = rfc }, cancellationToken: ct));

  private static byte[] BuildWorkbook(PrenominaService.PeriodRow period, IReadOnlyList<PrenominaLineDto> lines, IReadOnlyList<IncidentRow> incidents, IReadOnlyList<AbsenceRow> absences, IReadOnlyList<ValidationRow> validations)
  {
    using var workbook = new XLWorkbook();
    AddSheet(workbook, "Resumen", new[] { "Concepto", "Valor" }, new object?[][]
    {
      ["Periodo", $"{period.FromDate:yyyy-MM-dd} a {period.ToDate:yyyy-MM-dd}"],
      ["Version", period.Version], ["Empleados", lines.Count],
      ["Minutos trabajados", lines.Sum(x => x.WorkedMinutes)],
      ["Minutos extra aprobados", lines.Sum(x => x.OvertimeApprovedMinutes)],
      ["Layout", LayoutVersion]
    });
    AddSheet(workbook, "Detalle", new[] { "EmpleadoId", "Empleado", "ProgramadosMin", "TrabajadosMin", "ExtraAprobadaMin", "AusenciaPagadaDias", "AusenciaNoPagadaDias", "Incidencias" },
      lines.Select(x => new object?[] { x.EmployeeId, x.EmployeeName, x.ScheduledMinutes, x.WorkedMinutes, x.OvertimeApprovedMinutes, x.PaidLeaveDays, x.UnpaidLeaveDays, x.ExceptionCount }));
    AddSheet(workbook, "HorasExtra", new[] { "EmpleadoId", "Empleado", "MinutosAprobados" },
      lines.Where(x => x.OvertimeApprovedMinutes > 0).Select(x => new object?[] { x.EmployeeId, x.EmployeeName, x.OvertimeApprovedMinutes }));
    AddSheet(workbook, "Incidencias", new[] { "EmpleadoId", "Fecha", "Tipo", "Detalle", "Estado" },
      incidents.Select(x => new object?[] { x.EmployeeId, x.WorkDate, x.ExceptionType, x.Detail, x.Status }));
    AddSheet(workbook, "Ausencias", new[] { "EmpleadoId", "Codigo", "Tipo", "Inicio", "Fin", "Dias", "Pagada", "Estado" },
      absences.Select(x => new object?[] { x.EmployeeId, x.Code, x.LeaveType, x.StartDate, x.EndDate, x.RequestedDays, x.IsPaid ? "Si" : "No", x.Status }));
    AddSheet(workbook, "Validaciones", new[] { "Valido", "Errores", "Advertencias", "FechaUTC", "Usuario" },
      validations.Select(x => new object?[] { x.IsValid ? "Si" : "No", x.ErrorsJson, x.WarningsJson, x.ValidatedAtUtc, x.ValidatedBy }));
    using var output = new MemoryStream();
    workbook.SaveAs(output);
    return output.ToArray();
  }

  private static void AddSheet(XLWorkbook workbook, string name, IReadOnlyList<string> headers, IEnumerable<object?[]> rows)
  {
    var sheet = workbook.Worksheets.Add(name);
    for (var column = 0; column < headers.Count; column++) sheet.Cell(1, column + 1).Value = headers[column];
    var rowNumber = 2;
    foreach (var row in rows)
    {
      for (var column = 0; column < row.Length; column++) SetCellValue(sheet.Cell(rowNumber, column + 1), row[column]);
      rowNumber++;
    }
    var header = sheet.Range(1, 1, 1, headers.Count);
    header.Style.Font.Bold = true;
    header.Style.Font.FontColor = XLColor.White;
    header.Style.Fill.BackgroundColor = XLColor.FromHtml("#234961");
    sheet.SheetView.FreezeRows(1);
    sheet.Range(1, 1, Math.Max(1, rowNumber - 1), headers.Count).SetAutoFilter();
    for (var columnNumber = 1; columnNumber <= headers.Count; columnNumber++)
    {
      var column = sheet.Column(columnNumber);
      column.AdjustToContents();
      column.Width = Math.Clamp(column.Width, 10d, 45d);
    }
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
      default: cell.Value = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty; break;
    }
  }

  private static Dictionary<string, byte[]> BuildCsvFiles(PrenominaService.PeriodRow period, IReadOnlyList<PrenominaLineDto> lines, IReadOnlyList<IncidentRow> incidents, IReadOnlyList<AbsenceRow> absences, IReadOnlyList<ValidationRow> validations)
  {
    var files = new Dictionary<string, byte[]>();
    files["Resumen.csv"] = Csv(new[] { new[] { "Concepto", "Valor" }, new[] { "Periodo", $"{period.FromDate:yyyy-MM-dd} a {period.ToDate:yyyy-MM-dd}" }, new[] { "Version", period.Version.ToString(CultureInfo.InvariantCulture) }, new[] { "Layout", LayoutVersion } });
    files["Detalle.csv"] = Csv(new[] { new[] { "EmpleadoId", "Empleado", "ProgramadosMin", "TrabajadosMin", "ExtraAprobadaMin", "AusenciaPagadaDias", "AusenciaNoPagadaDias", "Incidencias" } }.Concat(lines.Select(x => new[] { F(x.EmployeeId), x.EmployeeName, F(x.ScheduledMinutes), F(x.WorkedMinutes), F(x.OvertimeApprovedMinutes), F(x.PaidLeaveDays), F(x.UnpaidLeaveDays), F(x.ExceptionCount) })));
    files["HorasExtra.csv"] = Csv(new[] { new[] { "EmpleadoId", "Empleado", "MinutosAprobados" } }.Concat(lines.Where(x => x.OvertimeApprovedMinutes > 0).Select(x => new[] { F(x.EmployeeId), x.EmployeeName, F(x.OvertimeApprovedMinutes) })));
    files["Incidencias.csv"] = Csv(new[] { new[] { "EmpleadoId", "Fecha", "Tipo", "Detalle", "Estado" } }.Concat(incidents.Select(x => new[] { F(x.EmployeeId), F(x.WorkDate), x.ExceptionType, x.Detail, x.Status })));
    files["Ausencias.csv"] = Csv(new[] { new[] { "EmpleadoId", "Codigo", "Tipo", "Inicio", "Fin", "Dias", "Pagada", "Estado" } }.Concat(absences.Select(x => new[] { F(x.EmployeeId), x.Code, x.LeaveType, F(x.StartDate), F(x.EndDate), F(x.RequestedDays), x.IsPaid ? "Si" : "No", x.Status })));
    files["Validaciones.csv"] = Csv(new[] { new[] { "Valido", "Errores", "Advertencias", "FechaUTC", "Usuario" } }.Concat(validations.Select(x => new[] { x.IsValid ? "Si" : "No", x.ErrorsJson, x.WarningsJson, x.ValidatedAtUtc.ToString("O", CultureInfo.InvariantCulture), x.ValidatedBy })));
    return files;
  }

  private static byte[] BuildZip(PrenominaService.PeriodRow period, IReadOnlyDictionary<string, byte[]> files, DateTime generatedAt)
  {
    using var stream = new MemoryStream();
    using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true, Encoding.UTF8))
    {
      foreach (var file in files)
      {
        var entry = archive.CreateEntry(file.Key, System.IO.Compression.CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        entryStream.Write(file.Value);
      }
      var manifest = new
      {
        layoutVersion = LayoutVersion,
        period = new { from = period.FromDate, to = period.ToDate, version = period.Version },
        snapshotId = period.Id,
        generatedAtUtc = generatedAt,
        files = files.Select(x => new { name = x.Key, sha256 = Sha256(x.Value), bytes = x.Value.Length })
      };
      var manifestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
      var manifestEntry = archive.CreateEntry("manifest.json", System.IO.Compression.CompressionLevel.Optimal);
      using var manifestStream = manifestEntry.Open();
      manifestStream.Write(manifestBytes);
    }
    return stream.ToArray();
  }

  private static byte[] Csv(IEnumerable<string[]> rows)
  {
    var builder = new StringBuilder();
    builder.Append('\uFEFF');
    foreach (var row in rows) builder.AppendLine(string.Join(',', row.Select(EscapeCsv)));
    return Encoding.UTF8.GetBytes(builder.ToString());
  }
  private static string EscapeCsv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
  private static string F<T>(T value) where T : IFormattable => value.ToString(null, CultureInfo.InvariantCulture);
  private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

  private sealed class IncidentRow { public int EmployeeId { get; set; } public DateOnly WorkDate { get; set; } public string ExceptionType { get; set; } = string.Empty; public string Detail { get; set; } = string.Empty; public string Status { get; set; } = string.Empty; }
  private sealed class AbsenceRow { public int EmployeeId { get; set; } public string Code { get; set; } = string.Empty; public string LeaveType { get; set; } = string.Empty; public DateOnly StartDate { get; set; } public DateOnly EndDate { get; set; } public decimal RequestedDays { get; set; } public bool IsPaid { get; set; } public string Status { get; set; } = string.Empty; }
  private sealed class ValidationRow { public bool IsValid { get; set; } public string ErrorsJson { get; set; } = string.Empty; public string WarningsJson { get; set; } = string.Empty; public DateTime ValidatedAtUtc { get; set; } public string ValidatedBy { get; set; } = string.Empty; }
}
