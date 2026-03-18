using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OrionERP.Infrastructure.Auth;
using OrionERP.Web.Identity;

namespace OrionERP.Web.Areas.Identity.Pages.Account;

[AllowAnonymous]
public class ResetPasswordModel : PageModel
{
  private readonly UserManager<ApplicationUser> _userManager;

  public ResetPasswordModel(UserManager<ApplicationUser> userManager)
  {
    _userManager = userManager;
  }

  [BindProperty]
  public InputModel Input { get; set; } = new();

  public string? ErrorMessage { get; private set; }

  public async Task<IActionResult> OnGetAsync(string? payload = null)
  {
    if (!PasswordResetLinkCodec.TryDecode(payload, out var userId, out _))
    {
      ErrorMessage = "El enlace de restablecimiento es invalido o incompleto.";
      return Page();
    }

    var user = await _userManager.FindByIdAsync(userId);
    if (user is null || string.IsNullOrWhiteSpace(user.Email))
    {
      ErrorMessage = "El enlace de restablecimiento ya no es valido para esta cuenta.";
      return Page();
    }

    Input = new InputModel
    {
      Payload = payload ?? string.Empty,
      Email = user.Email
    };

    return Page();
  }

  public async Task<IActionResult> OnPostAsync()
  {
    if (!ModelState.IsValid)
    {
      return Page();
    }

    if (!PasswordResetLinkCodec.TryDecode(Input.Payload, out var userId, out var code))
    {
      ModelState.AddModelError(string.Empty, "El enlace de restablecimiento ya no es valido.");
      return Page();
    }

    var user = await _userManager.FindByIdAsync(userId);
    if (user is null)
    {
      return RedirectToPage("./ResetPasswordConfirmation");
    }

    var result = await _userManager.ResetPasswordAsync(user, code, Input.Password);
    if (result.Succeeded)
    {
      return RedirectToPage("./ResetPasswordConfirmation");
    }

    foreach (var error in result.Errors)
    {
      ModelState.AddModelError(string.Empty, error.Description);
    }

    return Page();
  }

  public sealed class InputModel
  {
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Payload { get; set; } = string.Empty;

    [Required]
    [StringLength(100, ErrorMessage = "La contrasena debe tener al menos {2} caracteres.", MinimumLength = 8)]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [DataType(DataType.Password)]
    [Compare(nameof(Password), ErrorMessage = "La confirmacion no coincide con la contrasena.")]
    public string ConfirmPassword { get; set; } = string.Empty;

  }
}
