namespace OrionERP.Application.Features.OrdenesTrabajo;

public sealed record OrdenTrabajoReviewReadiness(
  int PendingStepCount,
  int MissingRequiredPhotoCount,
  int MissingRequiredNoteCount)
{
  public bool IsReady => PendingStepCount == 0
    && MissingRequiredPhotoCount == 0
    && MissingRequiredNoteCount == 0;

  public int RequirementCount => PendingStepCount
    + MissingRequiredPhotoCount
    + MissingRequiredNoteCount;
}

public static class OrdenTrabajoReviewReadinessCalculator
{
  public static OrdenTrabajoReviewReadiness Calculate(IEnumerable<OrdenTrabajoStepDto>? steps)
  {
    var items = steps?.ToList() ?? [];
    return new OrdenTrabajoReviewReadiness(
      items.Count(step => string.Equals(step.Estado, OrdenTrabajoCodes.PasoPendiente, StringComparison.OrdinalIgnoreCase)),
      items.Count(step => string.Equals(step.PoliticaFoto, OrdenTrabajoCodes.FotoRequerida, StringComparison.OrdinalIgnoreCase)
        && step.ActiveEvidenceCount <= 0
        && !step.Evidence.Any(evidence => !evidence.Eliminada)),
      items.Count(RequiresMissingNote));
  }

  private static bool RequiresMissingNote(OrdenTrabajoStepDto step)
    => string.IsNullOrWhiteSpace(step.Notas)
      && ((string.Equals(step.Estado, OrdenTrabajoCodes.PasoIncidencia, StringComparison.OrdinalIgnoreCase)
          && step.RequiereNotasEnIncidencia)
        || (string.Equals(step.Estado, OrdenTrabajoCodes.PasoNoAplica, StringComparison.OrdinalIgnoreCase)
          && step.RequiereNotasEnNoAplica));
}
