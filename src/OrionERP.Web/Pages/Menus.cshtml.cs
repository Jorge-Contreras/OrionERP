using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OrionERP.Web.Pages;

[AllowAnonymous]
public sealed class MenusModel : PageModel
{
  public void OnGet()
  {
  }
}
