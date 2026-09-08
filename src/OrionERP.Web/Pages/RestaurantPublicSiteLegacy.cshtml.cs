using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OrionERP.Web.Pages;

[Authorize(Policy = "RestaurantAdmin")]
public sealed class RestaurantPublicSiteLegacyModel : PageModel
{
  public IActionResult OnGet() => LocalRedirect("/restaurante/sitio-publico");
}
