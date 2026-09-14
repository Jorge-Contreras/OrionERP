using OrionERP.Application.Features.Platform;

namespace OrionERP.UnitTests.Platform;

public sealed class ModuleRoutesTests
{
  [Theory]
  [InlineData("/reservaciones/lista", PlatformModuleCodes.Hospitality)]
  [InlineData("reservaciones/123", PlatformModuleCodes.Hospitality)]
  [InlineData("/Reservaciones/sede?returnUrl=%2F", PlatformModuleCodes.Hospitality)]
  [InlineData("/arrendadores", PlatformModuleCodes.Hospitality)]
  [InlineData("/restaurante/pos", PlatformModuleCodes.Restaurant)]
  [InlineData("RESTAURANTE/", PlatformModuleCodes.Restaurant)]
  public void RequiredModuleFor_MapsModuleZones(string path, string expected)
    => Assert.Equal(expected, ModuleRoutes.RequiredModuleFor(path));

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("/")]
  [InlineData("/contabilidad/transacciones/5")]
  [InlineData("/ordenes-trabajo")]
  [InlineData("/logistica/ubicaciones")]
  [InlineData("/ajustes/catalogos?tab=arrendadores")]
  [InlineData("/restaurantes-cercanos")]
  [InlineData("/cfdi/declaracion-previa#reservaciones")]
  public void RequiredModuleFor_LeavesUniversalRoutesOpen(string? path)
    => Assert.Null(ModuleRoutes.RequiredModuleFor(path));

  [Fact]
  public void IsAvailable_RequiresTheZoneModuleOnly()
  {
    var restaurantOnly = new HashSet<string> { PlatformModuleCodes.AccountingCore, PlatformModuleCodes.Restaurant };

    Assert.True(ModuleRoutes.IsAvailable("/restaurante/pos", restaurantOnly));
    Assert.False(ModuleRoutes.IsAvailable("/reservaciones/lista", restaurantOnly));
    Assert.False(ModuleRoutes.IsAvailable("/arrendadores", restaurantOnly));
    Assert.True(ModuleRoutes.IsAvailable("/contabilidad/Bancos", restaurantOnly));
  }
}
