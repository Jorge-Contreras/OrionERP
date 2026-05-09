namespace OrionERP.Application.Features.OrdenesTrabajo;

public static class OrdenTrabajoPermissions
{
  public static bool CanExecute(int? actorEmployeeId, int ownerEmployeeId, IEnumerable<int> helperEmployeeIds)
  {
    if (!actorEmployeeId.HasValue || actorEmployeeId.Value <= 0)
    {
      return false;
    }

    return actorEmployeeId.Value == ownerEmployeeId
      || helperEmployeeIds.Contains(actorEmployeeId.Value);
  }
}
