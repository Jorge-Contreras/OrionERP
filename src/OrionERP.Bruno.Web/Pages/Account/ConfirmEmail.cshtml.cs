using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using OrionERP.Application.Features.Restaurante;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Bruno.Web.Pages.Account;

[EnableRateLimiting("account")]
public sealed class ConfirmEmailModel : PageModel
{
  private readonly UserManager<BrunoMemberUser> _userManager;
  private readonly ILoyaltyService _loyaltyService;
  public ConfirmEmailModel(UserManager<BrunoMemberUser> userManager, ILoyaltyService loyaltyService) { _userManager = userManager; _loyaltyService = loyaltyService; }
  public string Title { get; private set; } = "Confirmación de correo";
  public string Message { get; private set; } = "El enlace no es válido.";
  public bool Success { get; private set; }
  public async Task OnGetAsync(string? userId, string? code, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(code)) return;
    var user = await _userManager.FindByIdAsync(userId);
    if (user is null) return;
    string decoded;
    try { decoded = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code)); }
    catch { return; }
    var result = await _userManager.ConfirmEmailAsync(user, decoded);
    if (!result.Succeeded) { Message = "El enlace expiró o ya no es válido."; return; }
    var member = await _loyaltyService.GetMemberProfileByIdentityAsync(BrunoSiteConstants.Rfc, user.Id, ct);
    if (member is not null) await _loyaltyService.UpdateVerificationAsync(new LoyaltyMemberVerificationRequest { Rfc = BrunoSiteConstants.Rfc, MemberId = member.Id, EmailVerified = true }, ct);
    Success = true; Title = "Correo confirmado"; Message = "Tu correo quedó confirmado. Si también verificaste tu teléfono, ya puedes acceder a Club Bruno.";
  }
}
