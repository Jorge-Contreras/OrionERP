using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace OrionERP.Web.Features.Restaurante;

[Authorize(Roles = "Administrador,RestauranteAdmin,RestauranteSupervisor,RestauranteCaja,RestauranteCocina,RestaurantePantalla")]
public sealed class RestaurantEventsHub : Hub
{
  public async Task Subscribe(string rfc, int siteId)
  {
    var normalizedRfc = rfc?.Trim().ToUpperInvariant();
    var sessionRfcs = Context.User?.Claims
      .Where(claim => claim.Type == "rfc")
      .Select(claim => claim.Value.Trim().ToUpperInvariant())
      .Where(value => value.Length > 0)
      .ToArray() ?? [];
    var canReadRfc = sessionRfcs.Length == 1
      && string.Equals(sessionRfcs[0], normalizedRfc, StringComparison.OrdinalIgnoreCase);
    if (!canReadRfc || siteId <= 0)
    {
      throw new HubException("No tiene acceso al RFC o sede solicitados.");
    }
    await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(normalizedRfc!, siteId));
  }

  public static string GroupName(string rfc, int siteId)
    => $"restaurant:{rfc.Trim().ToUpperInvariant()}:{siteId}";
}
