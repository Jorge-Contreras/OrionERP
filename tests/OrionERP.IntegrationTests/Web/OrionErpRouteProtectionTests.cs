using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using OrionERP.Web.Features.Auth.AdminPortal;
using OrionERP.Web.Features.CapitalHumano.Workforce;
using OrionERP.Web.Features.Logistica.Locations;
using OrionERP.Web.Features.Logistica.Materials;
using OrionERP.Web.Features.Logistica.PhysicalCounts;
using OrionERP.Web.Features.Logistica.Purchasing;
using OrionERP.Web.Features.Logistica.Vendors;
using OrionERP.Web.Features.Platform;
using OrionERP.Web.Features.Reservaciones.ListaReservaciones;
using OrionERP.Web.Identity;

namespace OrionERP.IntegrationTests.Web;

public class OrionErpRouteProtectionTests
{
  [Theory]
  [InlineData(typeof(ListaReservacionesPage), "/reservaciones/lista", "Administrador,SatOperator")]
  [InlineData(typeof(IdentityAdminPage), "/admin/seguridad", "Administrador")]
  [InlineData(typeof(PlatformAdministrationPage), "/admin/plataforma", "Administrador")]
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
  public void LogisticsRoutes_AcceptTheLogisticaRole(Type componentType, string route, string roles)
  {
    var routeTemplates = componentType.GetCustomAttributes<RouteAttribute>()
      .Select(attribute => attribute.Template)
      .ToArray();
    var authorizeAttributes = componentType.GetCustomAttributes<AuthorizeAttribute>().ToArray();

    Assert.Contains(route, routeTemplates);
    Assert.Contains(authorizeAttributes, attribute => attribute.Roles == roles);
  }

  [Theory]
  [InlineData("/reservaciones/sede", CompanyOperationPolicies.HospitalitySite)]
  [InlineData("/ordenes-trabajo", CompanyOperationPolicies.WorkOrders)]
  [InlineData("/ordenes-trabajo/{Id:int}", CompanyOperationPolicies.WorkOrders)]
  [InlineData("/logistica/ubicaciones", CompanyOperationPolicies.Logistics)]
  [InlineData("/logistica/compras", CompanyOperationPolicies.Logistics)]
  [InlineData("/logistica/conteos", CompanyOperationPolicies.PhysicalCounts)]
  [InlineData("/restaurante/pos", "RestaurantPos")]
  public void TargetOperations_RequireRevocableCompanyPolicy(string route, string policy)
  {
    var componentType = typeof(ListaReservacionesPage).Assembly
      .GetTypes()
      .Single(type => type.GetCustomAttributes<RouteAttribute>()
        .Any(attribute => attribute.Template == route));

    Assert.Contains(componentType.GetCustomAttributes<AuthorizeAttribute>(),
      attribute => attribute.Policy == policy && attribute.Roles is null);
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
  [InlineData("/restaurante/recetas/configuracion", "RestaurantAdmin")]
  [InlineData("/restaurante/produccion", "RestaurantAdmin")]
  [InlineData("/restaurante/inventario", "RestaurantAdmin")]
  [InlineData("/restaurante/reportes", "RestaurantAdmin")]
  [InlineData("/restaurante/promociones", "RestaurantAdmin")]
  [InlineData("/restaurante/sitio-publico", "RestaurantAdmin")]
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

  [Theory]
  [InlineData(typeof(MiTrabajoPage), "/mi-trabajo", "CapitalHumanoEmployee")]
  [InlineData(typeof(MiEquipoPage), "/mi-equipo", "CapitalHumanoSupervisor")]
  [InlineData(typeof(AttendanceAdminPage), "/capital-humano/asistencia", "CapitalHumanoManagement")]
  [InlineData(typeof(WorkforceConfigurationPage), "/capital-humano/configuracion-tiempo", "CapitalHumanoAdmin")]
  [InlineData(typeof(AbsencesAdminPage), "/capital-humano/ausencias", "CapitalHumanoAdmin")]
  [InlineData(typeof(PrenominaPage), "/capital-humano/pre-nomina", "CapitalHumanoNomina")]
  public void WorkforceRoutes_RequireTheirExplicitPolicy(Type componentType, string route, string policy)
  {
    Assert.Contains(componentType.GetCustomAttributes<RouteAttribute>(), attribute => attribute.Template == route);
    Assert.Contains(componentType.GetCustomAttributes<AuthorizeAttribute>(), attribute => attribute.Policy == policy);
  }

  [Fact]
  public void KioskRoute_IsExplicitlyAnonymous()
  {
    Assert.Contains(typeof(KioskPage).GetCustomAttributes<RouteAttribute>(), attribute => attribute.Template == "/asistencia/kiosco");
    Assert.NotEmpty(typeof(KioskPage).GetCustomAttributes<AllowAnonymousAttribute>());
  }
}
