using System.IO.Compression;
using System.Reflection;
using OrionERP.Application.Features.CapitalHumano.Workforce;
using OrionERP.Infrastructure.Features.CapitalHumano.Workforce;

namespace OrionERP.UnitTests.CapitalHumano;

public sealed class WorkforceMvpStructureTests
{
  [Fact]
  public void SqlMigration_IsIdempotentAndKeepsEvidenceAndSnapshotsAppendOnly()
  {
    var sql = Read("src", "OrionERP.Infrastructure", "Features", "CapitalHumano", "Workforce", "Sql", "20260805_workforce_attendance_mvp.sql");

    Assert.Contains("IF OBJECT_ID('rh.TimeEvent', 'U') IS NULL", sql, StringComparison.Ordinal);
    Assert.Contains("UX_rh_TimeEvent_Idempotency", sql, StringComparison.Ordinal);
    Assert.Contains("LocationProtected varbinary(max)", sql, StringComparison.Ordinal);
    Assert.Contains("CREATE TABLE rh.PrenominaSnapshotLine", sql, StringComparison.Ordinal);
    Assert.Contains("CREATE TABLE rh.PrenominaValidationResult", sql, StringComparison.Ordinal);
    Assert.Contains("CREATE TABLE rh.OvertimeDecision", sql, StringComparison.Ordinal);
    Assert.Contains("CREATE TABLE rh.ScheduleBreak", sql, StringComparison.Ordinal);
    Assert.Contains("CREATE TABLE rh.PrivacyNotice", sql, StringComparison.Ordinal);
    Assert.Contains("CREATE TABLE rh.EmployeePrivacyAcknowledgement", sql, StringComparison.Ordinal);
    Assert.Contains("RowVersion rowversion", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("UPDATE rh.TimeEvent SET", sql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("DELETE FROM rh.TimeEvent", sql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("UPDATE rh.PrenominaSnapshotLine", sql, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void WebRoutesAndSecurityContracts_ArePresent()
  {
    var program = Read("src", "OrionERP.Web", "Program.cs");
    var navigation = Read("src", "OrionERP.Web", "Shared", "NavigationCatalog.cs");
    var gps = Read("src", "OrionERP.Infrastructure", "Features", "CapitalHumano", "Workforce", "GpsLocationProtector.cs");

    foreach (var route in new[] { "/mi-trabajo", "/mi-equipo", "/capital-humano/asistencia", "/capital-humano/configuracion-tiempo", "/capital-humano/ausencias", "/capital-humano/pre-nomina" })
      Assert.Contains(route, navigation, StringComparison.Ordinal);
    Assert.Contains("HttpOnly = true", program, StringComparison.Ordinal);
    Assert.Contains("SameSite = SameSiteMode.Strict", program, StringComparison.Ordinal);
    Assert.Contains("RequireRateLimiting(\"workforce-kiosk\")", program, StringComparison.Ordinal);
    Assert.Contains("Attendance.Gps.v1", gps, StringComparison.Ordinal);

    var attendance = Read("src", "OrionERP.Infrastructure", "Features", "CapitalHumano", "Workforce", "AttendanceService.cs");
    Assert.Contains("AcknowledgePrivacyNoticeAsync", attendance, StringComparison.Ordinal);
    Assert.Contains("ReturnCorrectionAsync", attendance, StringComparison.Ordinal);
    Assert.Contains("ReturnExceptionAsync", attendance, StringComparison.Ordinal);
    Assert.Contains("El supervisor debe estar ligado a un empleado", attendance, StringComparison.Ordinal);
    var serviceBase = Read("src", "OrionERP.Infrastructure", "Features", "CapitalHumano", "Workforce", "WorkforceServiceBase.cs");
    Assert.Contains("!actor.IsInRole(\"Administrador\")", serviceBase, StringComparison.Ordinal);

    var retention = Read("src", "OrionERP.Infrastructure", "Features", "CapitalHumano", "Workforce", "WorkforceRetentionMaintenance.cs");
    Assert.Contains("LocationProtected=NULL", retention, StringComparison.Ordinal);
    Assert.Contains("GPS_EVIDENCE_PURGED", retention, StringComparison.Ordinal);
    Assert.DoesNotContain("DELETE FROM rh.TimeEvent", retention, StringComparison.OrdinalIgnoreCase);
    var dateHandler = Read("src", "OrionERP.Infrastructure", "Features", "CapitalHumano", "Workforce", "WorkforceDapperTypeHandlers.cs");
    Assert.Contains("TypeHandler<DateOnly>", dateHandler, StringComparison.Ordinal);
  }

  [Fact]
  public void PreNominaExporter_HasRequiredWorkbookAndCsvLayouts()
  {
    var source = Read("src", "OrionERP.Infrastructure", "Features", "CapitalHumano", "Workforce", "PrenominaExportService.cs");
    foreach (var sheet in new[] { "Resumen", "Detalle", "HorasExtra", "Incidencias", "Ausencias", "Validaciones" })
    {
      Assert.Contains($"\"{sheet}\"", source, StringComparison.Ordinal);
      Assert.Contains($"\"{sheet}.csv\"", source, StringComparison.Ordinal);
    }
    Assert.Contains("manifest.json", source, StringComparison.Ordinal);
    Assert.Contains("SHA256.HashData", source, StringComparison.Ordinal);
  }

  [Fact]
  public void PreNominaExporter_ProducesReadableWorkbookWithRequiredSheets()
  {
    var method = typeof(PrenominaExportService).GetMethod("BuildWorkbook", BindingFlags.NonPublic | BindingFlags.Static)
      ?? throw new InvalidOperationException("BuildWorkbook was not found.");
    var parameters = method.GetParameters();
    var period = Activator.CreateInstance(parameters[0].ParameterType) ?? throw new InvalidOperationException("Could not create period.");
    Set(period, "Id", 42L); Set(period, "Rfc", "OHM191112Q26"); Set(period, "PayGroupId", 1);
    Set(period, "FromDate", new DateOnly(2026, 8, 1)); Set(period, "ToDate", new DateOnly(2026, 8, 7)); Set(period, "Version", 1);
    IReadOnlyList<PrenominaLineDto> lines =
    [
      new() { EmployeeId = 33, EmployeeName = "Empleado de prueba", ScheduledMinutes = 2400, WorkedMinutes = 2380, OvertimeApprovedMinutes = 30 }
    ];
    static object EmptyList(Type parameterType) => Activator.CreateInstance(typeof(List<>).MakeGenericType(parameterType.GetGenericArguments()[0]))!;
    var bytes = (byte[])(method.Invoke(null, [period, lines, EmptyList(parameters[2].ParameterType), EmptyList(parameters[3].ParameterType), EmptyList(parameters[4].ParameterType)])
      ?? throw new InvalidOperationException("Workbook generation returned no data."));

    using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
    var workbookEntry = archive.GetEntry("xl/workbook.xml") ?? throw new InvalidOperationException("Workbook manifest is missing.");
    using var reader = new StreamReader(workbookEntry.Open());
    var workbookXml = reader.ReadToEnd();
    foreach (var sheet in new[] { "Resumen", "Detalle", "HorasExtra", "Incidencias", "Ausencias", "Validaciones" })
      Assert.Contains($"name=\"{sheet}\"", workbookXml, StringComparison.Ordinal);
  }

  private static string Read(params string[] parts)
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrionERP.sln"))) directory = directory.Parent;
    if (directory is null) throw new InvalidOperationException("Could not locate repository root.");
    return File.ReadAllText(Path.Combine([directory.FullName, .. parts]));
  }

  private static void Set(object target, string property, object value)
    => target.GetType().GetProperty(property)?.SetValue(target, value);
}
