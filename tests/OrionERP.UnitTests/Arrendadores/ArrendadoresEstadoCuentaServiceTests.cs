using OrionERP.Infrastructure.Features.Arrendadores;
using OrionERP.UnitTests.Common;
using System.Reflection;

namespace OrionERP.UnitTests.Arrendadores;

public class ArrendadoresEstadoCuentaServiceTests
{
  [Fact]
  public async Task GetArrendadoresAsync_PassesOwnerIdScope()
  {
    var connection = new FakeQueryDbConnection();
    var service = new ArrendadoresEstadoCuentaService(new FakeQueryConnectionFactory(connection));

    await service.GetArrendadoresAsync("georgina", ownerIdScope: 42);

    Assert.Contains("@OwnerIdScope", connection.LastCommandText, StringComparison.OrdinalIgnoreCase);
    var scopeParameter = Assert.Single(connection.LastParameters, parameter => parameter.Name == "OwnerIdScope");
    Assert.Equal(42, scopeParameter.Value);
  }

  [Fact]
  public async Task GetRoomsAsync_PassesOwnerIdScope()
  {
    var connection = new FakeQueryDbConnection();
    var service = new ArrendadoresEstadoCuentaService(new FakeQueryConnectionFactory(connection));

    await service.GetRoomsAsync(42, ownerIdScope: 42);

    Assert.Contains("@OwnerIdScope", connection.LastCommandText, StringComparison.OrdinalIgnoreCase);
    var scopeParameter = Assert.Single(connection.LastParameters, parameter => parameter.Name == "OwnerIdScope");
    Assert.Equal(42, scopeParameter.Value);
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
