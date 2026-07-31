using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Bruno.Web.Pages.Account;

[Authorize]
public sealed class LogoutModel : PageModel
{
  private readonly SignInManager<BrunoMemberUser> _signInManager;
  public LogoutModel(SignInManager<BrunoMemberUser> signInManager) { _signInManager = signInManager; }
  public void OnGet() { }
  public async Task<IActionResult> OnPostAsync() { await _signInManager.SignOutAsync(); return LocalRedirect("/"); }
}
