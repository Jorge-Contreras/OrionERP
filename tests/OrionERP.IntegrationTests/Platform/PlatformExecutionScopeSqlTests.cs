using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Features.Platform;
using OrionERP.Infrastructure.Features.Platform;

namespace OrionERP.IntegrationTests.Platform;

public sealed class PlatformExecutionScopeSqlTests
{
  private static bool Enabled => Environment.GetEnvironmentVariable("ORION_RUN_SQL_INTEGRATION") == "1";

  [Fact, Trait("Category", "SqlIntegration")]
  public async Task StaticSessionInitializer_OpensClosedConnectionAndKeepsScopeOnThatConnection()
  {
    if (!Enabled) return;
    var connectionString = SandboxConnectionString("PlatformClosedInitializer-" + Guid.NewGuid().ToString("N"));
    var (hospitality, _) = await LoadFixtureScopesAsync(connectionString);
    await using var connection = new SqlConnection(connectionString);

    Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    await OrionSqlSessionFactory.InitializeAsync(connection, hospitality);

    Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    Assert.Equal(hospitality.CompanyId, await ScalarAsync<long>(connection,
      "SELECT TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.CompanyId'))"));
    Assert.Equal(hospitality.PublicSiteId, await ScalarAsync<long>(connection,
      "SELECT TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'))"));
  }

  [Fact, Trait("Category", "SqlIntegration")]
  public async Task SessionFactory_AlternatesPooledPublicScopesWithoutContamination()
  {
    if (!Enabled) return;
    var connectionString = SandboxConnectionString("PlatformScopePool-" + Guid.NewGuid().ToString("N"));
    var (hospitality, restaurant) = await LoadFixtureScopesAsync(connectionString);
    var factory = new OrionSqlSessionFactory(connectionString);

    int hospitalitySession;
    await using (var first = (SqlConnection)await factory.OpenAsync(hospitality))
    {
      hospitalitySession = await ScalarAsync<int>(first, "SELECT CONVERT(int,@@SPID)");
      Assert.Equal(hospitality.CompanyId, await ScalarAsync<long>(first,
        "SELECT TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.CompanyId'))"));
      Assert.Equal(hospitality.PublicSiteId, await ScalarAsync<long>(first,
        "SELECT TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'))"));
      Assert.Equal(hospitality.SiteId, await ScalarAsync<long>(first,
        "SELECT TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.HospitalitySiteId'))"));
    }

    await using (var second = (SqlConnection)await factory.OpenAsync(restaurant))
    {
      Assert.Equal(hospitalitySession, await ScalarAsync<int>(second, "SELECT CONVERT(int,@@SPID)"));
      Assert.Equal(restaurant.CompanyId, await ScalarAsync<long>(second,
        "SELECT TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.CompanyId'))"));
      Assert.Equal(restaurant.PublicSiteId, await ScalarAsync<long>(second,
        "SELECT TRY_CONVERT(bigint,SESSION_CONTEXT(N'OrionERP.PublicSiteId'))"));
      Assert.Equal("RESTAURANT", await ScalarAsync<string>(second,
        "SELECT CONVERT(varchar(40),SESSION_CONTEXT(N'OrionERP.ModuleCode'))"));
      Assert.Equal(DBNull.Value, await ScalarAsync<object>(second,
        "SELECT SESSION_CONTEXT(N'OrionERP.HospitalityCompanyId')"));
      Assert.Equal(DBNull.Value, await ScalarAsync<object>(second,
        "SELECT SESSION_CONTEXT(N'OrionERP.HospitalitySiteId')"));
    }

    using var poolKey = new SqlConnection(connectionString);
    SqlConnection.ClearPool(poolKey);
  }

  [Fact, Trait("Category", "SqlIntegration")]
  public async Task SessionFactory_RejectsAnExplicitlyDisabledCapability()
  {
    if (!Enabled) return;
    var connectionString = SandboxConnectionString("PlatformDisabledCapability-" + Guid.NewGuid().ToString("N"));
    await using var bootstrap = new SqlConnection(connectionString);
    await bootstrap.OpenAsync();
    await using var command = bootstrap.CreateCommand();
    command.CommandText = """
      SELECT company.CompanyId,company.Rfc,siteInfo.SiteId
      FROM orion.Company company
      JOIN orion.Site siteInfo ON siteInfo.CompanyId=company.CompanyId
      WHERE company.Rfc='TST260910DUAL01' AND siteInfo.SiteKey='dual-control';
      """;
    await using var reader = await command.ExecuteReaderAsync();
    Assert.True(await reader.ReadAsync());
    var disabled = new PlatformExecutionScope(reader.GetInt64(0),reader.GetString(1),
      SiteId:reader.GetInt64(2),ModuleCode:"HOSPITALITY");
    await reader.DisposeAsync();
    await bootstrap.CloseAsync();

    var exception = await Assert.ThrowsAsync<SqlException>(
      () => new OrionSqlSessionFactory(connectionString).OpenAsync(disabled));
    Assert.Equal(52241, exception.Number);
  }

  [Fact, Trait("Category", "SqlIntegration")]
  public async Task Provisioner_PreviewAndApplyAreIdempotentForTheDualFixture()
  {
    if (!Enabled) return;
    var connectionString=SandboxConnectionString("PlatformProvisioner-"+Guid.NewGuid().ToString("N"));
    var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
      { ["ConnectionStrings:OrionDb"]=connectionString }).Build();
    var provisioner=new PublicSiteProvisioner(configuration);
    var request=new PublicSiteProvisioningRequest(
      Guid.Parse("26091000-0000-0000-0000-000000000001"),"TST260910DUAL01",null,"synthetic-dual-01",
      "Empresa dual sintética","Empresa dual sintética de validación",
      [
        new("dual-campus","Campus dual","Central Standard Time (Mexico)",["HOSPITALITY","RESTAURANT"]),
        new("dual-control","Control dual","Central Standard Time (Mexico)",[])
      ],
      [
        new("synthetic-hospitality-main","dual-campus","HOSPITALITY","synthetic-hospitality-main.test","orion_public_synthetic_h_01"),
        new("synthetic-restaurant-main","dual-campus","RESTAURANT","synthetic-restaurant-main.test","orion_public_synthetic_r_01")
      ]);

    var firstPreview=await provisioner.PreviewAsync(request);
    var secondPreview=await provisioner.PreviewAsync(request);
    Assert.True(firstPreview.CanApply);
    Assert.Equal(firstPreview.Steps,secondPreview.Steps);
    Assert.All(firstPreview.Steps,step=>Assert.True(step.AlreadySatisfied,step.StepCode));

    var firstApply=await provisioner.ApplyAsync(request);
    var secondApply=await provisioner.ApplyAsync(request);
    Assert.All(firstApply.Steps,step=>Assert.True(step.AlreadySatisfied,step.StepCode));
    Assert.Equal(firstApply.Steps,secondApply.Steps);

    var conflictingPreview=await provisioner.PreviewAsync(request with { DisplayName="Otra empresa" });
    Assert.False(conflictingPreview.CanApply);
    Assert.Contains(conflictingPreview.Errors,error=>error.Contains("OperationId completado",StringComparison.Ordinal));
  }

  [Fact, Trait("Category", "SqlIntegration")]
  public async Task ModuleJobLease_AllowsOnlyOneWorkerPerModuleCompanyAndSite()
  {
    if (!Enabled) return;
    var connectionString=SandboxConnectionString("PlatformJobLease-"+Guid.NewGuid().ToString("N"),3);
    var (_,restaurant)=await LoadFixtureScopesAsync(connectionString);
    var manager=new ModuleJobLeaseManager(new OrionSqlSessionFactory(connectionString));
    await using var first=await manager.TryAcquireAsync("SyntheticJob",restaurant);
    Assert.True(first.IsAcquired);
    await using (var competing=await manager.TryAcquireAsync("SyntheticJob",restaurant))
      Assert.False(competing.IsAcquired);
    await first.DisposeAsync();
    await using var next=await manager.TryAcquireAsync("SyntheticJob",restaurant);
    Assert.True(next.IsAcquired);
  }

  private static string SandboxConnectionString(string applicationName,int maxPoolSize=1)
  {
    var source = Environment.GetEnvironmentVariable("ASPNETCORE_ConnectionStrings__OrionDb")
      ?? throw new InvalidOperationException("Missing SQL integration connection.");
    return new SqlConnectionStringBuilder(source)
    {
      InitialCatalog="Orion_Sandbox",
      ApplicationName=applicationName,
      MaxPoolSize=maxPoolSize
    }.ConnectionString;
  }

  private static async Task<(PlatformExecutionScope Hospitality,PlatformExecutionScope Restaurant)> LoadFixtureScopesAsync(string connectionString)
  {
    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = """
      SELECT company.CompanyId,company.Rfc,company.TaxRfc,siteInfo.SiteId,publicSite.ModuleCode,
        publicSite.PublicSiteId,publicSite.PublicSiteKey,localSite.Id ModuleLocalSiteId
      FROM orion.PublicSite publicSite
      JOIN orion.Company company ON company.CompanyId=publicSite.CompanyId
      JOIN orion.Site siteInfo ON siteInfo.CompanyId=publicSite.CompanyId AND siteInfo.SiteId=publicSite.SiteId
      LEFT JOIN restaurante.Site localSite
        ON localSite.OrionCompanyId=publicSite.CompanyId AND localSite.OrionSiteId=publicSite.SiteId
       AND publicSite.ModuleCode='RESTAURANT'
      WHERE publicSite.PublicSiteKey IN('synthetic-hospitality-main','synthetic-restaurant-main')
      ORDER BY publicSite.ModuleCode;
      """;
    await using var reader = await command.ExecuteReaderAsync();
    var scopes = new List<PlatformExecutionScope>();
    while (await reader.ReadAsync())
      scopes.Add(new PlatformExecutionScope(
        reader.GetInt64(0),reader.GetString(1),reader.IsDBNull(2)?null:reader.GetString(2),reader.GetInt64(3),
        reader.GetString(4),reader.GetInt64(5),reader.GetString(6),reader.IsDBNull(7)?null:reader.GetInt32(7)));
    Assert.Equal(2,scopes.Count);
    return (scopes.Single(scope => scope.ModuleCode=="HOSPITALITY"),scopes.Single(scope => scope.ModuleCode=="RESTAURANT"));
  }

  private static async Task<T> ScalarAsync<T>(SqlConnection connection,string sql)
  {
    await using var command = connection.CreateCommand();
    command.CommandText=sql;
    return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected a scalar value."));
  }
}
