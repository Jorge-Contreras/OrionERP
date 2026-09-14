namespace OrionERP.Application.Features.Platform;

/// <summary>
/// Qué módulo necesita cada zona de rutas del portal. Es la única tabla que consultan el
/// menú, la paleta de comandos y la página de acceso denegado; las rutas universales
/// (Contabilidad, Logística, CFDI, Capital Humano, Órdenes de trabajo) no aparecen.
/// </summary>
public static class ModuleRoutes
{
  public static string? RequiredModuleFor(string? path) => FirstSegment(path) switch
  {
    "reservaciones" or "arrendadores" => PlatformModuleCodes.Hospitality,
    "restaurante" => PlatformModuleCodes.Restaurant,
    _ => null
  };

  public static bool IsAvailable(string? path, IReadOnlySet<string> enabledModules)
  {
    ArgumentNullException.ThrowIfNull(enabledModules);
    var module = RequiredModuleFor(path);
    return module is null || enabledModules.Contains(module);
  }

  public static string DisplayName(string moduleCode) => moduleCode switch
  {
    PlatformModuleCodes.Hospitality => "Hospedaje",
    PlatformModuleCodes.Restaurant => "Restaurante",
    PlatformModuleCodes.AccountingCore => "Contabilidad",
    _ => moduleCode
  };

  private static string FirstSegment(string? path)
  {
    if (string.IsNullOrWhiteSpace(path))
      return string.Empty;

    var end = path.IndexOfAny(['?', '#']);
    var value = (end >= 0 ? path[..end] : path).Trim().Trim('/');
    var slash = value.IndexOf('/');
    return (slash >= 0 ? value[..slash] : value).ToLowerInvariant();
  }
}
