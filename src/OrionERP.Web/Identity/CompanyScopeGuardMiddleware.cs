using OrionERP.Infrastructure.Auth;

namespace OrionERP.Web.Identity;

public sealed class CompanyScopeGuardMiddleware
{
  private static readonly string[] ScopedQueryKeys = ["rfc", "companyRfc", "empresaRfc"];
  private readonly RequestDelegate _next;

  public CompanyScopeGuardMiddleware(RequestDelegate next) => _next = next;

  public async Task InvokeAsync(HttpContext context)
  {
    var user = context.User;
    if (user.Identity?.IsAuthenticated != true || context.Request.Path.StartsWithSegments("/company-branding"))
    {
      await _next(context);
      return;
    }

    var sessionRfc = Normalize(user.FindFirst(CompanyClaimTypes.Rfc)?.Value);
    if (sessionRfc is null)
    {
      await _next(context);
      return;
    }

    var requestedRfcs = context.Request.RouteValues
      .Where(pair => string.Equals(pair.Key, "rfc", StringComparison.OrdinalIgnoreCase))
      .Select(pair => Normalize(pair.Value?.ToString()))
      .Concat(context.Request.Query
        .Where(pair => ScopedQueryKeys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
        .SelectMany(pair => pair.Value.Select(Normalize)))
      .Where(value => value is not null)
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToArray();

    if (requestedRfcs.Any(value => !string.Equals(value, sessionRfc, StringComparison.OrdinalIgnoreCase)))
    {
      context.Response.StatusCode = StatusCodes.Status403Forbidden;
      if (context.Request.GetTypedHeaders().Accept?.Any(value => value.MediaType.Value?.Contains("json", StringComparison.OrdinalIgnoreCase) == true) == true)
        await context.Response.WriteAsJsonAsync(new { error = "El RFC solicitado no corresponde a la empresa de la sesión." });
      else
        await context.Response.WriteAsync("Acceso denegado: el RFC solicitado no corresponde a la empresa de la sesión.");
      return;
    }

    await _next(context);
  }

  private static string? Normalize(string? value)
  {
    var normalized = value?.Trim().ToUpperInvariant();
    return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
  }
}
