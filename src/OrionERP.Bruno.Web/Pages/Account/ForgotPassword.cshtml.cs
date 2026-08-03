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
public sealed class ForgotPasswordModel : PageModel
{
  private readonly UserManager<BrunoMemberUser> _userManager;
  private readonly IEmailSender<BrunoMemberUser> _emailSender;
  public ForgotPasswordModel(UserManager<BrunoMemberUser> userManager, IEmailSender<BrunoMemberUser> emailSender) { _userManager = userManager; _emailSender = emailSender; }
  [BindProperty]
  [Required(ErrorMessage = "El correo es obligatorio.")]
  [EmailAddress(ErrorMessage = "Escribe un correo válido.")]
  public string Email { get; set; } = string.Empty;
  public bool Sent { get; private set; }
  public void OnGet() { }
  public async Task<IActionResult> OnPostAsync()
  {
    if (!ModelState.IsValid) return Page();
    var user = await _userManager.FindByEmailAsync(Email.Trim());
    if (user is not null && user.EmailConfirmed && !user.ClosedAt.HasValue)
    {
      var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await _userManager.GeneratePasswordResetTokenAsync(user)));
      var url = Url.Page("/Account/ResetPassword", null, new { email = user.Email, code }, Request.Scheme);
      if (url is not null) await _emailSender.SendPasswordResetLinkAsync(user, user.Email!, url);
    }
    Sent = true;
    return Page();
  }
}
