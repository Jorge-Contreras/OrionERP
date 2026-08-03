using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using OrionERP.Application.Features.Restaurante;
using OrionERP.Bruno.Web.Services;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Bruno.Web.Pages.Account;

[EnableRateLimiting("account")]
public sealed class LoginModel : PageModel
{
  private readonly SignInManager<BrunoMemberUser> _signInManager;
  private readonly UserManager<BrunoMemberUser> _userManager;
  private readonly ILoyaltyService _loyaltyService;
  private readonly IBrunoTurnstileService _turnstile;
  public LoginModel(SignInManager<BrunoMemberUser> signInManager, UserManager<BrunoMemberUser> userManager, ILoyaltyService loyaltyService, IBrunoTurnstileService turnstile) { _signInManager = signInManager; _userManager = userManager; _loyaltyService = loyaltyService; _turnstile = turnstile; }
  [BindProperty] public InputModel Input { get; set; } = new();
  public string? ReturnUrl { get; private set; }
  public void OnGet(string? returnUrl = null) => ReturnUrl = returnUrl;
  public async Task<IActionResult> OnPostAsync(string? returnUrl = null, CancellationToken ct = default)
  {
    ReturnUrl = returnUrl;
    if (!await _turnstile.ValidateAsync(
          Request.Form["cf-turnstile-response"].ToString(),
          HttpContext.Connection.RemoteIpAddress?.ToString(),
          "member-login",
          ct))
      ModelState.AddModelError(string.Empty, "No fue posible validar la solicitud.");
    if (!ModelState.IsValid) return Page();
    var user = await _userManager.FindByEmailAsync(Input.Email.Trim());
    if (user is null) { ModelState.AddModelError(string.Empty, "Correo o contraseña incorrectos."); return Page(); }
    var member = await _loyaltyService.GetMemberProfileByIdentityAsync(BrunoSiteConstants.Rfc, user.Id, ct);
    if (member is null || member.Status == LoyaltyMemberStatuses.Closed || user.ClosedAt.HasValue)
    {
      ModelState.AddModelError(string.Empty, "La cuenta no está disponible.");
      return Page();
    }
    var result = await _signInManager.PasswordSignInAsync(user, Input.Password, Input.RememberMe, lockoutOnFailure: true);
    if (result.IsLockedOut) { ModelState.AddModelError(string.Empty, "La cuenta está temporalmente bloqueada por varios intentos."); return Page(); }
    if (result.IsNotAllowed) { ModelState.AddModelError(string.Empty, "Confirma tu correo antes de iniciar sesión."); return Page(); }
    if (!result.Succeeded) { ModelState.AddModelError(string.Empty, "Correo o contraseña incorrectos."); return Page(); }
    return LocalRedirect(string.IsNullOrWhiteSpace(returnUrl) || !Url.IsLocalUrl(returnUrl) ? "/cuenta" : returnUrl);
  }
  public sealed class InputModel
  {
    [Required(ErrorMessage = "El correo es obligatorio.")]
    [EmailAddress(ErrorMessage = "Escribe un correo válido.")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "La contraseña es obligatoria.")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;
    public bool RememberMe { get; set; }
  }
}
