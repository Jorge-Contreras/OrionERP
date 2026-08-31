using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Infrastructure.Features.Logistica.Materials;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Logistica;

/// <summary>
/// El catálogo de materiales se muestra en Logística, Compras, Conteos y Restaurante, y cada
/// módulo había elegido su propio orden. Estas pruebas fijan el acuerdo: descripción ascendente,
/// código como desempate, en todas partes.
/// </summary>
public class MaterialSortOrderTests
{
  [Fact]
  public void SqlKeys_PutsDescriptionBeforeCode()
    => Assert.Equal("m.[Description], m.MaterialCode", MaterialSortOrder.SqlKeys("m"));

  [Fact]
  public void Key_PutsDescriptionBeforeCode()
    => Assert.Equal("AZÚCAR|MAT-000123", MaterialSortOrder.Key("AZÚCAR", "MAT-000123"));

  [Fact]
  public async Task GetMaterialsAsync_OrdersByDescription()
  {
    var connection = new FakeQueryDbConnection();
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    await service.GetMaterialsAsync(new MaterialFilter { Rfc = "OHM191112Q26", Take = 25 });

    Assert.NotNull(connection.LastCommandText);
    Assert.Contains("ORDER BY m.[Description], m.MaterialCode, m.Id", connection.LastCommandText!, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("src/OrionERP.Infrastructure/Features/Logistica/PhysicalCounts/PhysicalCountService.cs", "ORDER BY m.[Description], m.MaterialCode, line.Id;")]
  [InlineData("src/OrionERP.Infrastructure/Features/Logistica/Stock/InventoryMovementService.cs", "ORDER BY material.[Description],material.MaterialCode,locationInfo.LocationName;")]
  [InlineData("src/OrionERP.Infrastructure/Features/Logistica/Purchasing/PurchaseOrderService.cs", "ORDER BY line.MaterialDescriptionSnapshot, line.MaterialCodeSnapshot, line.Id;")]
  [InlineData("src/OrionERP.Infrastructure/Features/Logistica/Stock/StockService.cs", "MaterialSortOrder.SqlKeys(\"m\")")]
  public void ListQueries_OrderMaterialsByDescription(string relativePath, string expectedFragment)
    => Assert.Contains(expectedFragment, RepoFile.Read(relativePath), StringComparison.Ordinal);

  [Theory]
  [InlineData("src/OrionERP.Infrastructure/Features/Logistica/Purchasing/PurchaseOrderService.cs")]
  [InlineData("src/OrionERP.Web/Features/Logistica/Purchasing/ComprasPage.razor.cs")]
  [InlineData("src/OrionERP.Web/Features/Restaurante/RestaurantMaterialOption.cs")]
  public void InMemoryOrdering_UsesTheSharedComparer(string relativePath)
  {
    var source = RepoFile.Read(relativePath);

    Assert.Contains("MaterialSortOrder.Comparer", source, StringComparison.Ordinal);
    Assert.DoesNotContain(".OrderBy(item => item.MaterialCode", source, StringComparison.Ordinal);
    Assert.DoesNotContain(".OrderBy(current => current.MaterialCode", source, StringComparison.Ordinal);
    Assert.DoesNotContain(".OrderBy(group => group.First().MaterialCode", source, StringComparison.Ordinal);
  }

  [Fact]
  public void SaleReadiness_SortsIngredientsByName()
    => Assert.Contains(
      "MaterialSortOrder.Key(material.Name, material.Code)",
      RepoFile.Read("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantSaleReadinessService.cs"),
      StringComparison.Ordinal);
}
