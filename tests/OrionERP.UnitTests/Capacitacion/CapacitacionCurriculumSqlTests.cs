using System.Text.RegularExpressions;
using OrionERP.Web.Shared;

namespace OrionERP.UnitTests.Capacitacion;

public sealed class CapacitacionCurriculumSqlTests
{
  private const string PilotSeedPath =
    "src/OrionERP.Infrastructure/Features/Capacitacion/Sql/20260817_capacitacion_v1.sql";
  private const string CurriculumPath =
    "src/OrionERP.Infrastructure/Features/Capacitacion/Sql/20260819_capacitacion_curriculum_v2.sql";

  private static readonly string PilotSql = ReadRepoFile(PilotSeedPath);
  private static readonly string CurriculumSql = ReadRepoFile(CurriculumPath);

  private static readonly string[] PilotCourses =
  [
    "ORION-FUNDAMENTOS", "RES-END-TO-END", "CFDI-CONTABILIDAD", "LOGISTICA-OPERACION", "RH-CAPITAL-HUMANO"
  ];

  [Fact]
  public void Curriculum_GuardsTheCatalogAndRequiresTheReviewedPilotSeed()
  {
    var guard = CurriculumSql.IndexOf("DB_NAME() <> @ExpectedDatabase", StringComparison.Ordinal);
    var pilotGuard = CurriculumSql.IndexOf("N'ORION-FUNDAMENTOS'", StringComparison.Ordinal);
    var firstWrite = CurriculumSql.IndexOf("INSERT INTO capacitacion.Curso", StringComparison.Ordinal);

    Assert.True(guard >= 0);
    Assert.True(pilotGuard > guard);
    Assert.True(firstWrite > pilotGuard);
    Assert.Contains(":setvar ExpectedDatabase \"Orion_Training\"", CurriculumSql, StringComparison.Ordinal);
    Assert.Contains("Orion_Sandbox", CurriculumSql, StringComparison.Ordinal);
    Assert.Contains("grupocarpio", CurriculumSql, StringComparison.Ordinal);

    // The published-content triggers make authored rows immutable, so the script
    // must author into a draft version and publish only at the end.
    var draftVersion = CurriculumSql.IndexOf("1, N'BORRADOR'", StringComparison.Ordinal);
    var lessonAuthoring = CurriculumSql.IndexOf("INSERT INTO capacitacion.Leccion", StringComparison.Ordinal);
    var practiceAuthoring = CurriculumSql.IndexOf("INSERT INTO capacitacion.PracticaPaso", StringComparison.Ordinal);
    var publishVersion = CurriculumSql.IndexOf("SET Estado = N'PUBLICADA'", StringComparison.Ordinal);

    Assert.True(draftVersion >= 0);
    Assert.True(lessonAuthoring > draftVersion);
    Assert.True(practiceAuthoring > lessonAuthoring);
    Assert.True(publishVersion > practiceAuthoring);
  }

  [Fact]
  public void Curriculum_AuthorsEveryCourseWithLessonsBlocksAssessmentAndPractice()
  {
    var courses = SeededCourses();
    Assert.Equal(courses.Length, courses.Distinct(StringComparer.Ordinal).Count());
    Assert.NotEmpty(courses);
    Assert.Empty(courses.Intersect(PilotCourses, StringComparer.Ordinal));

    foreach (var section in new[] { "@Lecciones", "@Bloques", "@Evaluaciones", "@Preguntas", "@Practicas", "@Pasos" })
    {
      var keys = KeysOf(section);
      Assert.Empty(courses.Except(keys, StringComparer.Ordinal));
      Assert.Empty(keys.Except(courses, StringComparer.Ordinal));
    }

    // Every course closes with a graded assessment and a hands-on practice.
    Assert.All(courses, course => Assert.Equal(4, KeyRowCount("@Preguntas", course)));
    Assert.All(courses, course => Assert.Equal(4, KeyRowCount("@Pasos", course)));
    Assert.All(courses, course => Assert.Equal(3, KeyRowCount("@Lecciones", course)));
  }

  [Fact]
  public void Curriculum_CoversEveryNavigationDestinationOfOrionErp()
  {
    var seeds = PilotSql + CurriculumSql;
    var destinations = NavigationCatalog.Sections
      .SelectMany(section => section.Items)
      .Concat(NavigationCatalog.AdminSection.Items)
      .Select(item => item.Href)
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToArray();

    var uncovered = destinations
      .Where(href => !seeds.Contains($"N'{href}'", StringComparison.OrdinalIgnoreCase))
      .ToArray();

    Assert.Empty(uncovered);

    // Screens that are reachable outside the navigation menu are covered too.
    foreach (var route in new[] { "/asistencia/kiosco", "/capacitacion/admin", "/capacitacion/sesiones/nueva" })
      Assert.Contains($"N'{route}'", CurriculumSql, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Curriculum_LearningPathOrdersThePilotAndModuleCoursesExactlyOnce()
  {
    var body = Section("INSERT INTO @RutaOrden (", "INSERT INTO capacitacion.RutaCurso (");
    var entries = Regex.Matches(body, @"(?m)^\s*\((?<order>\d+), N'(?<key>[A-Z0-9\-]+)'\)")
      .Select(match => (Order: int.Parse(match.Groups["order"].Value), Key: match.Groups["key"].Value))
      .ToArray();

    var expected = PilotCourses.Concat(SeededCourses()).ToArray();
    Assert.Equal(expected.Length, entries.Length);
    Assert.Empty(expected.Except(entries.Select(entry => entry.Key), StringComparer.Ordinal));
    Assert.Equal(Enumerable.Range(1, entries.Length), entries.Select(entry => entry.Order).Order());
    Assert.Equal("ORION-FUNDAMENTOS", entries.Single(entry => entry.Order == 1).Key);
    Assert.Contains("N'ORION-EXPERTO'", CurriculumSql, StringComparison.Ordinal);
  }

  [Fact]
  public void Curriculum_UsesLocalTrainingAssetsAndNoExternalTargets()
  {
    var routes = Regex.Matches(CurriculumSql, @"N'(?<route>/[^']*)'")
      .Select(match => match.Groups["route"].Value)
      .ToArray();

    Assert.NotEmpty(routes);
    Assert.All(routes, route => Assert.StartsWith("/", route, StringComparison.Ordinal));
    Assert.DoesNotContain("N'http:", CurriculumSql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("N'https:", CurriculumSql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("N'//", CurriculumSql, StringComparison.Ordinal);
  }

  private static string[] SeededCourses()
  {
    var body = Section("INSERT INTO @Cursos (", "INSERT INTO capacitacion.Curso (");
    return Regex.Matches(body, @"(?m)^\s*\(N'(?<key>[A-Z0-9\-]+)', N'")
      .Select(match => match.Groups["key"].Value)
      .ToArray();
  }

  private static string[] KeysOf(string tableVariable)
  {
    var body = SectionOf(tableVariable);
    return Regex.Matches(body, @"(?m)^\s*\(N'(?<key>[A-Z0-9\-]+)'")
      .Select(match => match.Groups["key"].Value)
      .Distinct(StringComparer.Ordinal)
      .ToArray();
  }

  private static int KeyRowCount(string tableVariable, string course)
  {
    var body = SectionOf(tableVariable);
    return Regex.Matches(body, $@"(?m)^\s*\(N'{Regex.Escape(course)}',").Count;
  }

  private static string SectionOf(string tableVariable) => tableVariable switch
  {
    "@Lecciones" => Section("INSERT INTO @Lecciones (", "INSERT INTO capacitacion.Leccion ("),
    "@Bloques" => Section("INSERT INTO @Bloques (", "INSERT INTO capacitacion.BloqueContenido ("),
    "@Evaluaciones" => Section("INSERT INTO @Evaluaciones (", "INSERT INTO capacitacion.Evaluacion ("),
    "@Preguntas" => Section("INSERT INTO @Preguntas (", "INSERT INTO capacitacion.Pregunta ("),
    "@Practicas" => Section("INSERT INTO @Practicas (", "INSERT INTO capacitacion.Practica ("),
    "@Pasos" => Section("INSERT INTO @Pasos (", "INSERT INTO capacitacion.PracticaPaso ("),
    _ => throw new ArgumentOutOfRangeException(nameof(tableVariable), tableVariable, null)
  };

  private static string Section(string start, string end)
  {
    var from = CurriculumSql.IndexOf(start, StringComparison.Ordinal);
    Assert.True(from >= 0, $"No se encontró {start} en el currículo.");
    var to = CurriculumSql.IndexOf(end, from, StringComparison.Ordinal);
    Assert.True(to > from, $"No se encontró {end} después de {start}.");
    return CurriculumSql[from..to];
  }

  private static string ReadRepoFile(string relativePath)
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
      var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
      if (File.Exists(candidate)) return File.ReadAllText(candidate);
      directory = directory.Parent;
    }

    throw new FileNotFoundException($"No se encontró {relativePath} desde {AppContext.BaseDirectory}.");
  }
}
