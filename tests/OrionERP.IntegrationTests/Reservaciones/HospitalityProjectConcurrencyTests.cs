using System.Data;
using System.Diagnostics;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Ajustes;
using OrionERP.Application.Features.Ajustes.Catalogos;
using OrionERP.Application.Features.Reservaciones;
using OrionERP.Infrastructure.Features.Ajustes.Catalogos;
using OrionERP.Infrastructure.Features.Reservaciones;

namespace OrionERP.IntegrationTests.Reservaciones;

public sealed class HospitalityProjectConcurrencyTests
{
  [Theory, Trait("Category", "SqlIntegration")]
  [InlineData(false, true)]
  [InlineData(true, true)]
  [InlineData(false, false)]
  [InlineData(true, false)]
  public async Task GenericMutation_WaitsForConcurrentCalendarLink_AndRevalidatesItsOutcome(bool delete, bool commitLink)
  {
    if (Environment.GetEnvironmentVariable("ORION_RUN_SQL_INTEGRATION") != "1") return;
    var marker = "project-race-" + Guid.NewGuid().ToString("N");
    var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ASPNETCORE_ConnectionStrings__OrionDb")
      ?? throw new InvalidOperationException("Missing Sandbox connection")) { InitialCatalog = "Orion_Sandbox" };
    Assert.Equal("Orion_Sandbox", builder.InitialCatalog, ignoreCase: true);
    await using var observer = await OpenSandboxAsync(builder.ConnectionString);
    await using var writer = await OpenSandboxAsync(builder.ConnectionString);
    var scope = await observer.QuerySingleAsync<HospitalityScope>("SELECT p.CompanyId,p.SiteId,c.Rfc AS CompanyRfc FROM orion.PublicSite p JOIN orion.Company c ON c.CompanyId=p.CompanyId WHERE p.PublicSiteKey='bonhomia-main'");
    await HospitalityConnectionFactory.InitializeAsync(observer, scope);
    await HospitalityConnectionFactory.InitializeAsync(writer, scope);
    var calendarId = await observer.ExecuteScalarAsync<int>("SELECT TOP (1) ID FROM dbo.ROOM_CALENDAR ORDER BY ID");
    Assert.True(calendarId > 0);
    builder.ApplicationName = marker;
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:OrionDb"] = builder.ConnectionString }).Build();
    var catalog = new CatalogoService(configuration, companyContext: new FixedCompany(scope.CompanyRfc));
    var projectId = 0;
    Task<AjustesCommandResult>? mutation = null;
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    try
    {
      var created = await catalog.SaveItemAsync(new() { Key = CatalogoKey.Proyectos, Rfc = scope.CompanyRfc, Nombre = marker });
      Assert.True(created.Success, created.Message);
      projectId = await observer.ExecuteScalarAsync<int>("SELECT ID FROM dbo.Actividad WHERE RFC=@Rfc AND Descripcion=@Marker", new { Rfc = scope.CompanyRfc, Marker = marker });
      Assert.True(projectId > 0);
      await using (var transaction = (SqlTransaction)await writer.BeginTransactionAsync(IsolationLevel.Serializable))
      {
        await writer.ExecuteAsync("INSERT dbo.Actividad_RoomCalendar(Actividad_ID,RoomCalendar_ID) VALUES(@ProjectId,@CalendarId)", new { ProjectId = projectId, CalendarId = calendarId }, transaction);
        mutation = delete
          ? catalog.DeleteItemAsync(CatalogoKey.Proyectos, projectId.ToString(), scope.CompanyRfc, cancellation.Token)
          : catalog.SaveItemAsync(new() { Key = CatalogoKey.Proyectos, Id = projectId.ToString(), Rfc = scope.CompanyRfc, Nombre = marker + "-edited" }, cancellation.Token);

        // Observe a real SQL lock wait, not merely a task that happened to run slowly.
        var elapsed = Stopwatch.StartNew();
        var blocked = false;
        while (elapsed.Elapsed < TimeSpan.FromSeconds(8) && !mutation.IsCompleted)
        {
          blocked = await observer.ExecuteScalarAsync<bool>("""
            SELECT CONVERT(bit,CASE WHEN EXISTS (
              SELECT 1 FROM sys.dm_exec_requests request
              JOIN sys.dm_exec_sessions session ON session.session_id=request.session_id
              WHERE session.program_name=@ApplicationName AND request.database_id=DB_ID()
                AND request.blocking_session_id=@Writer AND request.wait_type LIKE 'LCK_M_%'
            ) THEN 1 ELSE 0 END)
            """, new { ApplicationName = marker, Writer = writer.ServerProcessId });
          if (blocked) break;
          await Task.Delay(25, cancellation.Token);
        }
        Assert.True(blocked, "The catalog mutation must wait for the concurrent calendar-link transaction.");
        Assert.False(mutation.IsCompleted);
        if (commitLink) await transaction.CommitAsync();
        else await transaction.RollbackAsync();
      }

      var result = await mutation;
      Assert.Equal(!commitLink, result.Success);
      var name = await observer.ExecuteScalarAsync<string?>("SELECT Descripcion FROM dbo.Actividad WHERE ID=@Id AND RFC=@Rfc", new { Id = projectId, Rfc = scope.CompanyRfc });
      Assert.Equal(commitLink ? marker : delete ? null : marker + "-edited", name);
      Assert.Equal(commitLink ? 1 : 0, await observer.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Actividad_RoomCalendar WHERE Actividad_ID=@Id", new { Id = projectId }));
      if (commitLink) Assert.Contains("Hospedaje", result.Message);
    }
    finally
    {
      // Transaction disposal above releases the writer even when an assertion fails.
      await cancellation.CancelAsync();
      if (mutation is not null)
      {
        try { await mutation; }
        catch (OperationCanceledException) { }
      }
      await observer.ExecuteAsync("DELETE dbo.Actividad_RoomCalendar WHERE Actividad_ID=@Id; DELETE dbo.Actividad WHERE ID=@Id AND RFC=@Rfc AND Descripcion LIKE @Marker",
        new { Id = projectId, Rfc = scope.CompanyRfc, Marker = marker + "%" });
      // Avoid retaining a separate pool for each unique application name.
      using var poolKey = new SqlConnection(builder.ConnectionString);
      SqlConnection.ClearPool(poolKey);
    }
  }

  private static async Task<SqlConnection> OpenSandboxAsync(string cs)
  {
    Assert.Equal("Orion_Sandbox", new SqlConnectionStringBuilder(cs).InitialCatalog, ignoreCase: true);
    var connection = new SqlConnection(cs);
    try
    {
      await connection.OpenAsync();
      Assert.Equal("Orion_Sandbox", await connection.ExecuteScalarAsync<string>("SELECT DB_NAME()"), ignoreCase: true);
      return connection;
    }
    catch { await connection.DisposeAsync(); throw; }
  }

  private sealed class FixedCompany(string rfc) : ICurrentCompanyContext
  {
    public string? CurrentRfc => rfc;
    public string? DisplayName => "Sandbox concurrency fixture";
    public int? EmployeeId => null;
    public string RequireRfc() => rfc;
    public void EnsureRfc(string requested)
    { if (!string.Equals(rfc, requested, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException(); }
  }
}
