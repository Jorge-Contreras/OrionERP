namespace OrionERP.UnitTests.Capacitacion;

public sealed class CapacitacionSqlTests
{
  private static readonly string Sql = File.ReadAllText(GetRepoFile(
    "src/OrionERP.Infrastructure/Features/Capacitacion/Sql/20260817_capacitacion_v1.sql"));

  [Fact]
  public void Migration_HasSqlCmdDatabaseGuardBeforeSchemaMutation()
  {
    var guard = Sql.IndexOf("DB_NAME() <> @ExpectedDatabase", StringComparison.Ordinal);
    var schema = Sql.IndexOf("CREATE SCHEMA capacitacion", StringComparison.Ordinal);

    Assert.True(guard >= 0);
    Assert.True(schema > guard);
    Assert.Contains(":setvar ExpectedDatabase \"Orion_Training\"", Sql, StringComparison.Ordinal);
    Assert.Contains("Orion_Sandbox", Sql, StringComparison.Ordinal);
    Assert.Contains("grupocarpio", Sql, StringComparison.Ordinal);
    Assert.Contains("-f 65001", Sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Migration_ModelsVersionedCurriculumAndRfcScopedExecution()
  {
    string[] authoringTables =
    [
      "capacitacion.Curso", "capacitacion.CursoVersion", "capacitacion.Leccion",
      "capacitacion.BloqueContenido", "capacitacion.Recurso", "capacitacion.Evaluacion",
      "capacitacion.Pregunta", "capacitacion.OpcionPregunta", "capacitacion.Practica",
      "capacitacion.PracticaPaso", "capacitacion.RutaAprendizaje", "capacitacion.RutaCurso"
    ];
    string[] executionTables =
    [
      "capacitacion.Asignacion", "capacitacion.Sesion", "capacitacion.SesionParticipante",
      "capacitacion.ProgresoBloque", "capacitacion.IntentoEvaluacion",
      "capacitacion.ResultadoPractico", "capacitacion.FirmaInstructor",
      "capacitacion.Finalizacion", "capacitacion.EventoAuditoria"
    ];

    foreach (var table in authoringTables.Concat(executionTables))
      Assert.Contains(table, Sql, StringComparison.Ordinal);

    Assert.Contains("AsignacionId bigint", Sql, StringComparison.Ordinal);
    Assert.Contains("Rfc nvarchar(50) NOT NULL", Sql, StringComparison.Ordinal);
    Assert.Contains("EmployeeId int NOT NULL", Sql, StringComparison.Ordinal);
    Assert.Contains("CursoVersionId int NOT NULL", Sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Migration_EnforcesPublishedContentAndCompletionImmutability()
  {
    Assert.Contains("TR_CursoVersion_PublicadaInmutable", Sql, StringComparison.Ordinal);
    Assert.Contains("newRow.Estado <> N'RETIRADA'", Sql, StringComparison.Ordinal);
    Assert.Contains("versionInfo.PublicadaEn IS NOT NULL", Sql, StringComparison.Ordinal);
    Assert.Contains("TR_Leccion_VersionPublicadaInmutable", Sql, StringComparison.Ordinal);
    Assert.Contains("TR_BloqueContenido_VersionPublicadaInmutable", Sql, StringComparison.Ordinal);
    Assert.Contains("TR_Recurso_VersionPublicadaInmutable", Sql, StringComparison.Ordinal);
    Assert.Contains("TR_Evaluacion_VersionPublicadaInmutable", Sql, StringComparison.Ordinal);
    Assert.Contains("TR_Pregunta_VersionPublicadaInmutable", Sql, StringComparison.Ordinal);
    Assert.Contains("TR_OpcionPregunta_VersionPublicadaInmutable", Sql, StringComparison.Ordinal);
    Assert.Contains("TR_Practica_VersionPublicadaInmutable", Sql, StringComparison.Ordinal);
    Assert.Contains("TR_PracticaPaso_VersionPublicadaInmutable", Sql, StringComparison.Ordinal);
    Assert.Contains("TR_Finalizacion_AppendOnly", Sql, StringComparison.Ordinal);
    Assert.Contains("TR_EventoAuditoria_AppendOnly", Sql, StringComparison.Ordinal);
    Assert.Contains("INSTEAD OF UPDATE, DELETE", Sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Migration_SeedsFiveSpanishPilotCoursesWithTrainerAidsAndRealRoutes()
  {
    var courseSeedStart = Sql.IndexOf("INSERT INTO @Cursos", StringComparison.Ordinal);
    var courseSeedEnd = Sql.IndexOf("INSERT INTO capacitacion.Curso", courseSeedStart, StringComparison.Ordinal);
    var courseSeed = Sql[courseSeedStart..courseSeedEnd];
    var seededKeys = System.Text.RegularExpressions.Regex.Matches(
        courseSeed,
        @"(?m)^\s*\(N'(?<key>[^']+)'", 
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)
      .Select(match => match.Groups["key"].Value)
      .ToArray();

    Assert.Equal(
      ["ORION-FUNDAMENTOS", "RES-END-TO-END", "CFDI-CONTABILIDAD", "LOGISTICA-OPERACION", "RH-CAPITAL-HUMANO"],
      seededKeys);
    Assert.Contains("Fundamentos de OrionERP", Sql, StringComparison.Ordinal);
    Assert.Contains("Reservaciones de principio a fin", Sql, StringComparison.Ordinal);
    Assert.Contains("Del CFDI a la contabilidad", Sql, StringComparison.Ordinal);
    Assert.Contains("Logística: materiales, compras e inventario", Sql, StringComparison.Ordinal);
    Assert.Contains("Capital Humano: autoservicio del colaborador", Sql, StringComparison.Ordinal);
    Assert.Contains("notasInstructor", Sql, StringComparison.Ordinal);
    Assert.Contains("N'PRACTICA'", Sql, StringComparison.Ordinal);
    Assert.Contains("N'EVALUACION'", Sql, StringComparison.Ordinal);
    Assert.Contains("N'/reservaciones/lista'", Sql, StringComparison.Ordinal);
    Assert.Contains("N'/cfdi/cargar-xml-sat'", Sql, StringComparison.Ordinal);
    Assert.Contains("N'/training/fixtures/cfdi-ficticio-no-timbrable.xml'", Sql, StringComparison.Ordinal);
    Assert.Contains("N'/logistica/materiales'", Sql, StringComparison.Ordinal);
    Assert.Contains("N'/mi-trabajo'", Sql, StringComparison.Ordinal);
    Assert.Contains("N'ENLACE'", Sql, StringComparison.Ordinal);
    Assert.Contains("/Images/OrionERPMainPage.png", Sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Migration_NewModuleCoursesUseSixStepFlowAndPublishAfterAuthoring()
  {
    string[] flowSteps = ["Preparar", "Explicar", "Demostrar", "Practicar", "Evaluar", "Cerrar"];
    foreach (var step in flowSteps)
      Assert.Equal(2, CountOccurrences(Sql, $"N'{step}:"));

    Assert.Contains("\"diagram\":[\"Material\",\"Proveedor\",\"Compra\",\"Recepción\",\"Ubicación\",\"Conteo\"]", Sql, StringComparison.Ordinal);
    Assert.Contains("\"diagram\":[\"Mi identidad\",\"Mi asistencia\",\"Mi corrección\",\"Mi ausencia\",\"Seguimiento\"]", Sql, StringComparison.Ordinal);
    Assert.Contains("código inicia con TRN", Sql, StringComparison.Ordinal);
    Assert.Contains("sin usar funciones administrativas", Sql, StringComparison.Ordinal);

    var draftVersion = Sql.IndexOf("SELECT curso.CursoId, 1, N'BORRADOR'", StringComparison.Ordinal);
    var lessonAuthoring = Sql.IndexOf("INSERT INTO capacitacion.Leccion", StringComparison.Ordinal);
    var publishVersion = Sql.IndexOf("SET Estado = N'PUBLICADA'", lessonAuthoring, StringComparison.Ordinal);
    Assert.True(draftVersion >= 0);
    Assert.True(lessonAuthoring > draftVersion);
    Assert.True(publishVersion > lessonAuthoring);
  }

  private static int CountOccurrences(string value, string fragment)
  {
    var count = 0;
    var offset = 0;
    while ((offset = value.IndexOf(fragment, offset, StringComparison.Ordinal)) >= 0)
    {
      count++;
      offset += fragment.Length;
    }

    return count;
  }

  private static string GetRepoFile(string relativePath)
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
      var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
      if (File.Exists(candidate))
        return candidate;
      directory = directory.Parent;
    }

    throw new FileNotFoundException($"No se encontró {relativePath} desde {AppContext.BaseDirectory}.");
  }
}
