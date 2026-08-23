using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OrionERP.Application.Features.Restaurante;
using OrionERP.Infrastructure.Auth;
using QRCoder;

namespace OrionERP.Bruno.Web.Pages.Account.Member;

[Authorize]
public sealed class IndexModel : PageModel
{
  private readonly UserManager<BrunoMemberUser> _userManager;
  private readonly ILoyaltyService _loyaltyService;
  public IndexModel(UserManager<BrunoMemberUser> userManager, ILoyaltyService loyaltyService) { _userManager = userManager; _loyaltyService = loyaltyService; }
  public LoyaltyMemberProfileDto Profile { get; private set; } = new();
  public LoyaltyRedeemablePreviewDto? Redeemable { get; private set; }
  [BindProperty] public bool EmailMarketingConsent { get; set; }
  [BindProperty] public bool SmsMarketingConsent { get; set; }
  [BindProperty] public bool WhatsAppMarketingConsent { get; set; }
  [TempData] public string? Message { get; set; }
  [TempData] public bool IsError { get; set; }
  [TempData] public string? QrData { get; set; }
  [TempData] public DateTime? QrExpiresAt { get; set; }

  public async Task<IActionResult> OnGetAsync(CancellationToken ct)
  {
    if (!await LoadProfileAsync(ct)) return Redirect("/cuenta/acceso");
    EmailMarketingConsent = Profile.EmailMarketingConsent;
    SmsMarketingConsent = Profile.SmsMarketingConsent;
    WhatsAppMarketingConsent = Profile.WhatsAppMarketingConsent;
    return Page();
  }

  public async Task<IActionResult> OnPostGenerateQrAsync(CancellationToken ct)
  {
    if (!await LoadProfileAsync(ct)) return Redirect("/cuenta/acceso");
    try
    {
      var token = await _loyaltyService.CreateQrTokenAsync(BrunoSiteConstants.Rfc, Profile.Id, ct);
      QrData = Convert.ToBase64String(PngByteQRCodeHelper.GetQRCode(token.Token, QRCodeGenerator.ECCLevel.Q, 12));
      QrExpiresAt = token.ExpiresAtUtc;
    }
    catch (Exception ex) { Message = ex.Message; IsError = true; }
    return RedirectToPage();
  }

  public async Task<IActionResult> OnPostConsentsAsync(CancellationToken ct)
  {
    if (!await LoadProfileAsync(ct)) return Redirect("/cuenta/acceso");
    var result = await _loyaltyService.UpdateConsentsAsync(new LoyaltyConsentUpdateRequest
    {
      Rfc = BrunoSiteConstants.Rfc,
      MemberId = Profile.Id,
      PrivacyVersion = BrunoSiteConstants.PrivacyVersion,
      TermsVersion = BrunoSiteConstants.TermsVersion,
      EmailMarketingConsent = EmailMarketingConsent,
      SmsMarketingConsent = SmsMarketingConsent,
      WhatsAppMarketingConsent = WhatsAppMarketingConsent
    }, ct);
    Message = result.Message; IsError = !result.Success;
    return RedirectToPage();
  }

  private async Task<bool> LoadProfileAsync(CancellationToken ct)
  {
    var user = await _userManager.GetUserAsync(User);
    if (user is null) return false;
    var profile = await _loyaltyService.GetMemberProfileByIdentityAsync(BrunoSiteConstants.Rfc, user.Id, ct);
    if (profile is null || profile.Status == LoyaltyMemberStatuses.Closed) return false;
    Profile = profile;
    try
    {
      Redeemable = await _loyaltyService.GetRedeemablePreviewAsync(BrunoSiteConstants.Rfc, profile.Id, ct);
    }
    catch
    {
      Redeemable = null;
    }
    return true;
  }
}
