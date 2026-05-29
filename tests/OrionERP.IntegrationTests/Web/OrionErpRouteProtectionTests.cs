using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using OrionERP.Web.Features.Auth.AdminPortal;
using OrionERP.Web.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.IntegrationTests.Web;

public class OrionErpRouteProtectionTests
{
  [Theory]
  [InlineData(typeof(ListaReservacionesPage), "/reservaciones/lista", "Administrador,SatOperator")]
  [InlineData(typeof(IdentityAdminPage), "/admin/seguridad", "Administrador")]
  public void ProtectedErpRoutes_RetainAuthorizeMetadata(Type componentType, string route, string roles)
  {
    var routeTemplates = componentType.GetCustomAttributes<RouteAttribute>()
      .Select(attribute => attribute.Template)
      .ToArray();
    var authorizeAttributes = componentType.GetCustomAttributes<AuthorizeAttribute>().ToArray();

    Assert.Contains(route, routeTemplates);
    Assert.Contains(authorizeAttributes, attribute => attribute.Roles == roles);
  }

  [Fact]
  public void BonhomiaRoute_IsNoLongerAnErpComponent()
  {
    var routes = typeof(ListaReservacionesPage).Assembly
      .GetTypes()
      .SelectMany(type => type.GetCustomAttributes<RouteAttribute>())
      .Select(attribute => attribute.Template);

    Assert.DoesNotContain("/bonhomia", routes);
  }
}
