using System.Data;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Platform;
using OrionERP.Infrastructure.Features.Platform;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Platform;

public sealed class CompanyModuleAccessTests
{
  [Fact]
  public async Task WhenTheDatabaseFails_OffersOnlyTheAccountingCore()
  {
    var connections = new FailingConnectionFactory();
    var access = new CompanyModuleAccess(connections, new TestCompanyContext("BRUNOS260707L26"));

    var modules = await access.GetEnabledModulesAsync();

    Assert.Equal(PlatformModuleCodes.AccountingCore, Assert.Single(modules));
    Assert.False(await access.IsEnabledAsync(PlatformModuleCodes.Hospitality));
    Assert.True(await access.IsEnabledAsync("accounting_core"));
  }

  [Fact]
  public async Task FailuresAreNotCached_SoTheNextReadRetries()
  {
    var connections = new FailingConnectionFactory();
    var access = new CompanyModuleAccess(connections, new TestCompanyContext());

    await access.GetEnabledModulesAsync();
    await access.GetEnabledModulesAsync();

    Assert.Equal(2, connections.Calls);
  }

  [Fact]
  public async Task WithoutACompany_DoesNotQueryTheDatabase()
  {
    var connections = new FailingConnectionFactory();
    var access = new CompanyModuleAccess(connections, new NoCompanyContext());

    Assert.Equal(PlatformModuleCodes.AccountingCore, Assert.Single(await access.GetEnabledModulesAsync()));
    Assert.Equal(PlatformModuleCodes.AccountingCore, Assert.Single(await access.GetEnabledModulesForRfcAsync("  ")));
    Assert.False(await access.IsEnabledAsync(" "));
    Assert.Equal(0, connections.Calls);
  }

  private sealed class FailingConnectionFactory : IDbConnectionFactory
  {
    public int Calls { get; private set; }

    public IDbConnection Create()
    {
      Calls++;
      throw new InvalidOperationException("Sin base de datos.");
    }
  }

  private sealed class NoCompanyContext : ICurrentCompanyContext
  {
    public string? CurrentRfc => null;
    public string? DisplayName => null;
    public int? EmployeeId => null;
    public string RequireRfc() => throw new UnauthorizedAccessException("Sin empresa en la sesión.");
    public void EnsureRfc(string rfc) => throw new UnauthorizedAccessException("Sin empresa en la sesión.");
    public Task<long> RequireCompanyIdAsync(CancellationToken ct = default)
      => throw new UnauthorizedAccessException("Sin empresa en la sesión.");
  }
}
