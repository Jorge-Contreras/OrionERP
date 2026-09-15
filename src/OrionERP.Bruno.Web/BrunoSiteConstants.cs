namespace OrionERP.Bruno.Web;

public static class BrunoSiteConstants
{
  public static IReadOnlySet<string> PublicRoutes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
  {
    "/",
    "/menu",
    "/ordenar",
    "/checkout",
    "/promociones",
    "/membresia",
    "/visitanos",
    "/privacidad",
    "/terminos"
  };

  public static bool IsPublicRoute(string? route)
  {
    var normalized = route?.TrimEnd('/') ?? string.Empty;
    if (string.IsNullOrEmpty(normalized)) normalized = "/";
    if (PublicRoutes.Contains(normalized)) return true;
    if (!normalized.StartsWith("/pedido/", StringComparison.OrdinalIgnoreCase)) return false;
    var token = normalized["/pedido/".Length..];
    return token.Length is > 0 and <= 2048 && !token.Contains('/');
  }
}
