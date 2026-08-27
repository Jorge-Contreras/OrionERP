using OrionERP.Application.Features.Capacitacion;
using OrionERP.Application.Features.CapitalHumano.Workforce;
using OrionERP.Infrastructure.Features.Capacitacion;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Capacitacion;

public sealed class CapacitacionCourseCrudTests
{
  private const string Rfc = "OHM191112Q26";

  [Fact]
  public async Task CourseManagement_RequiresInstructorOrAdminBeforeSql()
  {
    var connection = new FakeQueryDbConnection();
    var service = NewService(connection, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetCursosAdministrablesAsync(Rfc));
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GuardarCursoAsync(new CapacitacionGuardarCursoRequest
    {
      Rfc = Rfc,
      ActorEmployeeId = 12,
      Actor = "spoofed@example.test",
      Clave = "COURSE",
      Categoria = "Test",
      Nombre = "Course",
      Descripcion = "Course description",
      Objetivos = "Course objectives"
    }));

    Assert.Empty(connection.ExecutedCommands);
  }

  [Fact]
  public async Task ManageableCourses_AreScopedToCompanyAndUnshadowedGlobalCatalog()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = static (_, _) => 1
    };
    var service = NewService(connection, new HashSet<string>([CapacitacionCodes.RoleInstructor], StringComparer.OrdinalIgnoreCase));

    var courses = await service.GetCursosAdministrablesAsync(Rfc);

    Assert.Empty(courses);
    var query = connection.ExecutedCommands.Last();
    Assert.Contains("c.Rfc = @Rfc", query.CommandText, StringComparison.Ordinal);
    Assert.Contains("c.Rfc = '*'", query.CommandText, StringComparison.Ordinal);
    Assert.Contains("companyCourse.Rfc = @Rfc AND companyCourse.Clave = c.Clave", query.CommandText, StringComparison.Ordinal);
    Assert.Contains("WHEN 'BORRADOR' THEN 0", query.CommandText, StringComparison.Ordinal);
    Assert.Contains(query.Parameters, parameter =>
      string.Equals(parameter.Name, "Rfc", StringComparison.OrdinalIgnoreCase)
      && string.Equals(Convert.ToString(parameter.Value), Rfc, StringComparison.Ordinal));
  }

  [Fact]
  public void CourseAuthoring_ClonesTheCompleteCurriculumAndUsesSoftDelete()
  {
    var source = ReadRepoFile("src/OrionERP.Infrastructure/Features/Capacitacion/CapacitacionService.CourseAuthoring.cs");

    foreach (var table in new[]
    {
      "capacitacion.Leccion",
      "capacitacion.BloqueContenido",
      "capacitacion.Recurso",
      "capacitacion.Evaluacion",
      "capacitacion.Pregunta",
      "capacitacion.OpcionPregunta",
      "capacitacion.Practica",
      "capacitacion.PracticaPaso"
    })
      Assert.Contains($"INSERT INTO {table}", source, StringComparison.Ordinal);

    Assert.Contains("SET Estado = 'RETIRADA'", source, StringComparison.Ordinal);
    Assert.Contains("SET Estado = 'PUBLICADA'", source, StringComparison.Ordinal);
    Assert.Contains("SET Activo = @Activo", source, StringComparison.Ordinal);
    Assert.DoesNotContain("DELETE FROM capacitacion.Curso\n", source, StringComparison.Ordinal);
    Assert.DoesNotContain("DELETE FROM capacitacion.Curso\r\n", source, StringComparison.Ordinal);
  }

  private static CapacitacionService NewService(FakeQueryDbConnection connection, IReadOnlySet<string> roles)
    => new(
      new FakeQueryConnectionFactory(connection),
      new StubAccessor(new CurrentEmployeeContext(
        "authenticated@orionerp.local",
        12,
        roles,
        Rfc)));

  private static string ReadRepoFile(string relativePath)
  {
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null && !File.Exists(Path.Combine(current.FullName, "OrionERP.sln"))) current = current.Parent;
    Assert.NotNull(current);
    return File.ReadAllText(Path.Combine(current!.FullName, relativePath));
  }

  private sealed class StubAccessor : ICurrentEmployeeAccessor
  {
    private readonly CurrentEmployeeContext _context;

    public StubAccessor(CurrentEmployeeContext context)
    {
      _context = context;
    }

    public ValueTask<CurrentEmployeeContext?> GetCurrentAsync(CancellationToken ct = default)
      => ValueTask.FromResult<CurrentEmployeeContext?>(_context);
  }
}
