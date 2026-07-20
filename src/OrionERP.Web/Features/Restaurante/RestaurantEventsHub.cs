using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace OrionERP.Web.Features.Restaurante;

[Authorize(Roles = "Administrador,RestauranteAdmin,RestauranteSupervisor,RestauranteCaja,RestauranteCocina,RestaurantePantalla")]
public sealed class RestaurantEventsHub : Hub
{
  public async Task Subscribe(string rfc, int siteId)
  {
    var normalizedRfc = rfc?.Trim().ToUpperInvariant();
    var canReadRfc = Context.User?.Claims.Any(claim =>
      claim.Type == "rfc" && string.Equals(claim.Value, normalizedRfc, StringComparison.OrdinalIgnoreCase)) == true;
    if (!canReadRfc || siteId <= 0)
    {
      throw new HubException("No tiene acceso al RFC o sede solicitados.");
    }
    await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(normalizedRfc!, siteId));
  }

  public static string GroupName(string rfc, int siteId)
    => $"restaurant:{rfc.Trim().ToUpperInvariant()}:{siteId}";
}
