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
public sealed class VerifyPhoneModel : PageModel
{
  private readonly UserManager<BrunoMemberUser> _userManager;
  private readonly SignInManager<BrunoMemberUser> _signInManager;
  private readonly ILoyaltyService _loyaltyService;
  private readonly IBrunoPhoneVerificationService _verification;
  public VerifyPhoneModel(UserManager<BrunoMemberUser> userManager, SignInManager<BrunoMemberUser> signInManager, ILoyaltyService loyaltyService, IBrunoPhoneVerificationService verification) { _userManager = userManager; _signInManager = signInManager; _loyaltyService = loyaltyService; _verification = verification; }
  [BindProperty, Required] public string UserId { get; set; } = string.Empty;
  [BindProperty, Required, StringLength(10, MinimumLength = 4)] public string Code { get; set; } = string.Empty;
  [BindProperty] public string? ReturnUrl { get; set; }
  public void OnGet(string userId, string? returnUrl = null) { UserId = userId; ReturnUrl = returnUrl; }
  public async Task<IActionResult> OnPostAsync(CancellationToken ct)
  {
    if (!ModelState.IsValid) return Page();
    var user = await _userManager.FindByIdAsync(UserId);
    if (user is null || string.IsNullOrWhiteSpace(user.PhoneNumber)) return RedirectToPage("/Account/Login");
    if (!await _verification.CheckAsync(user.PhoneNumber, Code, ct)) { ModelState.AddModelError(string.Empty, "El código no es válido o expiró."); return Page(); }
    user.PhoneNumberConfirmed = true;
    await _userManager.UpdateAsync(user);
    var member = await _loyaltyService.GetMemberProfileByIdentityAsync(BrunoSiteConstants.Rfc, user.Id, ct);
    if (member is not null) await _loyaltyService.UpdateVerificationAsync(new LoyaltyMemberVerificationRequest { Rfc = BrunoSiteConstants.Rfc, MemberId = member.Id, PhoneVerified = true }, ct);
    if (user.EmailConfirmed) await _signInManager.SignInAsync(user, isPersistent: false);
    return LocalRedirect(string.IsNullOrWhiteSpace(ReturnUrl) || !Url.IsLocalUrl(ReturnUrl) ? "/cuenta" : ReturnUrl);
  }
}
