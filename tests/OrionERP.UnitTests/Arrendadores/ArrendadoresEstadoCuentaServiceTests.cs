using OrionERP.Infrastructure.Features.Arrendadores;
using OrionERP.UnitTests.Common;
using System.Reflection;

namespace OrionERP.UnitTests.Arrendadores;

public class ArrendadoresEstadoCuentaServiceTests
{
  [Fact]
  public async Task GetArrendadoresAsync_RejectsMissingHospitalityScopeBeforeOpeningConnection()
  {
    var connection = new FakeQueryDbConnection();
    var service = new ArrendadoresEstadoCuentaService(new FakeQueryConnectionFactory(connection));

    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetArrendadoresAsync("georgina", ownerIdScope: 42));
    Assert.True(string.IsNullOrEmpty(connection.LastCommandText));
  }

  [Fact]
  public async Task GetRoomsAsync_RejectsMissingHospitalityScopeBeforeOpeningConnection()
  {
    var connection = new FakeQueryDbConnection();
    var service = new ArrendadoresEstadoCuentaService(new FakeQueryConnectionFactory(connection));

    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetRoomsAsync(42, ownerIdScope: 42));
    Assert.True(string.IsNullOrEmpty(connection.LastCommandText));
  }

  [Fact]
  public void EstadoCuentaSql_DoesNotFilterByRoomCalendarOrReservationStatus()
  {
    var sql = typeof(ArrendadoresEstadoCuentaService)
      .GetField("EstadoCuentaSql", BindingFlags.NonPublic | BindingFlags.Static)!
      .GetRawConstantValue() as string;

    Assert.NotNull(sql);
    Assert.DoesNotContain("RoomCalendarStatus = 'ACTIVA'", sql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("r.STATUS = 'ACTIVA'", sql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("rc.STATUS = 'ACTIVA'", sql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("RESERVACION_NO_ACTIVA", sql, StringComparison.OrdinalIgnoreCase);
  }
}
