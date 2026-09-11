using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OrionERP.Application.Features.Platform;
using OrionERP.Application.Features.Restaurante;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Bruno.Web.Pages.Account.Member;

[Authorize]
public sealed class ClosureModel : PageModel
{
  private readonly UserManager<PublicSiteUser> _userManager;
  private readonly SignInManager<PublicSiteUser> _signInManager;
  private readonly IRestaurantLoyaltyService _loyaltyService;
  private readonly IPublicWebsiteInstanceContext _website;
  private readonly IPublicIdentityScopeAccessor _identityScope;
  public ClosureModel(
    UserManager<PublicSiteUser> userManager,
    SignInManager<PublicSiteUser> signInManager,
    IRestaurantLoyaltyService loyaltyService,
    IPublicWebsiteInstanceContext website,
    IPublicIdentityScopeAccessor identityScope)
  {
    _userManager = userManager;
    _signInManager = signInManager;
    _loyaltyService = loyaltyService;
    _website = website;
    _identityScope = identityScope;
  }
  [BindProperty]
  [Required(ErrorMessage = "El motivo es obligatorio.")]
  [StringLength(500, ErrorMessage = "El motivo no puede exceder {1} caracteres.")]
  public string Reason { get; set; } = string.Empty;

  [BindProperty]
  [Required(ErrorMessage = "Escribe BAJA para confirmar.")]
  public string Confirmation { get; set; } = string.Empty;
  public void OnGet() { }
  public async Task<IActionResult> OnPostAsync(CancellationToken ct)
  {
    if (!string.Equals(Confirmation.Trim(), "BAJA", StringComparison.OrdinalIgnoreCase))
      ModelState.AddModelError(nameof(Confirmation), "Escribe BAJA para confirmar.");
    if (!ModelState.IsValid) return Page();
    var user = await _userManager.GetUserAsync(User);
    if (user is null) return Redirect("/cuenta/acceso");
    var publicSiteId = _identityScope.Current.PublicSiteId;
    var profile = await _loyaltyService.GetMemberProfileByIdentityAsync(
      _website.CurrentRfc,
      publicSiteId,
      user.Id,
      ct);
    if (profile is null) return Redirect("/");
    var result = await _loyaltyService.RequestClosureAsync(new LoyaltyClosureRequest { Rfc = _website.CurrentRfc, PublicSiteId = publicSiteId, MemberId = profile.Id, Reason = Reason }, ct);
    if (!result.Success) { ModelState.AddModelError(string.Empty, result.Message); return Page(); }
    await _signInManager.SignOutAsync();
    return Redirect("/?baja=solicitada");
  }
}
