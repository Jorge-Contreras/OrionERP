using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using OrionERP.Application.Features.Reservaciones;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Infrastructure.Features.Reservaciones;

/// <summary>
/// Resuelve el dueño de la sesión desde la identidad, no desde lo que pida la pantalla: quien
/// consulta no elige a qué arrendador se acota. Abre su propio <see cref="OrionIdentityDbContext"/>
/// por la misma razón que <see cref="HospitalityAdministrationAccessValidator"/>: una página puede
/// pedir varias conexiones a la vez y un DbContext compartido no lo tolera.
/// </summary>
public sealed class HospitalityViewerScopeResolver(
  AuthenticationStateProvider authenticationStateProvider,
  DbContextOptions<OrionIdentityDbContext> options) : IHospitalityViewerScope
{
  public async Task<int?> ResolveOwnerProveedorIdAsync(CancellationToken ct = default)
  {
    ct.ThrowIfCancellationRequested();
    var state = await authenticationStateProvider.GetAuthenticationStateAsync();
    var user = state.User;

    /* Quien administra ve el conjunto completo de su empresa; el acotamiento es para quien
       únicamente es arrendador, igual que en la página de estados de cuenta. */
    if (!user.IsInRole("Arrendadores") || HospitalitySessionRoles.Administration.Any(user.IsInRole))
      return null;

    var actorId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(actorId))
      throw new UnauthorizedAccessException("La sesión no identifica al usuario.");

    await using var db = new OrionIdentityDbContext(options);
    var proveedorId = await db.Users.AsNoTracking()
      .Where(candidate => candidate.Id == actorId)
      .Select(candidate => candidate.ArrendadorProveedorId)
      .SingleOrDefaultAsync(ct);

    /* Sin proveedor ligado no hay nada que pueda ver. Devolver null aquí lo dejaría sin filtro,
       que es exactamente el error que este tipo existe para impedir. */
    return proveedorId ?? throw new UnauthorizedAccessException(
      "Tu usuario tiene el rol Arrendadores, pero no tiene un proveedor ligado. Pide que lo liguen en el portal de seguridad.");
  }
}
