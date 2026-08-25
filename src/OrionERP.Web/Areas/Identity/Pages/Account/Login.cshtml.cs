using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OrionERP.Infrastructure.Auth;
using OrionERP.Application.Features.Auth.Companies;
using OrionERP.Web.Identity;

namespace OrionERP.Web.Areas.Identity.Pages.Account;

[AllowAnonymous]
public class LoginModel : PageModel
{
  private readonly SignInManager<ApplicationUser> _signInManager;
  private readonly ILogger<LoginModel> _logger;
  private readonly UserManager<ApplicationUser> _userManager;
  private readonly ICompanyAuthenticationCoordinator _companyAuthentication;

  public LoginModel(
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager,
    ICompanyAuthenticationCoordinator companyAuthentication,
    ILogger<LoginModel> logger)
  {
    _signInManager = signInManager;
    _userManager = userManager;
    _companyAuthentication = companyAuthentication;
    _logger = logger;
  }

  [BindProperty]
  public InputModel Input { get; set; } = new();

  public string ReturnUrl { get; set; } = "/";
  public IReadOnlyList<CompanyLoginOption> CompanyOptions { get; private set; } = [];
  public bool IsSelectingCompany => CompanyOptions.Count > 1;

  [TempData]
  public string? ErrorMessage { get; set; }

  public async Task<IActionResult> OnGetAsync(
    string? returnUrl = null,
    bool securityTokenExpired = false)
  {
    ReturnUrl = GetSafeReturnUrl(returnUrl);

    if (User.Identity?.IsAuthenticated == true)
    {
      return LocalRedirect(ReturnUrl);
    }

    if (!string.IsNullOrWhiteSpace(ErrorMessage))
    {
      ModelState.AddModelError(string.Empty, ErrorMessage);
    }

    if (securityTokenExpired)
    {
      ModelState.AddModelError(
        string.Empty,
        "El formulario de acceso expiro o cambio. Por seguridad, vuelve a ingresar tus datos.");
    }

    await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
    var companyResult = await _companyAuthentication.ResumePendingAsync(HttpContext);
    if (companyResult.Status != CompanyAuthenticationStatus.None)
      ReturnUrl = GetSafeReturnUrl(companyResult.ReturnUrl);
    if (companyResult.Status == CompanyAuthenticationStatus.SignedIn)
      return LocalRedirect(ReturnUrl);
    if (companyResult.Status == CompanyAuthenticationStatus.SelectionRequired)
      CompanyOptions = companyResult.CompanyOptions;
    else if (companyResult.Status == CompanyAuthenticationStatus.NoCompany)
      ModelState.AddModelError(string.Empty, "Tu cuenta no tiene una empresa activa asignada. Contacta al administrador de OrionERP.");
    else if (companyResult.Status == CompanyAuthenticationStatus.InvalidPendingSelection)
      ModelState.AddModelError(string.Empty, "La selección de empresa expiró o dejó de ser válida. Ingresa tus datos nuevamente.");
    return Page();
  }

  public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
  {
    ReturnUrl = GetSafeReturnUrl(returnUrl);

    if (!ModelState.IsValid)
    {
      return Page();
    }

    var login = Input.Email.Trim();
    var user = await _userManager.FindByEmailAsync(login) ?? await _userManager.FindByNameAsync(login);
    var result = user is null
      ? Microsoft.AspNetCore.Identity.SignInResult.Failed
      : await _signInManager.CheckPasswordSignInAsync(user, Input.Password, lockoutOnFailure: true);

    if (result.Succeeded)
    {
      _logger.LogInformation("User {Email} logged in.", Input.Email);
      var companyResult = await _companyAuthentication.BeginAsync(HttpContext, user!, Input.RememberMe, ReturnUrl);
      if (companyResult.Status == CompanyAuthenticationStatus.SignedIn)
        return LocalRedirect(ReturnUrl);
      if (companyResult.Status == CompanyAuthenticationStatus.SelectionRequired)
      {
        CompanyOptions = companyResult.CompanyOptions;
        return Page();
      }

      ModelState.AddModelError(string.Empty, "Tu cuenta no tiene una empresa activa asignada. Contacta al administrador de OrionERP.");
      return Page();
    }

    if (result.RequiresTwoFactor)
    {
      // Establish only Identity's short-lived two-factor user cookie. The
      // application cookie is created after 2FA and company selection.
      _ = await _signInManager.PasswordSignInAsync(user!, Input.Password, Input.RememberMe, lockoutOnFailure: true);
      return RedirectToPage("./LoginWith2fa", new { ReturnUrl, Input.RememberMe });
    }

    if (result.IsLockedOut)
    {
      _logger.LogWarning("User account {Email} locked out.", Input.Email);
      ModelState.AddModelError(string.Empty, "La cuenta esta temporalmente bloqueada. Intenta nuevamente mas tarde.");
      return Page();
    }

    ModelState.AddModelError(string.Empty, "Correo o contrasena incorrectos.");
    return Page();
  }

  public async Task<IActionResult> OnPostSelectCompanyAsync(string rfc)
  {
    var companyResult = await _companyAuthentication.CompletePendingAsync(HttpContext, rfc);
    ReturnUrl = GetSafeReturnUrl(companyResult.ReturnUrl);
    if (companyResult.Status != CompanyAuthenticationStatus.SignedIn)
    {
      ModelState.AddModelError(string.Empty, "La selección de empresa expiró o ya no está disponible. Ingresa tus datos nuevamente.");
      return Page();
    }

    return LocalRedirect(ReturnUrl);
  }

  private string GetSafeReturnUrl(string? returnUrl)
  {
    if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
    {
      return returnUrl;
    }

    return Url.Content("~/");
  }

  public sealed class InputModel
  {
    [Required(ErrorMessage = "El correo es obligatorio.")]
    [EmailAddress(ErrorMessage = "Ingresa un correo valido.")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "La contrasena es obligatoria.")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    public bool RememberMe { get; set; }
  }
}
