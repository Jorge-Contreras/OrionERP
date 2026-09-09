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
    /* Dos niveles, no uno: quien administra Hospedaje y quien sólo puede consultarlo. La ruta
       de cada página decide a cuál se expone, y el calendario acota al arrendador de la sesión
       a sus propias habitaciones. Exigir aquí el rol de administración dejaba fuera a los roles
       que las rutas sí autorizan, que es como los arrendadores perdieron el calendario. */
    if (user.Identity?.IsAuthenticated != true ||
        !(HospitalitySessionRoles.Administration.Any(user.IsInRole)
          || HospitalitySessionRoles.ReadOnly.Any(user.IsInRole)))
      throw new UnauthorizedAccessException("La sesión no tiene permiso para consultar Hospedaje.");

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
