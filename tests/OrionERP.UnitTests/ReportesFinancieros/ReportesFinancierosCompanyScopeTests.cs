using System.Data;
using OrionERP.Application.Common;
using OrionERP.Application.Features.ReportesFinancieros.Models;
using OrionERP.Infrastructure.Features.ReportesFinancieros.Dapper;

namespace OrionERP.UnitTests.ReportesFinancieros;

public sealed class ReportesFinancierosCompanyScopeTests
{
  [Theory]
  [InlineData("balance")]
  [InlineData("profit")]
  [InlineData("targets_read")]
  [InlineData("targets_write")]
  [InlineData("configuration_read")]
  [InlineData("configuration_write")]
  [InlineData("worksheet")]
  public async Task ForeignCompanyIsRejectedBeforeOpeningConnection(string operation)
  {
    var connections = new NoConnectionFactory();
    var service = new ReportesFinancierosService(connections, companyContext: new TestCompany());
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => InvokeAsync(service, operation, "FOREIGN010101AA"));
    Assert.Equal(0, connections.Calls);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData(" ")]
  public async Task MissingCompanyCannotSelectAllCompanies(string? rfc)
  {
    var connections = new NoConnectionFactory();
    var service = new ReportesFinancierosService(connections, companyContext: new TestCompany());
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetBalanzaComprobacionAsync(2026, 9, rfc));
    Assert.Equal(0, connections.Calls);
  }

  [Fact]
  public async Task MissingContextNeverUsesGlobalFallback()
  {
    var connections = new NoConnectionFactory();
    var service = new ReportesFinancierosService(connections);
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetSaludEmpresaConfigurationAsync("TEST010101AAA"));
    Assert.Equal(0, connections.Calls);
  }

  private static Task InvokeAsync(ReportesFinancierosService service, string operation, string rfc)
    => operation switch
    {
      "balance" => service.GetBalanzaComprobacionAsync(2026, 9, rfc),
      "profit" => service.GetEstadoPerdidasGananciasAsync(new DateTime(2026, 9, 1), new DateTime(2026, 10, 1), rfc),
      "targets_read" => service.GetSaludEmpresaTargetsAsync(rfc, new DateTime(2026, 9, 1), new DateTime(2026, 10, 1)),
      "targets_write" => service.SaveSaludEmpresaTargetAsync(new SaludEmpresaTarget { Rfc = rfc }, "test"),
      "configuration_read" => service.GetSaludEmpresaConfigurationAsync(rfc),
      "configuration_write" => service.SaveSaludEmpresaConfigurationAsync(new SaludEmpresaConfiguration { Rfc = rfc }, "test"),
      "worksheet" => service.GetHojaTrabajoAsync(2026, rfc),
      _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

  private sealed class NoConnectionFactory : IDbConnectionFactory
  {
    public int Calls { get; private set; }
    public IDbConnection Create()
    {
      Calls++;
      throw new InvalidOperationException("No SQL connection is allowed in these unit tests.");
    }
  }

  private sealed class TestCompany : ICurrentCompanyContext
  {
    public string? CurrentRfc => "TEST010101AAA";
    public string? DisplayName => "Test";
    public int? EmployeeId => null;
    public string RequireRfc() => "TEST010101AAA";
    public void EnsureRfc(string rfc)
    {
      if (!string.Equals(rfc, CurrentRfc, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException();
    }
  }
}
