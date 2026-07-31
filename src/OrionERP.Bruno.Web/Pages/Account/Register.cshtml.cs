using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Restaurante;
using OrionERP.Bruno.Web.Configuration;
using OrionERP.Bruno.Web.Services;
using OrionERP.Infrastructure.Auth;
using OrionERP.Infrastructure.Features.Mail;

namespace OrionERP.Bruno.Web.Pages.Account;

[EnableRateLimiting("account")]
public sealed class RegisterModel : PageModel
{
  private readonly UserManager<BrunoMemberUser> _userManager;
  private readonly IEmailSender<BrunoMemberUser> _emailSender;
  private readonly ILoyaltyService _loyaltyService;
  private readonly IBrunoPublicCatalogService _publicCatalog;
  private readonly IBrunoTurnstileService _turnstile;
  private readonly IBrunoPhoneVerificationService _phoneVerification;
  private readonly IOptions<BrunoGraphMailOptions> _mailOptions;
  private readonly IWebHostEnvironment _environment;

  public RegisterModel(
    UserManager<BrunoMemberUser> userManager,
    IEmailSender<BrunoMemberUser> emailSender,
    ILoyaltyService loyaltyService,
    IBrunoPublicCatalogService publicCatalog,
    IBrunoTurnstileService turnstile,
    IBrunoPhoneVerificationService phoneVerification,
    IOptions<BrunoGraphMailOptions> mailOptions,
    IWebHostEnvironment environment)
  {
    _userManager = userManager;
    _emailSender = emailSender;
    _loyaltyService = loyaltyService;
    _publicCatalog = publicCatalog;
    _turnstile = turnstile;
    _phoneVerification = phoneVerification;
    _mailOptions = mailOptions;
    _environment = environment;
  }

  [BindProperty] public InputModel Input { get; set; } = new();
  public string? ReturnUrl { get; private set; }

  public async Task<IActionResult> OnGetAsync(string? returnUrl = null)
  {
    ReturnUrl = returnUrl;
    var settings = await _publicCatalog.GetSettingsAsync(BrunoSiteConstants.Rfc);
    if (settings?.IsMembershipEnabled != true && !_environment.IsDevelopment())
      return Redirect("/membresia");
    return Page();
  }

  public async Task<IActionResult> OnPostAsync(string? returnUrl = null, CancellationToken ct = default)
  {
    ReturnUrl = returnUrl;
    if (!Input.IsAdultConfirmed) ModelState.AddModelError("Input.IsAdultConfirmed", "Debes confirmar que tienes al menos 18 años.");
    if (!Input.AcceptDocuments) ModelState.AddModelError("Input.AcceptDocuments", "Debes aceptar el aviso de privacidad y los términos.");
    var turnstileToken = Request.Form["cf-turnstile-response"].ToString();
    if (!await _turnstile.ValidateAsync(turnstileToken, HttpContext.Connection.RemoteIpAddress?.ToString(), ct))
      ModelState.AddModelError(string.Empty, "No fue posible validar la solicitud. Intenta nuevamente.");
    if (!ModelState.IsValid) return Page();

    var email = Input.Email.Trim();
    string phone;
    try { phone = NormalizePhone(Input.Phone); }
    catch (ValidationException ex) { ModelState.AddModelError("Input.Phone", ex.Message); return Page(); }
    var user = new BrunoMemberUser
    {
      UserName = email,
      Email = email,
      PhoneNumber = phone,
      FirstName = Input.FirstName.Trim(),
      LastName = Input.LastName.Trim()
    };
    var result = await _userManager.CreateAsync(user, Input.Password);
    if (!result.Succeeded)
    {
      foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, TranslateIdentityError(error));
      return Page();
    }

    LoyaltyMemberProfileDto member;
    try
    {
      member = await _loyaltyService.CreateMemberAsync(new LoyaltyMemberCreateRequest
      {
        Rfc = BrunoSiteConstants.Rfc,
        IdentityUserId = user.Id,
        FirstName = user.FirstName,
        LastName = user.LastName,
        Email = email,
        Phone = phone,
        IsAdultConfirmed = true,
        PrivacyVersion = BrunoSiteConstants.PrivacyVersion,
        TermsVersion = BrunoSiteConstants.TermsVersion,
        EmailMarketingConsent = Input.EmailMarketingConsent,
        SmsMarketingConsent = Input.SmsMarketingConsent,
        WhatsAppMarketingConsent = Input.WhatsAppMarketingConsent
      }, ct);
    }
    catch
    {
      await _userManager.DeleteAsync(user);
      throw;
    }

    var emailConfigured = IsMailConfigured(_mailOptions.Value);
    if (_environment.IsDevelopment() && !emailConfigured)
    {
      await _userManager.ConfirmEmailAsync(user, await _userManager.GenerateEmailConfirmationTokenAsync(user));
      await _loyaltyService.UpdateVerificationAsync(new LoyaltyMemberVerificationRequest { Rfc = BrunoSiteConstants.Rfc, MemberId = member.Id, EmailVerified = true }, ct);
    }
    else
    {
      var token = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await _userManager.GenerateEmailConfirmationTokenAsync(user)));
      var confirmationUrl = Url.Page("/Account/ConfirmEmail", null, new { userId = user.Id, code = token }, Request.Scheme)
        ?? throw new InvalidOperationException("No fue posible generar el enlace de confirmación.");
      await _emailSender.SendConfirmationLinkAsync(user, email, confirmationUrl);
    }
    await _phoneVerification.SendAsync(phone, ct);
    return RedirectToPage("/Account/VerifyPhone", new { userId = user.Id, returnUrl });
  }

  private static bool IsMailConfigured(BrunoGraphMailOptions options) =>
    !string.IsNullOrWhiteSpace(options.TenantId) &&
    !string.IsNullOrWhiteSpace(options.ClientId) &&
    !string.IsNullOrWhiteSpace(options.ClientSecret) &&
    !string.IsNullOrWhiteSpace(options.SenderAddress);

  internal static string NormalizePhone(string value)
  {
    var digits = new string(value.Where(char.IsDigit).ToArray());
    if (digits.Length == 10) digits = $"52{digits}";
    if (digits.Length is < 12 or > 15) throw new ValidationException("El teléfono no tiene un formato válido.");
    return $"+{digits}";
  }

  private static string TranslateIdentityError(IdentityError error) => error.Code switch
  {
    "DuplicateEmail" or "DuplicateUserName" => "Ya existe una cuenta con ese correo.",
    "PasswordTooShort" => "La contraseña debe tener al menos 10 caracteres.",
    "PasswordRequiresUpper" => "La contraseña requiere una mayúscula.",
    "PasswordRequiresLower" => "La contraseña requiere una minúscula.",
    "PasswordRequiresDigit" => "La contraseña requiere un número.",
    _ => "No fue posible crear la cuenta con los datos proporcionados."
  };

  public sealed class InputModel
  {
    [Required, StringLength(100)] public string FirstName { get; set; } = string.Empty;
    [Required, StringLength(100)] public string LastName { get; set; } = string.Empty;
    [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = string.Empty;
    [Required, Phone, StringLength(30)] public string Phone { get; set; } = string.Empty;
    [Required, DataType(DataType.Password), StringLength(100, MinimumLength = 10)] public string Password { get; set; } = string.Empty;
    [Required, DataType(DataType.Password), Compare(nameof(Password))] public string ConfirmPassword { get; set; } = string.Empty;
    public bool IsAdultConfirmed { get; set; }
    public bool AcceptDocuments { get; set; }
    public bool EmailMarketingConsent { get; set; }
    public bool SmsMarketingConsent { get; set; }
    public bool WhatsAppMarketingConsent { get; set; }
  }
}
