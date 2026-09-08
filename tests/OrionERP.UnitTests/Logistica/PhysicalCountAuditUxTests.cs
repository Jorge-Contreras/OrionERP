namespace OrionERP.UnitTests.Logistica;

public class PhysicalCountAuditUxTests
{
  [Fact]
  public void Page_ShowsWhoCountedAndWhenAcrossTheWholeLifecycle()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Logistica/PhysicalCounts/ConteosFisicosPage.razor");
    var codeBehind = ReadRepoFile("src/OrionERP.Web/Features/Logistica/PhysicalCounts/ConteosFisicosPage.razor.cs");

    Assert.Contains("Bitácora del conteo", page, StringComparison.Ordinal);
    Assert.Contains("Inicio del conteo", page, StringComparison.Ordinal);
    Assert.Contains("FirstCaptureEvent.OccurredAt", page, StringComparison.Ordinal);
    Assert.Contains("Personas que contaron", page, StringComparison.Ordinal);
    Assert.Contains("GetAuditActor(auditEvent.PerformedBy)", page, StringComparison.Ordinal);
    Assert.Contains("FormatAuditTime(auditEvent.OccurredAt)", page, StringComparison.Ordinal);
    Assert.Contains("PhysicalCountAuditEventTypes.LineCounted", codeBehind, StringComparison.Ordinal);
    Assert.Contains("FirstCaptureEvent", codeBehind, StringComparison.Ordinal);
    Assert.Contains("DateTime.SpecifyKind(value, DateTimeKind.Utc)", codeBehind, StringComparison.Ordinal);

    // Un conteo por material toca el mismo material una vez por ubicación: sin el lugar, la
    // bitácora repite la misma línea y nadie puede distinguir una parada de otra.
    Assert.Contains("auditEvent.LocationName", codeBehind, StringComparison.Ordinal);
  }

  [Fact]
  public void Page_KeepsLongAuditTrailsReadableOnDesktopAndMobile()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Logistica/PhysicalCounts/ConteosFisicosPage.razor");
    var styles = ReadRepoFile("src/OrionERP.Web/Features/Logistica/PhysicalCounts/ConteosFisicosPage.razor.css");

    Assert.Contains("VisibleAuditEvents", page, StringComparison.Ordinal);
    Assert.Contains("Ver toda la bitácora", page, StringComparison.Ordinal);
    Assert.Contains(".conteos-audit-event", styles, StringComparison.Ordinal);
    Assert.Contains(".conteos-audit-summary", styles, StringComparison.Ordinal);
    Assert.Contains("grid-template-columns: 1fr;", styles, StringComparison.Ordinal);
  }

  private static string ReadRepoFile(string relativePath)
  {
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null && !File.Exists(Path.Combine(current.FullName, "OrionERP.sln")))
    {
      current = current.Parent;
    }

    Assert.NotNull(current);
    return File.ReadAllText(Path.Combine(current!.FullName, relativePath));
  }
}
