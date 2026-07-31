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
    if (!result.Succeeded) { foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error.Description); return Page(); }
    Completed = true; return Page();
  }
  public sealed class InputModel
  {
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;
    [Required] public string Code { get; set; } = string.Empty;
    [Required, DataType(DataType.Password), StringLength(100, MinimumLength = 10)] public string Password { get; set; } = string.Empty;
    [Required, DataType(DataType.Password), Compare(nameof(Password))] public string ConfirmPassword { get; set; } = string.Empty;
  }
}
