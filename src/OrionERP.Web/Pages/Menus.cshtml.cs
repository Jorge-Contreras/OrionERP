using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OrionERP.Application.Features.Platform;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Web.Pages;

[AllowAnonymous]
public sealed class MenusModel : PageModel
{
  private readonly IRestaurantSignageService _signageService;
  private readonly IPublicSiteResolver _publicSiteResolver;
  private readonly IConfiguration _configuration;
  private readonly ILogger<MenusModel> _logger;
  private bool _usesConfiguredDefault;
  private bool _allowsLegacyFallback;

  public MenusModel(
    IRestaurantSignageService signageService,
    IPublicSiteResolver publicSiteResolver,
    IConfiguration configuration,
    ILogger<MenusModel> logger)
  {
    _signageService = signageService;
    _publicSiteResolver = publicSiteResolver;
    _configuration = configuration;
    _logger = logger;
  }

  public RestaurantSignagePublicScreenDto? Screen { get; private set; }

  /// <summary>
  /// Respaldo heredado: los dos PNG estáticos de wwwroot. Solo se permite en la
  /// ruta corta cuando el perfil de compatibilidad lo habilita explícitamente.
  /// Una solicitud con RFC explícito nunca hereda esos tableros.
  /// </summary>
  public bool UseLegacyStaticBoards => _allowsLegacyFallback && Screen is null;

  public async Task<IActionResult> OnGetAsync(string? rfc, string? screenKey, CancellationToken ct)
  {
    _usesConfiguredDefault = string.IsNullOrWhiteSpace(rfc);
    var resolvedRfc = rfc;
    var resolvedKey = _usesConfiguredDefault
      ? _configuration["Signage:DefaultScreenKey"]
      : screenKey;

    if (_usesConfiguredDefault)
    {
      var publicSiteKey = _configuration["Signage:DefaultPublicSiteKey"];
      if (string.IsNullOrWhiteSpace(publicSiteKey))
        return NotFound();
      try
      {
        var binding = await _publicSiteResolver.ResolveRequiredAsync(
          new PublicSiteResolutionRequest(
            publicSiteKey,
            string.Empty,
            string.Empty,
            PlatformModuleCodes.Restaurant,
            string.Empty),
          ct);
        resolvedRfc = binding.CompanyRfc;
      }
      catch (PublicSiteResolutionException ex)
      {
        _logger.LogError(ex, "No fue posible resolver el PublicSite de señalización {PublicSiteKey}.", publicSiteKey);
        return StatusCode(StatusCodes.Status503ServiceUnavailable);
      }

      _allowsLegacyFallback = _configuration.GetValue<bool>("Signage:LegacyStaticFallbackEnabled");
    }

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
