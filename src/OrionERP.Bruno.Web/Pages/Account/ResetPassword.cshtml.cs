using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Bruno.Web.Pages.Account;

[EnableRateLimiting("account")]
public sealed class ResetPasswordModel : PageModel
{
  private readonly UserManager<BrunoMemberUser> _userManager;
  public ResetPasswordModel(UserManager<BrunoMemberUser> userManager) { _userManager = userManager; }
  [BindProperty] public InputModel Input { get; set; } = new();
  public bool Completed { get; private set; }
  public IActionResult OnGet(string? code = null, string? email = null)
  {
    if (string.IsNullOrWhiteSpace(code)) return BadRequest();
    Input.Code = code; Input.Email = email ?? string.Empty; return Page();
  }
  public async Task<IActionResult> OnPostAsync()
  {
    if (!ModelState.IsValid) return Page();
    var user = await _userManager.FindByEmailAsync(Input.Email.Trim());
    if (user is null) { Completed = true; return Page(); }
    string token;
    try { token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(Input.Code)); }
    catch { ModelState.AddModelError(string.Empty, "El enlace no es válido."); return Page(); }
    var result = await _userManager.ResetPasswordAsync(user, token, Input.Password);
    if (!result.Succeeded) { foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, TranslateIdentityError(error)); return Page(); }
    Completed = true; return Page();
  }

  private static string TranslateIdentityError(IdentityError error) => error.Code switch
  {
    "PasswordTooShort" => "La contraseña debe tener al menos 8 caracteres.",
    "PasswordRequiresUpper" => "La contraseña requiere una mayúscula.",
    "PasswordRequiresLower" => "La contraseña requiere una minúscula.",
    "PasswordRequiresDigit" => "La contraseña requiere un número.",
    "InvalidToken" => "El enlace no es válido o ya expiró.",
    _ => "No fue posible actualizar la contraseña. Solicita un enlace nuevo e intenta nuevamente."
  };

  public sealed class InputModel
  {
    [Required(ErrorMessage = "El correo es obligatorio.")]
    [EmailAddress(ErrorMessage = "Escribe un correo válido.")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "El enlace de recuperación no es válido.")]
    public string Code { get; set; } = string.Empty;

    [Required(ErrorMessage = "La contraseña es obligatoria.")]
    [DataType(DataType.Password)]
    [StringLength(100, MinimumLength = 8, ErrorMessage = "La contraseña debe tener entre {2} y {1} caracteres.")]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "Confirma tu contraseña.")]
    [DataType(DataType.Password)]
    [Compare(nameof(Password), ErrorMessage = "Las contraseñas no coinciden.")]
    public string ConfirmPassword { get; set; } = string.Empty;
  }
}
