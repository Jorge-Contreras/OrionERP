using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OrionERP.Infrastructure.Auth;
using OrionERP.Web.Identity;

namespace OrionERP.Web.Areas.Identity.Pages.Account;

[AllowAnonymous]
public sealed class LoginWithRecoveryCodeModel : PageModel
{
  private readonly SignInManager<ApplicationUser> _signInManager;
  private readonly UserManager<ApplicationUser> _userManager;
  private readonly ICompanyAuthenticationCoordinator _companyAuthentication;

  public LoginWithRecoveryCodeModel(SignInManager<ApplicationUser> signInManager, UserManager<ApplicationUser> userManager, ICompanyAuthenticationCoordinator companyAuthentication)
    => (_signInManager, _userManager, _companyAuthentication) = (signInManager, userManager, companyAuthentication);

  [BindProperty] public InputModel Input { get; set; } = new();
  [BindProperty(SupportsGet = true)] public bool RememberMe { get; set; }
  public string ReturnUrl { get; private set; } = "/";

  public async Task<IActionResult> OnGetAsync(string? returnUrl = null)
  {
    ReturnUrl = SafeReturnUrl(returnUrl);
    return await _signInManager.GetTwoFactorAuthenticationUserAsync() is null ? RedirectToPage("./Login") : Page();
  }

  public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
  {
    ReturnUrl = SafeReturnUrl(returnUrl);
    var user = await _signInManager.GetTwoFactorAuthenticationUserAsync();
    if (user is null) return RedirectToPage("./Login");
    if (!ModelState.IsValid) return Page();
    var code = Input.Code.Replace(" ", string.Empty, StringComparison.Ordinal);
    var redeemed = await _userManager.RedeemTwoFactorRecoveryCodeAsync(user, code);
    if (!redeemed.Succeeded)
    {
      ModelState.AddModelError(string.Empty, "El código de recuperación no es válido o ya fue utilizado.");
      return Page();
    }

    await _userManager.ResetAccessFailedCountAsync(user);
    await HttpContext.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
    var result = await _companyAuthentication.BeginAsync(HttpContext, user, RememberMe, ReturnUrl);
    if (result.Status == CompanyAuthenticationStatus.SignedIn)
      return LocalRedirect(ReturnUrl);

    if (result.Status == CompanyAuthenticationStatus.NoCompany)
      TempData["ErrorMessage"] = "Tu cuenta no tiene una empresa activa asignada. Contacta al administrador de OrionERP.";
    return RedirectToPage("./Login");
  }

  private string SafeReturnUrl(string? returnUrl) => !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Content("~/");
  public sealed class InputModel { [Required(ErrorMessage = "El código es obligatorio.")] public string Code { get; set; } = string.Empty; }
}
