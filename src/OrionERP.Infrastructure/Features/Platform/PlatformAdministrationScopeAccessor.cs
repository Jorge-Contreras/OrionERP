using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Platform;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Infrastructure.Features.Platform;

public sealed class PlatformAdministrationScopeAccessor : IPlatformAdministrationScopeAccessor
{
  private readonly AuthenticationStateProvider _authenticationStateProvider;
  private readonly ICurrentCompanyContext _companyContext;
  private readonly IPlatformAdministrationAccessValidator _accessValidator;

  public PlatformAdministrationScopeAccessor(
    AuthenticationStateProvider authenticationStateProvider,
    ICurrentCompanyContext companyContext,
    IPlatformAdministrationAccessValidator accessValidator)
  {
    _authenticationStateProvider = authenticationStateProvider;
    _companyContext = companyContext;
    _accessValidator = accessValidator;
  }

  public async Task<PlatformAdministrationScope> GetRequiredScopeAsync(CancellationToken ct = default)
  {
    ct.ThrowIfCancellationRequested();
    var authenticationState = await _authenticationStateProvider.GetAuthenticationStateAsync();
    ct.ThrowIfCancellationRequested();
    var user = authenticationState.User;

    if (user.Identity?.IsAuthenticated != true)
      throw new UnauthorizedAccessException("La administración de plataforma requiere una sesión autenticada.");
    if (!user.IsInRole(PlatformAdministrationAuthorization.AdministratorRole))
      throw new UnauthorizedAccessException("La sesión no tiene permiso para administrar la plataforma.");

    var actorUserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(actorUserId))
      throw new UnauthorizedAccessException("La sesión no identifica al administrador.");

    var claimRfcs = user.FindAll(CompanyClaimTypes.Rfc)
      .Select(claim => NormalizeRfc(claim.Value))
      .Where(rfc => rfc.Length > 0)
      .Distinct(StringComparer.Ordinal)
      .ToArray();
    if (claimRfcs.Length != 1)
      throw new UnauthorizedAccessException("La sesión administrativa debe estar ligada a una sola empresa.");

    var contextRfc = NormalizeRfc(_companyContext.RequireRfc());
    if (!string.Equals(contextRfc, claimRfcs[0], StringComparison.Ordinal))
      throw new UnauthorizedAccessException("La empresa de la sesión no coincide con el contexto activo.");

    // Blazor Server circuits can outlive a membership or role change. Claims
    // remain a useful first boundary, but authorization is granted only after
    // a fresh check of the authoritative Identity tables on every operation.
    await _accessValidator.EnsureAuthorizedAsync(actorUserId, contextRfc, ct);

    return new PlatformAdministrationScope(actorUserId, contextRfc);
  }

  private static string NormalizeRfc(string? value)
    => (value ?? string.Empty).Trim().ToUpperInvariant();
}
