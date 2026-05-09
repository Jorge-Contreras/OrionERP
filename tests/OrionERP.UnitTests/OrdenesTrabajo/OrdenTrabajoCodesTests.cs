using OrionERP.Application.Features.OrdenesTrabajo;

namespace OrionERP.UnitTests.OrdenesTrabajo;

public class OrdenTrabajoCodesTests
{
  [Fact]
  public void OpenStatuses_IncludeAssignableAndReviewStates()
  {
    Assert.Contains(OrdenTrabajoCodes.EstadoBorrador, OrdenTrabajoCodes.OpenStatuses);
    Assert.Contains(OrdenTrabajoCodes.EstadoAsignada, OrdenTrabajoCodes.OpenStatuses);
    Assert.Contains(OrdenTrabajoCodes.EstadoEnProceso, OrdenTrabajoCodes.OpenStatuses);
    Assert.Contains(OrdenTrabajoCodes.EstadoEnRevision, OrdenTrabajoCodes.OpenStatuses);
    Assert.Contains(OrdenTrabajoCodes.EstadoRechazada, OrdenTrabajoCodes.OpenStatuses);
  }

  [Fact]
  public void OpenStatuses_ExcludeTerminalStates()
  {
    Assert.DoesNotContain(OrdenTrabajoCodes.EstadoCerrada, OrdenTrabajoCodes.OpenStatuses);
    Assert.DoesNotContain(OrdenTrabajoCodes.EstadoCancelada, OrdenTrabajoCodes.OpenStatuses);
  }

  [Fact]
  public void PhotoPolicies_UseExpectedPersistedCodes()
  {
    Assert.Equal("NO_PERMITIDA", OrdenTrabajoCodes.FotoNoPermitida);
    Assert.Equal("OPCIONAL", OrdenTrabajoCodes.FotoOpcional);
    Assert.Equal("REQUERIDA", OrdenTrabajoCodes.FotoRequerida);
  }
}
