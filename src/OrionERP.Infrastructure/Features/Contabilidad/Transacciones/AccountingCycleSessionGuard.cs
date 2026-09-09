using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using OrionERP.Application.Common;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Infrastructure.Features.Contabilidad.Transacciones;

/// <summary>
/// Revalida el permiso en cada transición del ciclo, no sólo al abrir la pantalla:
/// un circuito Blazor vivo sobrevive a la revocación del rol. Calcado de
/// HospitalityAdministrationSessionGuard.
/// </summary>
public sealed class AccountingCycleSessionGuard(
  AuthenticationStateProvider authenticationStateProvider,
  ICurrentCompanyContext companyContext)
{
  /// <summary>Empresa y usuario de la operación. Publicar o reversar exige ambos.</summary>
  public async Task<(long CompanyId, string CompanyRfc, string Actor)> RequireAsync(CancellationToken ct = default)
  {
    ct.ThrowIfCancellationRequested();
    var state = await authenticationStateProvider.GetAuthenticationStateAsync();
    ct.ThrowIfCancellationRequested();
    var user = state.User;
    if (user.Identity?.IsAuthenticated != true ||
        !(user.IsInRole("Administrador") || user.IsInRole("SatOperator")))
      throw new UnauthorizedAccessException("La sesión no tiene permiso para publicar o reversar pólizas.");

    var actor = user.FindFirstValue(ClaimTypes.Name)
      ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
    var rfcs = user.FindAll(CompanyClaimTypes.Rfc).Select(claim => claim.Value.Trim().ToUpperInvariant()).ToArray();
    if (string.IsNullOrWhiteSpace(actor) || rfcs.Length != 1 || string.IsNullOrWhiteSpace(rfcs[0]))
      throw new UnauthorizedAccessException("El ciclo contable exige una sesión con un usuario y una sola empresa.");

    companyContext.EnsureRfc(rfcs[0]);
    var companyId = await companyContext.RequireCompanyIdAsync(ct);
    return (companyId, rfcs[0], actor);
  }
}
