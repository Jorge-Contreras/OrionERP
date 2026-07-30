using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using OrionERP.Web.Features.Auth.AdminPortal;
using OrionERP.Web.Features.Logistica.Locations;
using OrionERP.Web.Features.Logistica.Materials;
using OrionERP.Web.Features.Logistica.PhysicalCounts;
using OrionERP.Web.Features.Logistica.Purchasing;
using OrionERP.Web.Features.Logistica.Vendors;
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

  [Theory]
  [InlineData(typeof(MaterialesPage), "/logistica/materiales", "Administrador,Logistica")]
  [InlineData(typeof(ProveedoresLogisticaPage), "/logistica/proveedores", "Administrador,Logistica")]
  [InlineData(typeof(ComprasPage), "/logistica/compras", "Administrador,Logistica")]
  [InlineData(typeof(UbicacionesPage), "/logistica/ubicaciones", "Administrador,Logistica")]
  [InlineData(typeof(ConteosFisicosPage), "/logistica/conteos", "Administrador,Logistica,Conteo")]
  public void LogisticsRoutes_AcceptTheLogisticaRole(Type componentType, string route, string roles)
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

  [Theory]
  [InlineData("/restaurante/admin", "RestaurantAdmin")]
  [InlineData("/restaurante/menus", "RestaurantAdmin")]
  [InlineData("/restaurante/recetas", "RestaurantAdmin")]
  [InlineData("/restaurante/produccion", "RestaurantAdmin")]
  [InlineData("/restaurante/inventario", "RestaurantAdmin")]
  [InlineData("/restaurante/reportes", "RestaurantAdmin")]
  [InlineData("/restaurante/configuracion", "RestaurantAdmin")]
  [InlineData("/restaurante/pos", "RestaurantPos")]
  [InlineData("/restaurante/ordenes", "RestaurantPos")]
  [InlineData("/restaurante/cocina", "RestaurantKitchen")]
  [InlineData("/restaurante/turnos", "RestaurantCash")]
  [InlineData("/restaurante/pantalla", "RestaurantDisplay")]
  public void RestaurantRoutes_RequireTheirRfcAwarePolicy(string route, string policy)
  {
    var componentType = typeof(ListaReservacionesPage).Assembly
      .GetTypes()
      .Single(type => type.GetCustomAttributes<RouteAttribute>()
        .Any(attribute => attribute.Template == route));

    var authorizeAttributes = componentType.GetCustomAttributes<AuthorizeAttribute>().ToArray();

    Assert.Contains(authorizeAttributes, attribute => attribute.Policy == policy);
  }
}
