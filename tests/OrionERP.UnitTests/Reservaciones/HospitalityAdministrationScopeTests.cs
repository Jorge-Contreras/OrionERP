using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Reservaciones;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;
using OrionERP.Infrastructure.Features.Reservaciones;
using OrionERP.Infrastructure.Features.Reservaciones.ListaReservaciones.Services;
using OrionERP.Infrastructure.Features.Reservaciones.Experiencias;

namespace OrionERP.UnitTests.Reservaciones;

public sealed class HospitalityAdministrationScopeTests
{
  private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(
    new Dictionary<string, string?> { ["ConnectionStrings:OrionDb"] = "Server=invalid.invalid;Database=unused;Integrated Security=true;Connect Timeout=1" }).Build();

  [Fact]
  public async Task MissingAuthenticatedCompanyFailsBeforeSql()
  {
    var accessor = new HospitalityAdministrationScopeAccessor(Configuration(), new NoCompany(), new HospitalitySiteSelection());
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => accessor.ResolveRequiredAsync());
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => accessor.GetSitesAsync());
  }

  [Fact]
  public async Task ReservationOperationsNeverFallbackToGlobalConnection()
  {
    var service = new ListaReservacionesService(Configuration(), NullLogger<ListaReservacionesService>.Instance);
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetListaAsync(new ListaReservacionFilter()));
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetReservacionDetailAsync(42));
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetClientesAsync());
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAttachmentContentAsync(42));
  }

  [Fact]
  public async Task ExperienceOperationsNeverFallbackToGlobalConnection()
  {
    var service = new ReservacionExperiencesService(Configuration());
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetExperiencesAsync(42));
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetActiveExperienceCatalogAsync());
  }

  [Theory]
  [InlineData(0, 1, "RFC")]
  [InlineData(1, 0, "RFC")]
  [InlineData(1, 1, "")]
  public async Task InvalidScopeFailsBeforeConnectionIsOpened(long companyId, long siteId, string rfc)
  {
    await using var connection = new SqlConnection();
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => HospitalityConnectionFactory.InitializeAsync(
      connection, new HospitalityScope(companyId, siteId, rfc)));
    Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
  }

  private sealed class NoCompany : ICurrentCompanyContext
  {
    public string? CurrentRfc => null;
    public string? DisplayName => null;
    public int? EmployeeId => null;
    public string RequireRfc() => throw new UnauthorizedAccessException();
    public void EnsureRfc(string rfc) => throw new UnauthorizedAccessException();
  }
}
