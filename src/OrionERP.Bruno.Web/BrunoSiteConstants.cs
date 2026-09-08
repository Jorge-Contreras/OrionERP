namespace OrionERP.Bruno.Web;

public static class BrunoSiteConstants
{
  public static IReadOnlySet<string> PublicRoutes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
  {
    "/",
    "/menu",
    "/promociones",
    "/membresia",
    "/visitanos",
    "/privacidad",
    "/terminos"
  };
}
