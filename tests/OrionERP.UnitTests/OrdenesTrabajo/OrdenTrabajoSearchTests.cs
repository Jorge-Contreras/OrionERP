using OrionERP.Application.Features.OrdenesTrabajo;
using OrionERP.Infrastructure.Features.OrdenesTrabajo;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.OrdenesTrabajo;

public class OrdenTrabajoSearchTests
{
  [Fact]
  public async Task SearchWorkOrdersAsync_FiltersByCreatorWhenActorIsProvided()
  {
    var connection = new FakeQueryDbConnection();
    var service = new OrdenTrabajoService(new FakeQueryConnectionFactory(connection));

    await service.SearchWorkOrdersAsync(new OrdenTrabajoSearchFilter
    {
      Rfc = "BRUNOS260707L26",
      CreatedByActor = "  vero@orion.land  ",
      Estado = OrdenTrabajoCodes.EstadoEnRevision
    });

    var command = Assert.Single(connection.ExecutedCommands);
    Assert.Contains("LTRIM(RTRIM(ot.CreadaPor)) = @CreatedByActor", command.CommandText, StringComparison.Ordinal);
    Assert.Contains(command.Parameters, parameter => string.Equals(parameter.Name, "CreatedByActor", StringComparison.OrdinalIgnoreCase)
      && Equals(parameter.Value, "vero@orion.land"));
  }

  [Fact]
  public async Task SearchWorkOrdersAsync_OmitsCreatorFilterWhenActorIsBlank()
  {
    var connection = new FakeQueryDbConnection();
    var service = new OrdenTrabajoService(new FakeQueryConnectionFactory(connection));

    await service.SearchWorkOrdersAsync(new OrdenTrabajoSearchFilter
    {
      Rfc = "BRUNOS260707L26",
      CreatedByActor = "   ",
      ParticipantEmployeeId = 10
    });

    var command = Assert.Single(connection.ExecutedCommands);
    Assert.DoesNotContain("CreadaPor", command.CommandText, StringComparison.Ordinal);
    Assert.Contains("OrdenTrabajoParticipante", command.CommandText, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SearchWorkOrdersAsync_UsesOperationalPriorityAndOverdueFilter()
  {
    var connection = new FakeQueryDbConnection();
    var service = new OrdenTrabajoService(new FakeQueryConnectionFactory(connection));

    await service.SearchWorkOrdersAsync(new OrdenTrabajoSearchFilter
    {
      Rfc = "BRUNOS260707L26",
      OverdueOnly = true,
      SortMode = OrdenTrabajoSearchSort.OperationalPriority,
      Skip = 25,
      Take = 25
    });

    var command = Assert.Single(connection.ExecutedCommands);
    Assert.Contains("ot.Estado = 'RECHAZADA'", command.CommandText, StringComparison.Ordinal);
    Assert.Contains("ISNULL(ot.FechaVencimiento, ot.FechaProgramada) < CONVERT(date, GETDATE())", command.CommandText, StringComparison.Ordinal);
    Assert.Contains("CASE ot.Prioridad", command.CommandText, StringComparison.Ordinal);
    Assert.Contains(command.Parameters, parameter => string.Equals(parameter.Name, "Skip", StringComparison.OrdinalIgnoreCase)
      && Equals(parameter.Value, 25));
    Assert.Contains(command.Parameters, parameter => string.Equals(parameter.Name, "Take", StringComparison.OrdinalIgnoreCase)
      && Equals(parameter.Value, 25));
  }

  [Fact]
  public async Task SearchWorkOrdersAsync_ClosedOnlyIncludesClosedAndCancelledStatuses()
  {
    var connection = new FakeQueryDbConnection();
    var service = new OrdenTrabajoService(new FakeQueryConnectionFactory(connection));

    await service.SearchWorkOrdersAsync(new OrdenTrabajoSearchFilter
    {
      Rfc = "BRUNOS260707L26",
      ClosedOnly = true,
      IncludeClosed = true
    });

    var command = Assert.Single(connection.ExecutedCommands);
    Assert.Contains("ot.Estado IN ('CERRADA','CANCELADA')", command.CommandText, StringComparison.Ordinal);
  }
}
