using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Web.Pages;

[AllowAnonymous]
public sealed class MenusModel : PageModel
{
  private readonly IRestaurantSignageService _signageService;
  private readonly IConfiguration _configuration;
  private readonly ILogger<MenusModel> _logger;

  public MenusModel(
    IRestaurantSignageService signageService,
    IConfiguration configuration,
    ILogger<MenusModel> logger)
  {
    _signageService = signageService;
    _configuration = configuration;
    _logger = logger;
  }

  public RestaurantSignagePublicScreenDto? Screen { get; private set; }

  /// <summary>
  /// Respaldo heredado: los dos PNG estáticos de wwwroot. Se usa cuando la ruta
  /// no trae RFC y no hay configuración, cuando la pantalla no existe o quedó sin
  /// imágenes, y cuando la base de datos no responde. Un tablero de menús debe
  /// seguir mostrando algo aunque el resto falle; el peor caso es exactamente el
  /// comportamiento anterior a esta función.
  /// </summary>
  public bool UseLegacyStaticBoards => Screen is null;

  public async Task OnGetAsync(string? rfc, string? screenKey, CancellationToken ct)
  {
    var usesConfiguredDefault = string.IsNullOrWhiteSpace(rfc);
    var resolvedRfc = usesConfiguredDefault
      ? _configuration["Signage:DefaultRfc"]
      : rfc;
    var resolvedKey = usesConfiguredDefault
      ? _configuration["Signage:DefaultScreenKey"]
      : screenKey;

    if (string.IsNullOrWhiteSpace(resolvedRfc)) return;

    try
    {
      Screen = await _signageService.GetPublicScreenAsync(resolvedRfc, resolvedKey, ct);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "No fue posible resolver la pantalla de señalización {Rfc}/{ScreenKey}.", resolvedRfc, resolvedKey);
    }
  }
}
