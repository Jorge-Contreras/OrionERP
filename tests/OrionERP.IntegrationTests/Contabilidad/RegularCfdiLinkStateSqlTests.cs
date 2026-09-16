using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace OrionERP.IntegrationTests.Contabilidad;

/// <summary>
/// El estado previo al vínculo CFDI-póliza se consultó durante seis días con SQL que
/// el servidor rechazaba (error 130: subconsulta dentro de SUM) y después con una
/// forma que devolvía cero filas cuando el CFDI no tenía vínculos. Las pruebas de
/// texto no lo vieron porque el literal "parecía" correcto: hay que ejecutarlo.
/// </summary>
public sealed class RegularCfdiLinkStateSqlTests
{
  [Fact]
  public void TheStateQueryLiteralIsStillWhereTheTestExpectsIt()
  {
    Assert.Contains("PlaceholderExists", ReadStateSql(), StringComparison.Ordinal);
  }

  [Fact, Trait("Category", "SqlIntegration")]
  public async Task TheStateQueryCompilesAndAlwaysReturnsExactlyOneRow()
  {
    if (Environment.GetEnvironmentVariable("ORION_RUN_SQL_INTEGRATION") != "1") return;

    var builder = new SqlConnectionStringBuilder(
      Environment.GetEnvironmentVariable("ASPNETCORE_ConnectionStrings__OrionDb")
      ?? throw new InvalidOperationException("Missing Sandbox connection"))
    { InitialCatalog = "Orion_Sandbox" };

    await using var connection = new SqlConnection(builder.ConnectionString);
    await connection.OpenAsync();

    // Un CFDI sin ningún vínculo es el caso que rompía: QuerySingleAsync exige una fila.
    await using var command = new SqlCommand(ReadStateSql(), connection);
    command.Parameters.AddWithValue("@TransaccionId", -1);
    command.Parameters.AddWithValue("@ComprobanteId", -1L);
    command.Parameters.AddWithValue("@RelinkPlaceholder", true);
    command.Parameters.AddWithValue("@CompanyRfc", "RFC-INEXISTENTE");

    await using var reader = await command.ExecuteReaderAsync();

    Assert.True(await reader.ReadAsync(), "La consulta de estado no devolvió ninguna fila.");
    foreach (var column in new[]
             {
               "CfdiAssignedOther", "TransaccionAssignedOther",
               "CurrentLinkExists", "HasPaymentLinks", "PlaceholderExists"
             })
    {
      Assert.False(await reader.IsDBNullAsync(reader.GetOrdinal(column)), column);
    }

    Assert.False(await reader.ReadAsync(), "La consulta de estado devolvió más de una fila.");
  }

  private static string ReadStateSql()
  {
    var service = ReadRepoFile(
      "src/OrionERP.Infrastructure/Features/Contabilidad/Transacciones/Services/TransaccionService.cs");
    // El literal completo, sin anclarse a la última columna: si cambia su forma, la
    // prueba debe fallar por lo que el servidor responde, no por el patrón. El archivo
    // tiene también el stateSql de Pago20, así que se elige por contenido, no por orden.
    var match = Regex.Matches(service, """const string stateSql = @"(?<sql>(?:[^"]|"")*)";""")
      .FirstOrDefault(candidate => candidate.Groups["sql"].Value.Contains("PlaceholderExists", StringComparison.Ordinal));

    Assert.True(match is not null, "No se encontró el literal stateSql del vínculo CFDI regular.");
    return match!.Groups["sql"].Value.Replace("\"\"", "\"");
  }

  private static string ReadRepoFile(string relativePath)
  {
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null && !File.Exists(Path.Combine(current.FullName, "OrionERP.sln")))
    {
      current = current.Parent;
    }

    if (current is null)
      throw new InvalidOperationException("No se encontró la raíz del repositorio desde el directorio de pruebas.");

    return File.ReadAllText(Path.Combine(current.FullName, relativePath));
  }
}
