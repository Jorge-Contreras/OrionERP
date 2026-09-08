using Microsoft.Extensions.Configuration;
using OrionERP.Application.Features.Ajustes.Catalogos;
using OrionERP.Infrastructure.Features.Ajustes.Catalogos;

namespace OrionERP.UnitTests.Reservaciones;

public sealed class HospitalityCatalogScopeTests
{
  private static CatalogoService CreateService() => new(new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:OrionDb"] = "not a valid connection" }).Build());

  [Fact]
  public async Task SharedLegacyOwners_CannotBeCreatedChangedOrDeletedThroughGenericCatalog()
  {
    var service = CreateService();
    Assert.False((await service.SaveItemAsync(new() { Key = CatalogoKey.Arrendadores, Nombre = "Owner" })).Success);
    Assert.False((await service.SaveItemAsync(new() { Key = CatalogoKey.Arrendadores, Id = "123", Nombre = "Changed" })).Success);
    Assert.False((await service.DeleteItemAsync(CatalogoKey.Arrendadores, "123", "RFC-A")).Success);
  }

  [Fact]
  public async Task Owners_RequireHospitalityScopeBeforeOpeningSql()
    => await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
      CreateService().GetItemsAsync(CatalogoKey.Arrendadores, "RFC-A", null, false));

  [Fact]
  public async Task Projects_RequireAuthenticatedCompanyBeforeOpeningSql()
    => await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
      CreateService().GetItemsAsync(CatalogoKey.Proyectos, "RFC-A", null, false));
}
