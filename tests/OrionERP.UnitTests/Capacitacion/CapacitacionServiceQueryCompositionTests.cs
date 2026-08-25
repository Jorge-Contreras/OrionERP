using System.Data;
using System.Text.RegularExpressions;
using OrionERP.Application.Features.Capacitacion;
using OrionERP.Application.Features.CapitalHumano.Workforce;
using OrionERP.Infrastructure.Features.Capacitacion;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Capacitacion;

/// <summary>
/// El panel, Mi plan y las sesiones arman su SQL concatenando un SELECT reutilizable con el WHERE
/// de cada consulta. Si el fragmento reutilizable pierde el salto de línea final, el alias final
/// se pega al WHERE ("... ) finalInfoWHERE a.Rfc = @Rfc") y SQL Server responde
/// "Incorrect syntax near 'a'", lo que la UI muestra como
/// "No fue posible cargar tu panel de capacitación."
/// </summary>
public sealed class CapacitacionServiceQueryCompositionTests
{
  private const string Rfc = "OHM191112Q26";
  private const int EmployeeId = 12;

  // Detecta una palabra clave pegada al identificador anterior, p. ej. "finalInfoWHERE" o "s.RfcWHERE".
  private static readonly Regex GluedKeyword = new(
    @"[A-Za-z0-9_\]](WHERE|SELECT|ORDER|GROUP|JOIN|FROM)\b",
    RegexOptions.Compiled);

  [Fact]
  public async Task MiPlan_ComposesAssignmentSelectAndWhereWithSeparator()
  {
    var connection = NewConnection();
    var service = NewService(connection);

    await service.GetMiPlanAsync(Rfc, EmployeeId);

    var query = LastQuery(connection);
    Assert.Contains("FROM capacitacion.Asignacion a", query, StringComparison.Ordinal);
    Assert.Contains("WHERE a.Rfc = @Rfc AND a.EmployeeId = @EmployeeId", query, StringComparison.Ordinal);
    AssertNoGluedKeyword(query);
  }

  [Fact]
  public async Task Dashboard_ComposesEveryFragmentWithSeparator()
  {
    var connection = NewConnection();
    connection.MultiResultReaderFactory = static (_, _) => EmptyDashboardResults();
    var service = NewService(connection);

    await service.GetDashboardAsync(new CapacitacionActorContext
    {
      Rfc = Rfc,
      EmployeeId = EmployeeId,
      Actor = "authenticated@orionerp.local"
    });

    var query = LastQuery(connection);
    Assert.Contains("FROM capacitacion.Asignacion a", query, StringComparison.Ordinal);
    Assert.Contains("FROM capacitacion.Sesion s", query, StringComparison.Ordinal);
    AssertNoGluedKeyword(query);
  }

  [Fact]
  public async Task Sesion_ComposesSessionSelectAndWhereWithSeparator()
  {
    var connection = NewConnection();
    connection.MultiResultReaderFactory = static (_, _) =>
    {
      var set = new DataSet();
      set.Tables.Add(EmptyTable("SesionId", typeof(long)));   // resumen de la sesión
      set.Tables.Add(EmptyTable("EmployeeId", typeof(int)));  // participantes
      return set;
    };
    var service = NewService(connection);

    await service.GetSesionAsync(sesionId: 5, Rfc, actorEmployeeId: EmployeeId);

    var query = LastQuery(connection);
    Assert.Contains("FROM capacitacion.Sesion s", query, StringComparison.Ordinal);
    Assert.Contains("WHERE s.SesionId = @SesionId AND s.Rfc = @Rfc", query, StringComparison.Ordinal);
    AssertNoGluedKeyword(query);
  }

  [Fact]
  public async Task Dashboard_ReportsZeroCountersWhenEmployeeHasNoAssignments()
  {
    var connection = NewConnection();
    connection.MultiResultReaderFactory = static (_, _) => EmptyDashboardResults();
    var service = NewService(connection);

    var dashboard = await service.GetDashboardAsync(new CapacitacionActorContext
    {
      Rfc = Rfc,
      EmployeeId = EmployeeId,
      Actor = "authenticated@orionerp.local"
    });

    Assert.Equal(0, dashboard.Pendientes);
    Assert.Equal(0, dashboard.EnCurso);
    Assert.Equal(0, dashboard.Completadas);
    Assert.Equal(0, dashboard.Vencidas);
    Assert.Empty(dashboard.MisAsignaciones);
  }

  [Fact]
  public async Task Dashboard_CountersNeverReturnNullForAnEmptyAssignmentSet()
  {
    var connection = NewConnection();
    connection.MultiResultReaderFactory = static (_, _) => EmptyDashboardResults();
    var service = NewService(connection);

    await service.GetDashboardAsync(new CapacitacionActorContext
    {
      Rfc = Rfc,
      EmployeeId = EmployeeId,
      Actor = "authenticated@orionerp.local"
    });

    // SUM(CASE ...) devuelve NULL sobre cero renglones y Dapper no puede mapearlo a int.
    // COUNT(CASE ...) devuelve 0, que es lo que el panel necesita.
    var query = LastQuery(connection);
    Assert.DoesNotContain("SUM(", query, StringComparison.Ordinal);
    foreach (var counter in new[] { "Pendientes", "EnCurso", "Completadas", "Vencidas" })
    {
      var alias = " END) AS " + counter;
      var aliasIndex = query.IndexOf(alias, StringComparison.Ordinal);
      Assert.True(aliasIndex > 0, $"No se encontró el contador {counter} en el SQL del panel.");

      var expressionStart = query.LastIndexOf("COUNT(CASE WHEN ", aliasIndex, StringComparison.Ordinal);
      var previousAliasEnd = query.LastIndexOf(" AS ", aliasIndex - 1, StringComparison.Ordinal);
      Assert.True(
        expressionStart > previousAliasEnd,
        $"El contador {counter} debe calcularse con COUNT(CASE ...) para no devolver NULL sin asignaciones.");
    }
    Assert.Contains("ISNULL(AVG(", query, StringComparison.Ordinal);
  }

  private static void AssertNoGluedKeyword(string query)
  {
    var match = GluedKeyword.Match(query);
    Assert.False(
      match.Success,
      $"El SQL generado pegó una palabra clave al token anterior: \"{Context(query, match)}\".");
  }

  private static string Context(string query, Match match)
    => match.Success
      ? query.Substring(Math.Max(0, match.Index - 30), Math.Min(70, query.Length - Math.Max(0, match.Index - 30)))
      : string.Empty;

  private static DataSet EmptyDashboardResults()
  {
    var counters = new DataTable();
    counters.Columns.Add("Pendientes", typeof(int));
    counters.Columns.Add("EnCurso", typeof(int));
    counters.Columns.Add("Completadas", typeof(int));
    counters.Columns.Add("Vencidas", typeof(int));
    counters.Columns.Add("ProgresoPromedio", typeof(decimal));
    counters.Rows.Add(0, 0, 0, 0, 0m);

    var activeSessions = new DataTable();
    activeSessions.Columns.Add("SesionesActivas", typeof(int));
    activeSessions.Rows.Add(0);

    var set = new DataSet();
    set.Tables.Add(counters);
    set.Tables.Add(activeSessions);
    set.Tables.Add(EmptyTable("AsignacionId", typeof(long)));  // asignaciones
    set.Tables.Add(EmptyTable("SesionId", typeof(long)));      // sesiones
    return set;
  }

  /// <summary>Dapper necesita al menos una columna aunque el resultado venga vacío.</summary>
  private static DataTable EmptyTable(string columnName, Type columnType)
  {
    var table = new DataTable();
    table.Columns.Add(columnName, columnType);
    return table;
  }

  private static FakeQueryDbConnection NewConnection()
    => new() { ScalarResultFactory = static (_, _) => 1 };

  private static CapacitacionService NewService(FakeQueryDbConnection connection)
    => new(
      new FakeQueryConnectionFactory(connection),
      new StubAccessor(new CurrentEmployeeContext(
        "authenticated@orionerp.local",
        EmployeeId,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        Rfc)));

  private static string LastQuery(FakeQueryDbConnection connection)
    => connection.ExecutedCommands
      .Select(command => command.CommandText)
      .Last(text => text.Contains("capacitacion.", StringComparison.Ordinal));

  private sealed class StubAccessor : ICurrentEmployeeAccessor
  {
    private readonly CurrentEmployeeContext? _context;

    public StubAccessor(CurrentEmployeeContext? context)
    {
      _context = context;
    }

    public ValueTask<CurrentEmployeeContext?> GetCurrentAsync(CancellationToken ct = default)
      => ValueTask.FromResult(_context);
  }
}
