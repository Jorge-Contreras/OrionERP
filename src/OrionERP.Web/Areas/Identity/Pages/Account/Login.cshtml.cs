using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
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
  private readonly ICompanyAccessService _companyAccess;
  private readonly ICompanySignInContext _companySignInContext;

  public LoginModel(
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager,
    ICompanyAccessService companyAccess,
    ICompanySignInContext companySignInContext,
    ILogger<LoginModel> logger)
  {
    _signInManager = signInManager;
    _userManager = userManager;
    _companyAccess = companyAccess;
    _companySignInContext = companySignInContext;
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
    var pending = await HttpContext.AuthenticateAsync(CompanyAuthenticationSchemes.PendingCompanySelection);
    if (pending.Succeeded)
      await LoadPendingCompanyOptionsAsync(pending.Principal);
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
      return await BeginCompanySelectionAsync(user!, Input.RememberMe, ReturnUrl);
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
    var pending = await HttpContext.AuthenticateAsync(CompanyAuthenticationSchemes.PendingCompanySelection);
    if (!pending.Succeeded || pending.Principal is null)
    {
      ModelState.AddModelError(string.Empty, "La selección de empresa expiró. Ingresa tus datos nuevamente.");
      return Page();
    }

    var userId = pending.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
    var rememberMe = string.Equals(pending.Principal.FindFirst("remember_me")?.Value, "1", StringComparison.Ordinal);
    ReturnUrl = GetSafeReturnUrl(pending.Principal.FindFirst("return_url")?.Value);
    var user = string.IsNullOrWhiteSpace(userId) ? null : await _userManager.FindByIdAsync(userId);
    var normalizedRfc = rfc?.Trim().ToUpperInvariant() ?? string.Empty;
    if (user is null || !await _companyAccess.HasActiveMembershipAsync(user.Id, normalizedRfc, HttpContext.RequestAborted))
    {
      await HttpContext.SignOutAsync(CompanyAuthenticationSchemes.PendingCompanySelection);
      ModelState.AddModelError(string.Empty, "La empresa seleccionada ya no está disponible. Ingresa nuevamente.");
      return Page();
    }

    await CompleteSignInAsync(user, normalizedRfc, rememberMe);
    await HttpContext.SignOutAsync(CompanyAuthenticationSchemes.PendingCompanySelection);
    return LocalRedirect(ReturnUrl);
  }

  private async Task<IActionResult> BeginCompanySelectionAsync(ApplicationUser user, bool rememberMe, string returnUrl)
  {
    var options = await _companyAccess.GetLoginOptionsAsync(user.Id, HttpContext.RequestAborted);
    if (options.Count == 0)
    {
      ModelState.AddModelError(string.Empty, "Tu cuenta no tiene una empresa activa asignada. Contacta al administrador de OrionERP.");
      return Page();
    }

    if (options.Count == 1)
    {
      await CompleteSignInAsync(user, options[0].Rfc, rememberMe);
      return LocalRedirect(returnUrl);
    }

    var claims = new[]
    {
      new Claim(ClaimTypes.NameIdentifier, user.Id),
      new Claim("remember_me", rememberMe ? "1" : "0"),
      new Claim("return_url", GetSafeReturnUrl(returnUrl))
    };
    var identity = new ClaimsIdentity(claims, CompanyAuthenticationSchemes.PendingCompanySelection);
    await HttpContext.SignInAsync(
      CompanyAuthenticationSchemes.PendingCompanySelection,
      new ClaimsPrincipal(identity),
      new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5) });
    CompanyOptions = options;
    ReturnUrl = GetSafeReturnUrl(returnUrl);
    return Page();
  }

  private async Task CompleteSignInAsync(ApplicationUser user, string rfc, bool rememberMe)
  {
    _companySignInContext.SelectedRfc = rfc;
    try
    {
      await _signInManager.SignInAsync(user, new AuthenticationProperties
      {
        IsPersistent = rememberMe,
        AllowRefresh = true
      });
    }
    finally
    {
      _companySignInContext.SelectedRfc = null;
    }
  }

  private async Task LoadPendingCompanyOptionsAsync(ClaimsPrincipal? principal)
  {
    var userId = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId)) return;
    CompanyOptions = await _companyAccess.GetLoginOptionsAsync(userId, HttpContext.RequestAborted);
    ReturnUrl = GetSafeReturnUrl(principal?.FindFirst("return_url")?.Value);
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
