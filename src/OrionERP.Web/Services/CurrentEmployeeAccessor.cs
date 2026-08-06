using Microsoft.AspNetCore.Components.Authorization;
using OrionERP.Application.Features.CapitalHumano.Workforce;

namespace OrionERP.Web.Services;

public sealed class CurrentEmployeeAccessor : ICurrentEmployeeAccessor
{
  private readonly AuthenticationStateProvider _authenticationStateProvider;
  private readonly IHttpContextAccessor _httpContextAccessor;

  public CurrentEmployeeAccessor(AuthenticationStateProvider authenticationStateProvider, IHttpContextAccessor httpContextAccessor)
  {
    _authenticationStateProvider = authenticationStateProvider;
    _httpContextAccessor = httpContextAccessor;
  }

  public async ValueTask<CurrentEmployeeContext?> GetCurrentAsync(CancellationToken ct = default)
  {
    var user = _httpContextAccessor.HttpContext?.User;
    if (user?.Identity?.IsAuthenticated != true)
      user = (await _authenticationStateProvider.GetAuthenticationStateAsync()).User;
    if (user.Identity?.IsAuthenticated != true) return null;
    var employeeId = int.TryParse(user.FindFirst("employee_id")?.Value, out var parsed) ? parsed : (int?)null;
    var roles = user.FindAll(System.Security.Claims.ClaimTypes.Role)
      .Select(claim => claim.Value)
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var rfcs = user.FindAll("rfc")
      .Select(claim => claim.Value.Trim().ToUpperInvariant())
      .Where(value => value.Length > 0)
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var employeeRfc = user.FindFirst("employee_rfc")?.Value.Trim().ToUpperInvariant();
    return new CurrentEmployeeContext(user.Identity.Name ?? "OrionERP", employeeId, roles, rfcs, employeeRfc);
  }
}
