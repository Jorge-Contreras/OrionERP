using System.Data;
using Microsoft.Data.SqlClient;
using OrionERP.Application.Common;
using OrionERP.Application.Features.ReportesFinancieros.Models;
using OrionERP.Infrastructure.Features.ReportesFinancieros.Dapper;

namespace OrionERP.IntegrationTests.ReportesFinancieros;

public sealed class SaludEmpresaSqlIntegrationTests
{
  [Fact]
  [Trait("Category", "SqlIntegration")]
  public async Task V2_UsesValidReservationsAndReturnsAllExecutiveDatasets()
  {
    if (!string.Equals(Environment.GetEnvironmentVariable("ORION_RUN_SQL_INTEGRATION"), "1", StringComparison.Ordinal))
      return;

    await using var connection = await OpenSandboxAsync();
    var command = BuildReportCommand(connection);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    await using var reader = await command.ExecuteReaderAsync();

    Assert.True(await reader.ReadAsync());
    var reservationCount = Convert.ToInt32(reader["ReservationCount"]);
    var pipelineCount = Convert.ToInt32(reader["PipelineReservationCount"]);
    var rentableSuites = Convert.ToInt32(reader["RentableSuites"]);
    Assert.True(rentableSuites <= 8);

    var resultSetCount = 1;
    while (await reader.NextResultAsync())
    {
      resultSetCount++;
      while (await reader.ReadAsync()) { }
    }
    watch.Stop();

    Assert.Equal(13, resultSetCount);
    Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"La consulta tardo {watch.Elapsed.TotalSeconds:N2}s.");
    await reader.DisposeAsync();

    await using var countCommand = connection.CreateCommand();
    countCommand.CommandText = """
SELECT
  SUM(CASE WHEN UPPER(LTRIM(RTRIM(ISNULL(STATUS,'')))) IN ('ACTIVA','PAGADA') THEN 1 ELSE 0 END) ValidCount,
  SUM(CASE WHEN UPPER(LTRIM(RTRIM(ISNULL(STATUS,''))))='COTIZACION' THEN 1 ELSE 0 END) PipelineCount
FROM dbo.RESERVATION
WHERE CHECKIN>='20260801' AND CHECKIN<'20260825';
""";
    await using var counts = await countCommand.ExecuteReaderAsync();
    Assert.True(await counts.ReadAsync());
    Assert.Equal(Convert.ToInt32(counts["ValidCount"]), reservationCount);
    Assert.Equal(Convert.ToInt32(counts["PipelineCount"]), pipelineCount);
  }

  [Fact]
  [Trait("Category", "SqlIntegration")]
  public async Task Reconciliation_IsPagedAndMeetsLatencyTarget()
  {
    if (!string.Equals(Environment.GetEnvironmentVariable("ORION_RUN_SQL_INTEGRATION"), "1", StringComparison.Ordinal))
      return;

    await using var connection = await OpenSandboxAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "reporteFinanciero.Reporte_Salud_Empresa_Conciliacion";
    command.CommandType = CommandType.StoredProcedure;
    command.CommandTimeout = 30;
    command.Parameters.AddWithValue("@RFC", "OHM191112Q26");
    command.Parameters.AddWithValue("@FechaInicio", new DateTime(2026, 1, 1));
    command.Parameters.AddWithValue("@FechaFin", new DateTime(2026, 8, 24));
    command.Parameters.AddWithValue("@Pagina", 1);
    command.Parameters.AddWithValue("@TamanoPagina", 25);

    var watch = System.Diagnostics.Stopwatch.StartNew();
    await using var reader = await command.ExecuteReaderAsync();
    var rows = 0;
    while (await reader.ReadAsync()) rows++;
    watch.Stop();

    Assert.InRange(rows, 0, 25);
    Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"La conciliacion tardo {watch.Elapsed.TotalSeconds:N2}s.");
  }

  [Fact]
  [Trait("Category", "SqlIntegration")]
  public async Task NonLodgingRfc_SelfInitializesConfigurationAndTargetsWithoutHotelMetrics()
  {
    if (!string.Equals(Environment.GetEnvironmentVariable("ORION_RUN_SQL_INTEGRATION"), "1", StringComparison.Ordinal))
      return;

    var service = new ReportesFinancierosService(new SandboxConnectionFactory(GetSandboxConnectionString()));
    var report = await service.GetSaludEmpresaAsync(new SaludEmpresaQuery(
      2026, 8, 2026, 8, "AOBA880201779", new DateTime(2026, 8, 24)));
    var configuration = await service.GetSaludEmpresaConfigurationAsync("AOBA880201779");
    var targets = await service.GetSaludEmpresaTargetsAsync(
      "AOBA880201779", new DateTime(2026, 1, 1), new DateTime(2027, 8, 1));

    Assert.False(report.Metadata.LodgingEnabled);
    Assert.False(configuration.LodgingEnabled);
    Assert.Equal(20, targets.Count);
    Assert.Empty(report.SuitePerformance);
    Assert.All(report.ExecutiveIndicators, row =>
    {
      Assert.Equal(0m, row.RoomRevenue);
      Assert.Equal(0m, row.ExtrasRevenue);
      Assert.Equal(0m, row.ExperiencesRevenue);
      Assert.Equal(0, row.ReservationCount);
      Assert.Equal(0, row.PipelineReservationCount);
      Assert.Equal(row.NetAccountingIncome, row.TotalOperatingRevenue);
    });
    Assert.All(report.RevenueMix, row => Assert.Equal(0m, row.Amount));
    Assert.All(report.DailyOutlook, row =>
    {
      Assert.Equal(0m, row.RoomRevenue);
      Assert.Equal(0m, row.ComplementaryRevenue);
    });
    Assert.All(report.MonthlyOutlook, row =>
    {
      Assert.Equal(0m, row.RoomRevenue);
      Assert.Equal(0m, row.ComplementaryRevenue);
    });

    var reconciliation = await service.GetSaludEmpresaReconciliationAsync(
      new SaludEmpresaReconciliationQuery(
        "AOBA880201779", new DateTime(2026, 1, 1), new DateTime(2026, 8, 24), PageSize: 100));
    Assert.DoesNotContain(reconciliation.Items, row =>
      row.Type is "Reservacion" or "Pipeline" or "Calendario");
  }

  private static async Task<SqlConnection> OpenSandboxAsync()
  {
    var connection = new SqlConnection(GetSandboxConnectionString());
    await connection.OpenAsync();
    Assert.Equal("Orion_Sandbox", connection.Database, ignoreCase: true);
    return connection;
  }

  private static string GetSandboxConnectionString()
  {
    var source = Environment.GetEnvironmentVariable("ASPNETCORE_ConnectionStrings__OrionDb")
      ?? throw new InvalidOperationException("Falta ASPNETCORE_ConnectionStrings__OrionDb.");
    return new SqlConnectionStringBuilder(source) { InitialCatalog = "Orion_Sandbox" }.ConnectionString;
  }

  private static SqlCommand BuildReportCommand(SqlConnection connection)
  {
    var command = connection.CreateCommand();
    command.CommandText = "reporteFinanciero.Reporte_Salud_Empresa";
    command.CommandType = CommandType.StoredProcedure;
    command.CommandTimeout = 30;
    command.Parameters.AddWithValue("@AnioInicio", 2026);
    command.Parameters.AddWithValue("@MesInicio", 8);
    command.Parameters.AddWithValue("@AnioFin", 2026);
    command.Parameters.AddWithValue("@MesFin", 8);
    command.Parameters.AddWithValue("@RFC", "OHM191112Q26");
    command.Parameters.AddWithValue("@IncluirHabitacionesNoRentables", false);
    command.Parameters.AddWithValue("@FechaCorte", new DateTime(2026, 8, 24));
    return command;
  }

  private sealed class SandboxConnectionFactory(string connectionString) : IDbConnectionFactory
  {
    public IDbConnection Create() => new SqlConnection(connectionString);
  }
}
