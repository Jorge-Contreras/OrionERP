using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OrionERP.Bruno.Web.Pages;

public sealed class ErrorModel : PageModel
{
  private readonly ILogger<ErrorModel> _logger;

  public ErrorModel(ILogger<ErrorModel> logger)
  {
    _logger = logger;
  }

  public void OnGet()
  {
    var exception = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
    if (exception is not null)
    {
      _logger.LogError(exception.Error, "Unhandled request failure at {Path}.", exception.Path);
    }

    Response.StatusCode = StatusCodes.Status500InternalServerError;
  }
}
