using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Web.Areas.Identity.Pages.Account;

[AllowAnonymous]
public class LoginModel : PageModel
{
  private readonly SignInManager<ApplicationUser> _signInManager;
  private readonly ILogger<LoginModel> _logger;

  public LoginModel(SignInManager<ApplicationUser> signInManager, ILogger<LoginModel> logger)
  {
    _signInManager = signInManager;
    _logger = logger;
  }

  [BindProperty]
  public InputModel Input { get; set; } = new();

  public string ReturnUrl { get; set; } = "/";

  [TempData]
  public string? ErrorMessage { get; set; }

  public async Task<IActionResult> OnGetAsync(string? returnUrl = null)
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

    await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
    return Page();
  }

  public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
  {
    ReturnUrl = GetSafeReturnUrl(returnUrl);

    if (!ModelState.IsValid)
    {
      return Page();
    }

    var result = await _signInManager.PasswordSignInAsync(
      Input.Email.Trim(),
      Input.Password,
      Input.RememberMe,
      lockoutOnFailure: true);

    if (result.Succeeded)
    {
      _logger.LogInformation("User {Email} logged in.", Input.Email);
      return LocalRedirect(ReturnUrl);
    }

    if (result.RequiresTwoFactor)
    {
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
