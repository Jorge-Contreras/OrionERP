using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using OrionERP.Bruno.Web.Services;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Bruno.Web.Pages.Account;

[EnableRateLimiting("account")]
public sealed class ResendConfirmationModel : PageModel
{
  private readonly UserManager<BrunoMemberUser> _userManager;
  private readonly IEmailSender<BrunoMemberUser> _emailSender;
  private readonly IBrunoTurnstileService _turnstile;
  private readonly ILogger<ResendConfirmationModel> _logger;

  public ResendConfirmationModel(
    UserManager<BrunoMemberUser> userManager,
    IEmailSender<BrunoMemberUser> emailSender,
    IBrunoTurnstileService turnstile,
    ILogger<ResendConfirmationModel> logger)
  {
    _userManager = userManager;
    _emailSender = emailSender;
    _turnstile = turnstile;
    _logger = logger;
  }

  [BindProperty, Required, EmailAddress, StringLength(256)]
  public string Email { get; set; } = string.Empty;

  public bool Sent { get; private set; }

  public void OnGet() { }

  public async Task<IActionResult> OnPostAsync(CancellationToken ct = default)
  {
    var turnstileToken = Request.Form["cf-turnstile-response"].ToString();
    if (!await _turnstile.ValidateAsync(
          turnstileToken,
          HttpContext.Connection.RemoteIpAddress?.ToString(),
          "resend-confirmation",
          ct))
    {
      ModelState.AddModelError(string.Empty, "No fue posible validar la solicitud. Intenta nuevamente.");
    }

    if (!ModelState.IsValid) return Page();

    var user = await _userManager.FindByEmailAsync(Email.Trim());
    if (user is not null &&
        !user.EmailConfirmed &&
        !user.ClosedAt.HasValue &&
        !string.IsNullOrWhiteSpace(user.Email))
    {
      var token = WebEncoders.Base64UrlEncode(
        Encoding.UTF8.GetBytes(await _userManager.GenerateEmailConfirmationTokenAsync(user)));
      var confirmationUrl = Url.Page(
        "/Account/ConfirmEmail",
        null,
        new { userId = user.Id, code = token },
        Request.Scheme);

      if (confirmationUrl is null)
      {
        _logger.LogError(
          "Could not generate a resend confirmation URL for Bruno member {UserId}.",
          user.Id);
      }
      else
      {
        try
        {
          await _emailSender.SendConfirmationLinkAsync(user, user.Email, confirmationUrl);
        }
        catch (Exception ex)
        {
          _logger.LogError(
            ex,
            "Could not resend the confirmation email for Bruno member {UserId}.",
            user.Id);
        }
      }
    }

    Sent = true;
    return Page();
  }
}
