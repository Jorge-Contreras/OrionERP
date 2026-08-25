using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using OrionERP.Application.Features.Auth.Companies;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Web.Identity;

public enum CompanyAuthenticationStatus
{
  None,
  NoCompany,
  SignedIn,
  SelectionRequired,
  InvalidPendingSelection
}

public sealed record CompanyAuthenticationResult(
  CompanyAuthenticationStatus Status,
  string ReturnUrl,
  IReadOnlyList<CompanyLoginOption> CompanyOptions)
{
  public static CompanyAuthenticationResult Create(
    CompanyAuthenticationStatus status,
    string returnUrl = "/",
    IReadOnlyList<CompanyLoginOption>? companyOptions = null)
    => new(status, returnUrl, companyOptions ?? []);
}

public interface ICompanyAuthenticationCoordinator
{
  Task<CompanyAuthenticationResult> BeginAsync(
    HttpContext httpContext,
    ApplicationUser user,
    bool rememberMe,
    string returnUrl);

  Task<CompanyAuthenticationResult> ResumePendingAsync(HttpContext httpContext);

  Task<CompanyAuthenticationResult> CompletePendingAsync(
    HttpContext httpContext,
    string? rfc);
}

public sealed class CompanyAuthenticationCoordinator : ICompanyAuthenticationCoordinator
{
  private const string RememberMeClaim = "remember_me";
  private const string ReturnUrlClaim = "return_url";
  private static readonly TimeSpan PendingSelectionLifetime = TimeSpan.FromMinutes(5);

  private readonly SignInManager<ApplicationUser> _signInManager;
  private readonly UserManager<ApplicationUser> _userManager;
  private readonly ICompanyAccessService _companyAccess;
  private readonly ICompanySignInContext _companySignInContext;

  public CompanyAuthenticationCoordinator(
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager,
    ICompanyAccessService companyAccess,
    ICompanySignInContext companySignInContext)
  {
    _signInManager = signInManager;
    _userManager = userManager;
    _companyAccess = companyAccess;
    _companySignInContext = companySignInContext;
  }

  public async Task<CompanyAuthenticationResult> BeginAsync(
    HttpContext httpContext,
    ApplicationUser user,
    bool rememberMe,
    string returnUrl)
  {
    var options = await _companyAccess.GetLoginOptionsAsync(user.Id, httpContext.RequestAborted);
    if (options.Count == 0)
      return CompanyAuthenticationResult.Create(CompanyAuthenticationStatus.NoCompany, returnUrl);

    if (options.Count == 1)
    {
      if (!await _companyAccess.HasActiveMembershipAsync(user.Id, options[0].Rfc, httpContext.RequestAborted))
        return CompanyAuthenticationResult.Create(CompanyAuthenticationStatus.NoCompany, returnUrl);

      await CompleteSignInAsync(httpContext, user, options[0].Rfc, rememberMe);
      return CompanyAuthenticationResult.Create(CompanyAuthenticationStatus.SignedIn, returnUrl);
    }

    await IssuePendingSelectionAsync(httpContext, user.Id, rememberMe, returnUrl);
    return CompanyAuthenticationResult.Create(CompanyAuthenticationStatus.SelectionRequired, returnUrl, options);
  }

  public async Task<CompanyAuthenticationResult> ResumePendingAsync(HttpContext httpContext)
  {
    var pending = await httpContext.AuthenticateAsync(CompanyAuthenticationSchemes.PendingCompanySelection);
    if (pending.None)
      return CompanyAuthenticationResult.Create(CompanyAuthenticationStatus.None);

    if (!pending.Succeeded || pending.Principal is null)
    {
      await ClearPendingAsync(httpContext);
      return CompanyAuthenticationResult.Create(CompanyAuthenticationStatus.InvalidPendingSelection);
    }

    var pendingState = await ResolvePendingStateAsync(httpContext, pending.Principal);
    if (pendingState.User is null)
    {
      await ClearPendingAsync(httpContext);
      return CompanyAuthenticationResult.Create(
        CompanyAuthenticationStatus.InvalidPendingSelection,
        pendingState.ReturnUrl);
    }

    var options = await _companyAccess.GetLoginOptionsAsync(pendingState.User.Id, httpContext.RequestAborted);
    if (options.Count == 0)
    {
      await ClearPendingAsync(httpContext);
      return CompanyAuthenticationResult.Create(CompanyAuthenticationStatus.NoCompany, pendingState.ReturnUrl);
    }

    if (options.Count == 1)
    {
      if (!await _companyAccess.HasActiveMembershipAsync(pendingState.User.Id, options[0].Rfc, httpContext.RequestAborted))
      {
        await ClearPendingAsync(httpContext);
        return CompanyAuthenticationResult.Create(CompanyAuthenticationStatus.NoCompany, pendingState.ReturnUrl);
      }

      await CompleteSignInAsync(httpContext, pendingState.User, options[0].Rfc, pendingState.RememberMe);
      await ClearPendingAsync(httpContext);
      return CompanyAuthenticationResult.Create(CompanyAuthenticationStatus.SignedIn, pendingState.ReturnUrl);
    }

    return CompanyAuthenticationResult.Create(
      CompanyAuthenticationStatus.SelectionRequired,
      pendingState.ReturnUrl,
      options);
  }

  public async Task<CompanyAuthenticationResult> CompletePendingAsync(
    HttpContext httpContext,
    string? rfc)
  {
    var pending = await httpContext.AuthenticateAsync(CompanyAuthenticationSchemes.PendingCompanySelection);
    if (!pending.Succeeded || pending.Principal is null)
    {
      await ClearPendingAsync(httpContext);
      return CompanyAuthenticationResult.Create(CompanyAuthenticationStatus.InvalidPendingSelection);
    }

    var pendingState = await ResolvePendingStateAsync(httpContext, pending.Principal);
    var normalizedRfc = NormalizeRfc(rfc);
    if (pendingState.User is null
        || normalizedRfc is null
        || !await _companyAccess.HasActiveMembershipAsync(
          pendingState.User.Id,
          normalizedRfc,
          httpContext.RequestAborted))
    {
      await ClearPendingAsync(httpContext);
      return CompanyAuthenticationResult.Create(
        CompanyAuthenticationStatus.InvalidPendingSelection,
        pendingState.ReturnUrl);
    }

    await CompleteSignInAsync(httpContext, pendingState.User, normalizedRfc, pendingState.RememberMe);
    await ClearPendingAsync(httpContext);
    return CompanyAuthenticationResult.Create(CompanyAuthenticationStatus.SignedIn, pendingState.ReturnUrl);
  }

  private async Task IssuePendingSelectionAsync(
    HttpContext httpContext,
    string userId,
    bool rememberMe,
    string returnUrl)
  {
    var identity = new ClaimsIdentity(
      [
        new Claim(ClaimTypes.NameIdentifier, userId),
        new Claim(RememberMeClaim, rememberMe ? "1" : "0"),
        new Claim(ReturnUrlClaim, returnUrl)
      ],
      CompanyAuthenticationSchemes.PendingCompanySelection);

    await httpContext.SignInAsync(
      CompanyAuthenticationSchemes.PendingCompanySelection,
      new ClaimsPrincipal(identity),
      new AuthenticationProperties
      {
        IsPersistent = false,
        ExpiresUtc = DateTimeOffset.UtcNow.Add(PendingSelectionLifetime)
      });
  }

  private async Task<(ApplicationUser? User, bool RememberMe, string ReturnUrl)> ResolvePendingStateAsync(
    HttpContext httpContext,
    ClaimsPrincipal principal)
  {
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    var user = string.IsNullOrWhiteSpace(userId) ? null : await _userManager.FindByIdAsync(userId);
    var rememberMe = string.Equals(principal.FindFirst(RememberMeClaim)?.Value, "1", StringComparison.Ordinal);
    var returnUrl = principal.FindFirst(ReturnUrlClaim)?.Value;
    return (user, rememberMe, string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl);
  }

  private async Task CompleteSignInAsync(
    HttpContext httpContext,
    ApplicationUser user,
    string rfc,
    bool rememberMe)
  {
    _signInManager.Context = httpContext;
    _companySignInContext.SelectedRfc = rfc;
    try
    {
      await _signInManager.SignInAsync(
        user,
        new AuthenticationProperties { IsPersistent = rememberMe, AllowRefresh = true });
    }
    finally
    {
      _companySignInContext.SelectedRfc = null;
    }
  }

  private static Task ClearPendingAsync(HttpContext httpContext)
    => httpContext.SignOutAsync(CompanyAuthenticationSchemes.PendingCompanySelection);

  private static string? NormalizeRfc(string? rfc)
    => string.IsNullOrWhiteSpace(rfc) ? null : rfc.Trim().ToUpperInvariant();
}
