namespace OrionERP.Application.Features.OrdenesTrabajo;

public static class OrdenTrabajoPermissions
{
  public static readonly IReadOnlyList<string> ManagementRoles =
  [
    "Administrador",
    "OrdenTrabajoAdmin",
    "OrdenTrabajoSupervisor"
  ];

  public static bool CanAccessManagement(Func<string, bool> isInRole)
    => ManagementRoles.Any(isInRole);

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
