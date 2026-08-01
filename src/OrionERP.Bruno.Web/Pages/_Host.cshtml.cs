using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OrionERP.Bruno.Web.Pages;

public sealed class HostModel : PageModel
{
  public void OnGet()
  {
    var route = Request.Path.Value?.TrimEnd('/') ?? string.Empty;
    if (string.IsNullOrEmpty(route)) route = "/";

    if (!BrunoSiteConstants.PublicRoutes.Contains(route))
    {
      Response.StatusCode = StatusCodes.Status404NotFound;
    }
  }
}
