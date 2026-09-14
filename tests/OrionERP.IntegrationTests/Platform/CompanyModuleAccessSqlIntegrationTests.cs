using System.Data;
using Microsoft.Data.SqlClient;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Platform;
using OrionERP.Infrastructure.Features.Platform;

namespace OrionERP.IntegrationTests.Platform;

public sealed class CompanyModuleAccessSqlIntegrationTests
{
  // Mapa de Orion_Sandbox al 2026-09-14: sólo OHM tiene sede de Hospedaje, sólo Bruno's
  // de Restaurante, la empresa de prueba dual ambas y el resto ninguna.
  [Theory, Trait("Category", "SqlIntegration")]
  [InlineData("BRUNOS260707L26", false, true)]
  [InlineData("OHM191112Q26", true, false)]
  [InlineData("TST260910DUAL01", true, true)]
  [InlineData("AOBA880201779", false, false)]
  public async Task Sandbox_ReportsEachCompanysUsableModules(string rfc, bool hospitality, bool restaurant)
  {
    if (!string.Equals(Environment.GetEnvironmentVariable("ORION_RUN_SQL_INTEGRATION"), "1", StringComparison.Ordinal))
      return;

    var access = new CompanyModuleAccess(new SandboxConnectionFactory(GetSandboxConnectionString()));

    var modules = await access.GetEnabledModulesForRfcAsync(rfc);

    Assert.Contains(PlatformModuleCodes.AccountingCore, modules);
    Assert.Equal(hospitality, modules.Contains(PlatformModuleCodes.Hospitality));
    Assert.Equal(restaurant, modules.Contains(PlatformModuleCodes.Restaurant));
  }

  private static string GetSandboxConnectionString()
  {
    var source = Environment.GetEnvironmentVariable("ASPNETCORE_ConnectionStrings__OrionDb")
      ?? throw new InvalidOperationException("Falta ASPNETCORE_ConnectionStrings__OrionDb.");
    return new SqlConnectionStringBuilder(source) { InitialCatalog = "Orion_Sandbox" }.ConnectionString;
  }

  private sealed class SandboxConnectionFactory(string connectionString) : IDbConnectionFactory
  {
    public IDbConnection Create() => new SqlConnection(connectionString);
  }
}
