using System.Data;
using OrionERP.Application.Common;
using OrionERP.Application.Features.OrdenesTrabajo;
using OrionERP.Infrastructure.Features.OrdenesTrabajo;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.OrdenesTrabajo;

public class OrdenTrabajoHospitalityScopeTests
{
  [Fact]
  public async Task Search_IncludesFailClosedVisibilityForEveryHospitalityReference()
  {
    var conn = new FakeQueryDbConnection();
    var service = new OrdenTrabajoService(new FakeQueryConnectionFactory(conn), companyContext: new TestCompanyContext());
    await service.SearchWorkOrdersAsync(new());
    var sql = Assert.Single(conn.ExecutedCommands).CommandText;
    Assert.Contains("ot.RoomId IS NULL AND ot.RoomCalendarId IS NULL AND ot.ReservationId IS NULL", sql);
    Assert.Contains("ownedRoom.OrionSiteId", sql);
    Assert.Contains("ownedCell.OrionSiteId", sql);
    Assert.Contains("ownedReservation.OrionSiteId", sql);
    Assert.Contains("ot.Rfc = CONVERT", sql);
  }

  [Fact]
  public async Task RoomOptions_WithoutScopeHaveExplicitScopePredicates()
  {
    var conn = new FakeQueryDbConnection();
    await new OrdenTrabajoService(new FakeQueryConnectionFactory(conn), companyContext: new TestCompanyContext()).GetRoomOptionsAsync();
    var sql = Assert.Single(conn.ExecutedCommands).CommandText;
    Assert.Contains("r.OrionCompanyId =", sql);
    Assert.Contains("r.OrionSiteId =", sql);
  }

  [Fact]
  public async Task ManualOrderWithHospitalityReference_RequiresScopeBeforeSql()
    => await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new OrdenTrabajoService(new ForbiddenConnectionFactory(), companyContext: new TestCompanyContext("OWNER010101AA1"))
      .CreateManualAsync(new() { RoomId = 10, Rfc = "OWNER010101AA1", Titulo = "Test" }));

  [Fact]
  public async Task CalendarBadges_RequireScopeBeforeSql()
    => await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new OrdenTrabajoService(new ForbiddenConnectionFactory(), companyContext: new TestCompanyContext("OWNER010101AA1"))
      .GetCalendarBadgesAsync(DateTime.Today, DateTime.Today.AddDays(1)));

  [Fact]
  public async Task LegacyAttribution_RemainsBlockedWithoutCallingSql()
  {
    var service = new OrdenTrabajoService(new ForbiddenConnectionFactory(), companyContext: new TestCompanyContext("OWNER010101AA1"));
    Assert.False((await service.SeedCleaningTemplatesFromLegacyAsync("OWNER010101AA1", "test")).Success);
    Assert.False((await service.SeedChecklistTemplatesFromLegacyAsync("OWNER010101AA1", "test")).Success);
  }

  private sealed class ForbiddenConnectionFactory : IDbConnectionFactory
  {
    public IDbConnection Create() => throw new Xunit.Sdk.XunitException("SQL must not be called.");
  }
}
