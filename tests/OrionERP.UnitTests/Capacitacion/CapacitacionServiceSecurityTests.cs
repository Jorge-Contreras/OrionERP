using OrionERP.Application.Features.Capacitacion;
using OrionERP.Application.Features.CapitalHumano.Workforce;
using OrionERP.Infrastructure.Features.Capacitacion;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Capacitacion;

public sealed class CapacitacionServiceSecurityTests
{
  private const string Rfc = "OHM191112Q26";

  [Fact]
  public async Task Catalogo_RejectsAnonymousCallerBeforeSql()
  {
    var connection = new FakeQueryDbConnection();
    var service = new CapacitacionService(
      new FakeQueryConnectionFactory(connection),
      new StubCurrentEmployeeAccessor(null));

    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetCatalogoAsync(Rfc));

    Assert.Empty(connection.ExecutedCommands);
  }

  [Fact]
  public async Task Catalogo_RejectsRfcOutsideAuthenticatedClaimsBeforeSql()
  {
    var connection = new FakeQueryDbConnection();
    var actor = Actor(employeeId: 12, companyRfc: "OTHER010101AAA");
    var service = new CapacitacionService(
      new FakeQueryConnectionFactory(connection),
      new StubCurrentEmployeeAccessor(actor));

    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetCatalogoAsync(Rfc));

    Assert.Empty(connection.ExecutedCommands);
  }

  [Fact]
  public async Task Catalogo_FiltersPublishedCoursesToExactOrGlobalRfc()
  {
    var connection = new FakeQueryDbConnection();
    var service = new CapacitacionService(
      new FakeQueryConnectionFactory(connection),
      new StubCurrentEmployeeAccessor(Actor(employeeId: 12)));

    var courses = await service.GetCatalogoAsync(Rfc);

    Assert.Empty(courses);
    var query = Assert.Single(connection.ExecutedCommands);
    Assert.Contains("c.Rfc IN (@Rfc, '*')", query.CommandText, StringComparison.Ordinal);
    Assert.Contains("cv.Estado = 'PUBLICADA'", query.CommandText, StringComparison.Ordinal);
    Assert.Contains(query.Parameters, parameter =>
      string.Equals(parameter.Name, "Rfc", StringComparison.OrdinalIgnoreCase)
      && string.Equals(Convert.ToString(parameter.Value), Rfc, StringComparison.Ordinal));
  }

  [Fact]
  public async Task CursoPublico_FiltersOutDraftAndInactiveCurriculum()
  {
    var connection = new FakeQueryDbConnection();
    var service = new CapacitacionService(
      new FakeQueryConnectionFactory(connection),
      new StubCurrentEmployeeAccessor(Actor(employeeId: 12)));

    var course = await service.GetCursoAsync(cursoVersionId: 7, rfc: Rfc);

    Assert.Null(course);
    var query = Assert.Single(connection.ExecutedCommands);
    Assert.Contains("cv.Estado = 'PUBLICADA' AND c.Activo = 1", query.CommandText, StringComparison.Ordinal);
    Assert.Contains(query.Parameters, parameter =>
      string.Equals(parameter.Name, "AllowPinned", StringComparison.OrdinalIgnoreCase)
      && parameter.Value is false);
    Assert.Contains(query.Parameters, parameter =>
      string.Equals(parameter.Name, "IncludeAnswerKey", StringComparison.OrdinalIgnoreCase)
      && parameter.Value is false);
  }

  [Fact]
  public async Task CursoAsignado_AllowsPinnedVersionOnlyThroughOwnedAssignment()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = static (_, _) => 1,
      ReaderResultFactory = static (commandText, _) =>
      {
        var table = new System.Data.DataTable();
        if (commandText.Contains("FROM capacitacion.Asignacion", StringComparison.Ordinal)
            && commandText.Contains("EmployeeId = @EmployeeId", StringComparison.Ordinal))
        {
          table.Columns.Add("CursoVersionId", typeof(int));
          table.Rows.Add(7);
        }
        return table;
      }
    };
    var service = new CapacitacionService(
      new FakeQueryConnectionFactory(connection),
      new StubCurrentEmployeeAccessor(Actor(employeeId: 12)));

    var course = await service.GetCursoAsignadoAsync(asignacionId: 21, rfc: Rfc, employeeId: 12);

    Assert.Null(course);
    Assert.Equal(3, connection.ExecutedCommands.Count);
    var assignmentScope = connection.ExecutedCommands[1];
    Assert.Contains("AsignacionId = @AsignacionId AND Rfc = @Rfc AND EmployeeId = @EmployeeId", assignmentScope.CommandText, StringComparison.Ordinal);
    var pinnedCourse = connection.ExecutedCommands[2];
    Assert.Contains(pinnedCourse.Parameters, parameter =>
      string.Equals(parameter.Name, "AllowPinned", StringComparison.OrdinalIgnoreCase)
      && parameter.Value is true);
    Assert.Contains(pinnedCourse.Parameters, parameter =>
      string.Equals(parameter.Name, "IncludeAnswerKey", StringComparison.OrdinalIgnoreCase)
      && parameter.Value is false);
  }

  [Fact]
  public async Task MiPlan_RejectsReadingAnotherEmployee()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = static (_, _) => 1
    };
    var service = new CapacitacionService(
      new FakeQueryConnectionFactory(connection),
      new StubCurrentEmployeeAccessor(Actor(employeeId: 12)));

    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetMiPlanAsync(Rfc, employeeId: 13));

    var employeeScopeCheck = Assert.Single(connection.ExecutedCommands);
    Assert.Contains("dbo.Capital_Humano", employeeScopeCheck.CommandText, StringComparison.Ordinal);
    Assert.Contains(employeeScopeCheck.Parameters, parameter =>
      string.Equals(parameter.Name, "EmployeeId", StringComparison.OrdinalIgnoreCase)
      && Convert.ToInt32(parameter.Value) == 12);
  }

  [Fact]
  public async Task CrearAsignaciones_RequiresTrainingRoleBeforeSql()
  {
    var connection = new FakeQueryDbConnection();
    var service = new CapacitacionService(
      new FakeQueryConnectionFactory(connection),
      new StubCurrentEmployeeAccessor(Actor(employeeId: 12)));

    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CrearAsignacionesAsync(new CapacitacionCrearAsignacionesRequest
    {
      Rfc = Rfc,
      CursoVersionId = 3,
      EmployeeIds = [21],
      ActorEmployeeId = 12,
      Actor = "spoofed@example.test"
    }));

    Assert.Empty(connection.ExecutedCommands);
  }

  [Fact]
  public async Task Instructor_CannotCreateSessionForAnotherInstructor()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = static (_, _) => 1
    };
    var service = new CapacitacionService(
      new FakeQueryConnectionFactory(connection),
      new StubCurrentEmployeeAccessor(Actor(employeeId: 12, roles: new HashSet<string>([CapacitacionCodes.RoleInstructor], StringComparer.OrdinalIgnoreCase))));

    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CrearSesionAsync(new CapacitacionCrearSesionRequest
    {
      Rfc = Rfc,
      CursoVersionId = 3,
      Nombre = "Sesión de prueba",
      InstructorEmployeeId = 99,
      ParticipantEmployeeIds = [21],
      ActorEmployeeId = 12,
      Actor = "spoofed@example.test"
    }));

    var employeeScopeCheck = Assert.Single(connection.ExecutedCommands);
    Assert.Contains("dbo.Capital_Humano", employeeScopeCheck.CommandText, StringComparison.Ordinal);
  }

  private static CurrentEmployeeContext Actor(
    int? employeeId,
    IReadOnlySet<string>? roles = null,
    string companyRfc = Rfc)
    => new(
      "authenticated@orionerp.local",
      employeeId,
      roles ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
      companyRfc);

  private sealed class StubCurrentEmployeeAccessor : ICurrentEmployeeAccessor
  {
    private readonly CurrentEmployeeContext? _context;

    public StubCurrentEmployeeAccessor(CurrentEmployeeContext? context)
    {
      _context = context;
    }

    public ValueTask<CurrentEmployeeContext?> GetCurrentAsync(CancellationToken ct = default)
      => ValueTask.FromResult(_context);
  }
}
