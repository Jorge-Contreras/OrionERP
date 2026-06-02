using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrionERP.Infrastructure.Auth;
using OrionERP.Infrastructure.Features.Mail;
using OrionERP.Web.Identity;

namespace OrionERP.Web.Areas.Identity.Pages.Account;

[AllowAnonymous]
public class ForgotPasswordModel : PageModel
{
  private readonly UserManager<ApplicationUser> _userManager;
  private readonly IEmailSender _emailSender;
  private readonly IOptions<GraphMailOptions> _graphMailOptions;
  private readonly ILogger<ForgotPasswordModel> _logger;

  public ForgotPasswordModel(
    UserManager<ApplicationUser> userManager,
    IEmailSender emailSender,
    IOptions<GraphMailOptions> graphMailOptions,
    ILogger<ForgotPasswordModel> logger)
  {
    _userManager = userManager;
    _emailSender = emailSender;
    _graphMailOptions = graphMailOptions;
    _logger = logger;
  }

  [BindProperty]
  public InputModel Input { get; set; } = new();

  public void OnGet()
  {
  }

  public async Task<IActionResult> OnPostAsync()
  {
    if (!ModelState.IsValid)
    {
      return Page();
    }

    var email = Input.Email.Trim();
    var user = await _userManager.FindByEmailAsync(email);

    if (user is null || string.IsNullOrWhiteSpace(user.Email))
    {
      return RedirectToPage("./ForgotPasswordConfirmation");
    }

    try
    {
      var code = await _userManager.GeneratePasswordResetTokenAsync(user);
      var payload = PasswordResetLinkCodec.Encode(user.Id, code);
      var callbackUrl = BuildAbsoluteResetUrl(payload);

      var message = $"""
<p>Hola,</p>
<p>Recibimos una solicitud para restablecer la contrasena de OrionERP para la cuenta {user.Email}.</p>
<p><a href="{callbackUrl}">Restablecer contrasena</a></p>
<p>Si el boton no abre, copia y pega este enlace en tu navegador:</p>
<p>{callbackUrl}</p>
<p>Si no solicitaste este cambio, puedes ignorar este mensaje.</p>
""";

      await _emailSender.SendEmailAsync(user.Email, "Restablece tu contrasena de OrionERP", message);
      _logger.LogInformation("Password reset email requested for {Email}.", user.Email);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to send password reset email for {Email}.", user.Email);
      ModelState.AddModelError(string.Empty, "No fue posible enviar el correo de recuperacion. Intenta nuevamente en unos minutos.");
      return Page();
    }

    return RedirectToPage("./ForgotPasswordConfirmation");
  }

  private string BuildAbsoluteResetUrl(string payload)
  {
    var encodedPayload = Uri.EscapeDataString(payload);
    var relativePath = $"/auth/reset-password?payload={encodedPayload}";

    if (string.IsNullOrWhiteSpace(relativePath))
    {
      throw new InvalidOperationException("No se pudo construir la URL de restablecimiento.");
    }

    var configuredBaseUrl = _graphMailOptions.Value.PublicBaseUrl?.Trim();
    if (!string.IsNullOrWhiteSpace(configuredBaseUrl))
    {
      if (!Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var baseUri))
      {
        throw new InvalidOperationException("GraphMail:PublicBaseUrl debe ser una URL absoluta.");
      }

      return new Uri(baseUri, relativePath).ToString();
    }

    var absoluteUrl = $"{Request.Scheme}://{Request.Host}{relativePath}";

    return absoluteUrl ?? throw new InvalidOperationException("No se pudo construir la URL absoluta de restablecimiento.");
  }

  public sealed class InputModel
  {
    [Required(ErrorMessage = "El correo es obligatorio.")]
    [EmailAddress(ErrorMessage = "Ingresa un correo valido.")]
    public string Email { get; set; } = string.Empty;
  }
}
