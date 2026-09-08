using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Web.Pages;

[AllowAnonymous]
public sealed class MenusModel : PageModel
{
  private readonly IRestaurantSignageService _signageService;
  private readonly IConfiguration _configuration;
  private readonly ILogger<MenusModel> _logger;
  private bool _usesConfiguredDefault;
  private bool _allowsLegacyFallback;

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
  /// Respaldo heredado: los dos PNG estáticos de wwwroot. Solo se permite en la
  /// ruta corta cuando el RFC predeterminado sigue siendo el RFC histórico de
  /// Bruno. Una solicitud explícita u otro RFC predeterminado nunca debe
  /// heredar los tableros de esa empresa.
  /// </summary>
  public bool UseLegacyStaticBoards => _allowsLegacyFallback && Screen is null;

  public async Task<IActionResult> OnGetAsync(string? rfc, string? screenKey, CancellationToken ct)
  {
    _usesConfiguredDefault = string.IsNullOrWhiteSpace(rfc);
    var resolvedRfc = _usesConfiguredDefault
      ? _configuration["Signage:DefaultRfc"]
      : rfc;
    var resolvedKey = _usesConfiguredDefault
      ? _configuration["Signage:DefaultScreenKey"]
      : screenKey;
    _allowsLegacyFallback = _usesConfiguredDefault
      && string.Equals(
        resolvedRfc?.Trim(),
        BrunoRestaurantConstants.Rfc,
        StringComparison.OrdinalIgnoreCase);

    if (string.IsNullOrWhiteSpace(resolvedRfc))
    {
      return NotFound();
    }

    try
    {
      Screen = await _signageService.GetPublicScreenAsync(resolvedRfc, resolvedKey, ct);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "No fue posible resolver la pantalla de señalización {Rfc}/{ScreenKey}.", resolvedRfc, resolvedKey);
      return _allowsLegacyFallback ? Page() : StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    return Screen is null && !_allowsLegacyFallback ? NotFound() : Page();
  }
}
