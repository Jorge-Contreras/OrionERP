using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OrionERP.Web.Identity;

namespace OrionERP.Web.Areas.Identity.Pages.Account;

[AllowAnonymous]
[ValidateAntiForgeryToken]
public sealed class LogoutModel : PageModel
{
  public IActionResult OnGet() => RedirectToPage("./Login");

  public async Task<IActionResult> OnPostAsync()
  {
    await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
    await HttpContext.SignOutAsync(CompanyAuthenticationSchemes.PendingCompanySelection);
    await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
    return RedirectToPage("./Login");
  }
}
