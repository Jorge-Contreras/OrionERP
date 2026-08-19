using System.Xml.Linq;

namespace OrionERP.UnitTests.Capacitacion;

public sealed class TrainingScenarioFixtureTests
{
  private static readonly string ScenarioSql = ReadRepoFile(
    "src/OrionERP.Infrastructure/Features/Capacitacion/Sql/20260817_orion_training_scenarios.sql");
  private static readonly string CourseSql = ReadRepoFile(
    "src/OrionERP.Infrastructure/Features/Capacitacion/Sql/20260817_capacitacion_v1.sql");
  private static readonly string Sanitizer = ReadRepoFile("Sanitize-OrionTraining.ps1");
  private static readonly string AttestationSql = ReadRepoFile(
    "src/OrionERP.Infrastructure/Features/Capacitacion/Sql/20260817_orion_training_attest.sql");
  private static readonly string FixturePath = GetRepoFile(
    "src/OrionERP.Web/wwwroot/training/fixtures/cfdi-ficticio-no-timbrable.xml");

  [Fact]
  public void WorkforceScenario_IsGuardedSyntheticAndAddsNoPrivilegedCapability()
  {
    var catalogGuard = ScenarioSql.IndexOf(
      "DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'Orion_Training'",
      StringComparison.Ordinal);
    var sessionGuard = ScenarioSql.IndexOf(
      "SESSION_CONTEXT(N'OrionTrainingSanitizerApply')",
      StringComparison.Ordinal);
    var firstScenarioWrite = ScenarioSql.IndexOf("INSERT rh.WorkSite", StringComparison.Ordinal);

    Assert.InRange(catalogGuard, 0, sessionGuard - 1);
    Assert.InRange(sessionGuard, catalogGuard + 1, firstScenarioWrite - 1);
    Assert.Contains("the RH schema is not empty after sanitization", ScenarioSql, StringComparison.Ordinal);
    Assert.Contains("@TrainingRfc varchar(50) = 'XAXX010101000'", ScenarioSql, StringComparison.Ordinal);
    Assert.Contains("EmployeeId NOT IN (990002, 990003)", ScenarioSql, StringComparison.Ordinal);
    Assert.Contains("LocationRequired = 0", ScenarioSql, StringComparison.Ordinal);
    Assert.Contains("(SELECT COUNT(*) FROM rh.EmployeeWorkAssignment) <> 2", ScenarioSql, StringComparison.Ordinal);
    Assert.Contains("(SELECT COUNT(*) FROM rh.LeaveEnrollment) <> 4", ScenarioSql, StringComparison.Ordinal);
    Assert.Contains("(SELECT COUNT(*) FROM rh.LeaveBalanceLedger) <> 2", ScenarioSql, StringComparison.Ordinal);
    Assert.Contains("FROM (VALUES (990002), (990003)) expected(EmployeeId)", ScenarioSql, StringComparison.Ordinal);
    Assert.Contains("CROSS JOIN (VALUES (@PersonalLeavePolicyId), (@UnpaidLeavePolicyId))", ScenarioSql, StringComparison.Ordinal);
    Assert.Contains("StartTime <> CONVERT(time(0), '09:00:00')", ScenarioSql, StringComparison.Ordinal);
    Assert.Contains("EndTime <> CONVERT(time(0), '17:00:00')", ScenarioSql, StringComparison.Ordinal);
    Assert.Contains("@AllowedNonEmptyRhTables", ScenarioSql, StringComparison.Ordinal);

    Assert.DoesNotContain("INSERT auth.", ScenarioSql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("INSERT rh.SupervisorAssignment", ScenarioSql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("INSERT rh.EmployeeKioskCredential", ScenarioSql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("INSERT rh.TimeEvent", ScenarioSql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("CapitalHumanoAdmin", ScenarioSql, StringComparison.Ordinal);
    Assert.DoesNotContain("CapitalHumanoSupervisor", ScenarioSql, StringComparison.Ordinal);
    Assert.DoesNotContain("Administrador", ScenarioSql, StringComparison.Ordinal);
    Assert.DoesNotContain("CREATE LOGIN", ScenarioSql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("USE [", ScenarioSql, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void CfdiFixture_IsObviouslyFictionalStructurallyReadableAndCryptographicallyInvalid()
  {
    var xml = File.ReadAllText(FixturePath);
    var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
    var root = Assert.IsType<XElement>(document.Root);
    XNamespace cfdi = "http://www.sat.gob.mx/cfd/4";
    XNamespace tfd = "http://www.sat.gob.mx/TimbreFiscalDigital";
    XNamespace training = "urn:orionerp:training-only";

    Assert.Equal(cfdi + "Comprobante", root.Name);
    Assert.Equal("true", root.Attribute(training + "Ficticio")?.Value);
    Assert.Equal("true", root.Attribute(training + "NoValidoFiscal")?.Value);
    Assert.Equal("TRN", root.Attribute("Serie")?.Value);
    Assert.Equal("NO-TIMBRABLE-001", root.Attribute("Folio")?.Value);
    Assert.Equal("NO_VALIDO_ENTRENAMIENTO", root.Attribute("Sello")?.Value);
    Assert.Equal("NO_VALIDO_ENTRENAMIENTO", root.Attribute("Certificado")?.Value);
    Assert.Equal("00000000000000000000", root.Attribute("NoCertificado")?.Value);

    var stamp = Assert.Single(root.Descendants(tfd + "TimbreFiscalDigital"));
    Assert.Equal("true", stamp.Attribute(training + "Ficticio")?.Value);
    Assert.Equal("00000000-0000-4000-8000-000000000001", stamp.Attribute("UUID")?.Value);
    Assert.Equal("NO_VALIDO_ENTRENAMIENTO", stamp.Attribute("SelloCFD")?.Value);
    Assert.Equal("NO_VALIDO_ENTRENAMIENTO", stamp.Attribute("SelloSAT")?.Value);
    Assert.Equal("00000000000000000000", stamp.Attribute("NoCertificadoSAT")?.Value);

    var rfcs = root.Descendants()
      .Attributes("Rfc")
      .Select(attribute => attribute.Value)
      .ToArray();
    Assert.NotEmpty(rfcs);
    Assert.All(rfcs, rfc => Assert.Equal("XAXX010101000", rfc));
    Assert.Contains("EXCLUSIVO PARA ORION_TRAINING", xml, StringComparison.Ordinal);
    Assert.Contains("SIN VALIDEZ FISCAL Y NO TIMBRABLE", xml, StringComparison.Ordinal);
    Assert.DoesNotContain("PRIVATE KEY", xml, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("BEGIN CERTIFICATE", xml, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void CfdiCourse_LinksTheLocalFixtureAndStartsPracticeAtTheLocalUploadFlow()
  {
    Assert.Contains(
      "N'/training/fixtures/cfdi-ficticio-no-timbrable.xml'",
      CourseSql,
      StringComparison.Ordinal);
    Assert.Contains("N'ARCHIVO'", CourseSql, StringComparison.Ordinal);
    Assert.Contains("N'/cfdi/cargar-xml-sat'", CourseSql, StringComparison.Ordinal);
    Assert.Contains("NO_VALIDO_ENTRENAMIENTO", CourseSql, StringComparison.Ordinal);
    Assert.Contains("cárgalo únicamente en Orion_Training", CourseSql, StringComparison.Ordinal);
    Assert.Contains("no guardes pólizas ni movimientos bancarios", CourseSql, StringComparison.Ordinal);
    Assert.Contains("Gasto propuesto: 1000.00 al debe", CourseSql, StringComparison.Ordinal);
    Assert.Contains("Contraparte propuesta: 1160.00 al haber", CourseSql, StringComparison.Ordinal);
    Assert.DoesNotContain("registra una póliza balanceada", CourseSql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("Vincula el movimiento bancario", CourseSql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("http://", CourseSql[CourseSql.IndexOf("DECLARE @SeedActor", StringComparison.Ordinal)..], StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("https://", CourseSql[CourseSql.IndexOf("DECLARE @SeedActor", StringComparison.Ordinal)..], StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Sanitizer_InvokesScenariosAfterCohortProvisionAndAttestationReviewsEveryRhTable()
  {
    const string scenarioFile = "20260817_orion_training_scenarios.sql";
    var provisionCall = Sanitizer.IndexOf(
      "Invoke-SqlFile -Connection $connection -Path $provisionScript",
      StringComparison.Ordinal);
    var scenarioCall = Sanitizer.IndexOf(
      "Invoke-SqlFile -Connection $connection -Path $scenarioScript",
      StringComparison.Ordinal);
    var attestationCall = Sanitizer.IndexOf(
      "Invoke-SqlFile -Connection $connection -Path $attestScript",
      StringComparison.Ordinal);

    Assert.Contains(scenarioFile, Sanitizer, StringComparison.Ordinal);
    Assert.InRange(scenarioCall, provisionCall + 1, attestationCall - 1);

    string[] reviewedTables =
    [
      "WorkSite", "ScheduleTemplate", "ScheduleDay", "AttendancePolicy", "PayGroup",
      "EmployeeWorkAssignment", "LeaveType", "LeavePolicy", "LeaveEnrollment",
      "LeaveBalanceLedger", "PrivacyNotice"
    ];
    foreach (var table in reviewedTables)
      Assert.Contains($"N'{table}'", AttestationSql, StringComparison.Ordinal);

    Assert.Contains("TRN-ASISTENCIA", AttestationSql, StringComparison.Ordinal);
    Assert.Contains("TRN-PERSONAL", AttestationSql, StringComparison.Ordinal);
    Assert.Contains(
      "dayInfo.StartTime IS NULL OR dayInfo.StartTime <> CONVERT(time(0), '09:00:00')",
      AttestationSql,
      StringComparison.Ordinal);
    Assert.Contains(
      "dayInfo.EndTime IS NULL OR dayInfo.EndTime <> CONVERT(time(0), '17:00:00')",
      AttestationSql,
      StringComparison.Ordinal);
    Assert.Contains(
      "FROM (VALUES (990002), (990003)) expected(EmployeeId)",
      AttestationSql,
      StringComparison.Ordinal);
    Assert.Contains(
      "CROSS JOIN (VALUES ('TRN-PERSONAL-POL'), ('TRN-SIN-GOCE-POL')) expectedPolicy(Code)",
      AttestationSql,
      StringComparison.Ordinal);
    Assert.Contains(
      "typeInfo.Code <> CASE policyInfo.Code",
      AttestationSql,
      StringComparison.Ordinal);
    Assert.Contains(
      "WHEN 990002 THEN 'training:initial:trainee01'",
      AttestationSql,
      StringComparison.Ordinal);
    Assert.Contains(
      "WHEN 990003 THEN 'training:initial:trainee02'",
      AttestationSql,
      StringComparison.Ordinal);
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(GetRepoFile(relativePath));

  private static string GetRepoFile(string relativePath)
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
      var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
      if (File.Exists(candidate)) return candidate;
      directory = directory.Parent;
    }

    throw new FileNotFoundException($"No se encontró {relativePath} desde {AppContext.BaseDirectory}.");
  }
}
