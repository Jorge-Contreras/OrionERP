using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Reservaciones;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Infrastructure.Features.Reservaciones;

/// <summary>Revalidates revocable user access on every operation, including existing Blazor circuits.</summary>
public sealed class HospitalityAdministrationSessionGuard(
  AuthenticationStateProvider authenticationStateProvider,
  ICurrentCompanyContext companyContext,
  IHospitalityAdministrationAccessValidator accessValidator)
{
  public async Task<string> RequireCompanyRfcAsync(CancellationToken ct = default)
  {
    ct.ThrowIfCancellationRequested();
    var state = await authenticationStateProvider.GetAuthenticationStateAsync();
    ct.ThrowIfCancellationRequested();
    var user = state.User;
    if (user.Identity?.IsAuthenticated != true ||
        !(user.IsInRole("Administrador") || user.IsInRole("SatOperator")))
      throw new UnauthorizedAccessException("La sesión no tiene permiso para administrar Hospedaje.");

    var actorId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    var rfcs = user.FindAll(CompanyClaimTypes.Rfc).Select(claim => claim.Value.Trim().ToUpperInvariant()).ToArray();
    if (string.IsNullOrWhiteSpace(actorId) || rfcs.Length != 1 || string.IsNullOrWhiteSpace(rfcs[0]))
      throw new UnauthorizedAccessException("La sesión de Hospedaje debe identificar un usuario y una sola empresa.");

    companyContext.EnsureRfc(rfcs[0]);
    await accessValidator.EnsureAuthorizedAsync(actorId, rfcs[0], ct);
    companyContext.EnsureRfc(rfcs[0]);
    return rfcs[0];
  }
}
