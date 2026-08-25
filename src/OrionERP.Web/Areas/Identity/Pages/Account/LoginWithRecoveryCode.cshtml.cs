using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OrionERP.Application.Features.Auth.Companies;
using OrionERP.Infrastructure.Auth;
using OrionERP.Web.Identity;

namespace OrionERP.Web.Areas.Identity.Pages.Account;

[AllowAnonymous]
public sealed class LoginWithRecoveryCodeModel : PageModel
{
  private readonly SignInManager<ApplicationUser> _signInManager;
  private readonly UserManager<ApplicationUser> _userManager;
  private readonly ICompanyAccessService _companyAccess;
  private readonly ICompanySignInContext _companySignInContext;

  public LoginWithRecoveryCodeModel(SignInManager<ApplicationUser> signInManager, UserManager<ApplicationUser> userManager, ICompanyAccessService companyAccess, ICompanySignInContext companySignInContext)
    => (_signInManager, _userManager, _companyAccess, _companySignInContext) = (signInManager, userManager, companyAccess, companySignInContext);

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
    var options = await _companyAccess.GetLoginOptionsAsync(user.Id, HttpContext.RequestAborted);
    if (options.Count == 0)
    {
      TempData["ErrorMessage"] = "Tu cuenta no tiene una empresa activa asignada. Contacta al administrador de OrionERP.";
      return RedirectToPage("./Login");
    }

    if (options.Count == 1)
    {
      _companySignInContext.SelectedRfc = options[0].Rfc;
      try { await _signInManager.SignInAsync(user, new AuthenticationProperties { IsPersistent = RememberMe, AllowRefresh = true }); }
      finally { _companySignInContext.SelectedRfc = null; }
      return LocalRedirect(ReturnUrl);
    }

    var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim("remember_me", RememberMe ? "1" : "0"), new Claim("return_url", ReturnUrl) }, CompanyAuthenticationSchemes.PendingCompanySelection);
    await HttpContext.SignInAsync(CompanyAuthenticationSchemes.PendingCompanySelection, new ClaimsPrincipal(identity), new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5) });
    return RedirectToPage("./Login");
  }

  private string SafeReturnUrl(string? returnUrl) => !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Content("~/");
  public sealed class InputModel { [Required(ErrorMessage = "El código es obligatorio.")] public string Code { get; set; } = string.Empty; }
}
