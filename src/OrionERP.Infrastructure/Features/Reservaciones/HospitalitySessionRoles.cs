namespace OrionERP.Infrastructure.Features.Reservaciones;

/// <summary>
/// Los dos niveles de acceso a Hospedaje, en un solo lugar porque el guard los lee de los claims
/// y el validador los vuelve a comprobar contra la base.
/// </summary>
public static class HospitalitySessionRoles
{
  /// <summary>Administra: escribe catálogos, reservaciones y órdenes de trabajo.</summary>
  public static readonly string[] Administration = ["Administrador", "SatOperator"];

  /// <summary>
  /// Sólo consulta. La ruta de cada página decide a cuál de estos roles se expone, y el
  /// calendario acota la consulta de un arrendador a sus propias habitaciones.
  /// </summary>
  public static readonly string[] ReadOnly = ["OrdenTrabajoOperador", "Arrendadores"];

  /// <summary>Los cuatro, normalizados como los guarda Identity.</summary>
  public static readonly string[] NormalizedAll =
    ["ADMINISTRADOR", "SATOPERATOR", "ORDENTRABAJOOPERADOR", "ARRENDADORES"];
}
