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

  [Theory]
  [InlineData("PENDIENTE", "HECHO")]
  [InlineData("INCIDENCIA", "HECHO")]
  [InlineData("NO_APLICA", "HECHO")]
  [InlineData("HECHO", "PENDIENTE")]
  [InlineData("hecho", "PENDIENTE")]
  public void TogglePasoHecho_ReturnsExpectedStatus(string current, string expected)
  {
    Assert.Equal(expected, OrdenTrabajoCodes.TogglePasoHecho(current));
  }

  [Fact]
  public void NewStepModels_DefaultPhotoPolicyToOptional()
  {
    Assert.Equal(OrdenTrabajoCodes.FotoOpcional, new OrdenTrabajoStepSaveRequest().PoliticaFoto);
    Assert.Equal(OrdenTrabajoCodes.FotoOpcional, new OrdenTrabajoTemplateStepSaveRequest().PoliticaFoto);
  }
}
