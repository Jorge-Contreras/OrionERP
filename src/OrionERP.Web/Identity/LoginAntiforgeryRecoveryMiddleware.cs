using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;

namespace OrionERP.Web.Identity;

public sealed class LoginAntiforgeryRecoveryMiddleware
{
  private const string LoginPath = "/Identity/Account/Login";
  private const string RecoveryQueryParameter = "securityTokenExpired=true";

  private readonly RequestDelegate _next;
  private readonly AntiforgeryOptions _antiforgeryOptions;
  private readonly ILogger<LoginAntiforgeryRecoveryMiddleware> _logger;

  public LoginAntiforgeryRecoveryMiddleware(
    RequestDelegate next,
    IOptions<AntiforgeryOptions> antiforgeryOptions,
    ILogger<LoginAntiforgeryRecoveryMiddleware> logger)
  {
    _next = next;
    _antiforgeryOptions = antiforgeryOptions.Value;
    _logger = logger;
  }

  public async Task InvokeAsync(HttpContext context)
  {
    await _next(context);

    if (!IsRejectedLoginPost(context) || context.Response.HasStarted)
    {
      return;
    }

    var cookieName = _antiforgeryOptions.Cookie.Name;
    var redirectUri = BuildRecoveryUri(context.Request);

    context.Response.Clear();

    if (!string.IsNullOrWhiteSpace(cookieName))
    {
      context.Response.Cookies.Delete(cookieName);
    }

    context.Response.Redirect(redirectUri);

    _logger.LogWarning(
      "Recovered rejected login POST by clearing the antiforgery cookie and redirecting to {RedirectUri}.",
      redirectUri);
  }

  private static bool IsRejectedLoginPost(HttpContext context)
    => HttpMethods.IsPost(context.Request.Method)
       && string.Equals(context.Request.Path.Value, LoginPath, StringComparison.OrdinalIgnoreCase)
       && context.Response.StatusCode == StatusCodes.Status400BadRequest;

  private static string BuildRecoveryUri(HttpRequest request)
  {
    var currentUri = $"{request.PathBase}{request.Path}{request.QueryString}";
    var separator = request.QueryString.HasValue ? "&" : "?";
    return $"{currentUri}{separator}{RecoveryQueryParameter}";
  }
}
