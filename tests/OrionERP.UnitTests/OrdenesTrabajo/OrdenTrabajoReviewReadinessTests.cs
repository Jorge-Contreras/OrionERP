using OrionERP.Application.Features.OrdenesTrabajo;

namespace OrionERP.UnitTests.OrdenesTrabajo;

public class OrdenTrabajoReviewReadinessTests
{
  [Fact]
  public void Calculate_ReportsEveryBlockingRequirement()
  {
    var readiness = OrdenTrabajoReviewReadinessCalculator.Calculate(
    [
      new OrdenTrabajoStepDto { Estado = OrdenTrabajoCodes.PasoPendiente },
      new OrdenTrabajoStepDto
      {
        Estado = OrdenTrabajoCodes.PasoHecho,
        PoliticaFoto = OrdenTrabajoCodes.FotoRequerida
      },
      new OrdenTrabajoStepDto
      {
        Estado = OrdenTrabajoCodes.PasoIncidencia,
        RequiereNotasEnIncidencia = true
      }
    ]);

    Assert.False(readiness.IsReady);
    Assert.Equal(1, readiness.PendingStepCount);
    Assert.Equal(1, readiness.MissingRequiredPhotoCount);
    Assert.Equal(1, readiness.MissingRequiredNoteCount);
    Assert.Equal(3, readiness.RequirementCount);
  }

  [Fact]
  public void Calculate_IsReadyWhenStepsAndEvidenceAreComplete()
  {
    var readiness = OrdenTrabajoReviewReadinessCalculator.Calculate(
    [
      new OrdenTrabajoStepDto
      {
        Estado = OrdenTrabajoCodes.PasoHecho,
        PoliticaFoto = OrdenTrabajoCodes.FotoRequerida,
        ActiveEvidenceCount = 1
      },
      new OrdenTrabajoStepDto
      {
        Estado = OrdenTrabajoCodes.PasoNoAplica,
        RequiereNotasEnNoAplica = true,
        Notas = "No existe acceso seguro."
      }
    ]);

    Assert.True(readiness.IsReady);
    Assert.Equal(0, readiness.RequirementCount);
  }
}
