using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Infrastructure.Features.Logistica.Materials;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Logistica;

public class MaterialServicePagingTests
{
  [Fact]
  public async Task GetMaterialsAsync_AddsPagingAndFilterParameters_WhenTakeIsProvided()
  {
    var connection = new FakeQueryDbConnection();
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    await service.GetMaterialsAsync(new MaterialFilter
    {
      Rfc = "OHM191112Q26",
      SearchText = "aceite",
      CategoryId = 3,
      VendorId = 5,
      MaterialClass = "Consumable",
      Status = "ACTIVO",
      HasImage = true,
      Skip = 50,
      Take = 51
    });

    Assert.NotNull(connection.LastCommandText);
    Assert.Contains("OFFSET @Skip ROWS", connection.LastCommandText!, StringComparison.Ordinal);
    Assert.Contains("FETCH NEXT @Take ROWS ONLY;", connection.LastCommandText!, StringComparison.Ordinal);
    Assert.Contains("m.CategoryId = @CategoryId", connection.LastCommandText!, StringComparison.Ordinal);
    Assert.Contains("m.BusinessPartnerId = @VendorId", connection.LastCommandText!, StringComparison.Ordinal);
    Assert.Contains("m.MaterialClass = @MaterialClass", connection.LastCommandText!, StringComparison.Ordinal);
    Assert.Contains("m.MaterialStatus = @Status", connection.LastCommandText!, StringComparison.Ordinal);
    Assert.Contains("m.PrimaryImage IS NOT NULL", connection.LastCommandText!, StringComparison.Ordinal);

    AssertParameter(connection.LastParameters, "@Search", "%aceite%");
    AssertParameter(connection.LastParameters, "@CategoryId", 3);
    AssertParameter(connection.LastParameters, "@VendorId", 5);
    AssertParameter(connection.LastParameters, "@MaterialClass", "Consumable");
    AssertParameter(connection.LastParameters, "@Status", "ACTIVO");
    AssertParameter(connection.LastParameters, "@Skip", 50);
    AssertParameter(connection.LastParameters, "@Take", 51);
  }

  [Fact]
  public async Task GetMaterialsAsync_OmitsPagingClause_WhenTakeIsZero()
  {
    var connection = new FakeQueryDbConnection();
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    await service.GetMaterialsAsync(new MaterialFilter
    {
      Rfc = "OHM191112Q26",
      SearchText = "filtro",
      Skip = 25,
      Take = 0
    });

    Assert.NotNull(connection.LastCommandText);
    Assert.DoesNotContain("OFFSET @Skip ROWS", connection.LastCommandText!, StringComparison.Ordinal);
    Assert.DoesNotContain("FETCH NEXT @Take ROWS ONLY;", connection.LastCommandText!, StringComparison.Ordinal);
    Assert.DoesNotContain(connection.LastParameters, parameter => HasParameterName(parameter, "@Skip"));
    Assert.DoesNotContain(connection.LastParameters, parameter => HasParameterName(parameter, "@Take"));
  }

  private static void AssertParameter(IReadOnlyList<FakeQueryParameter> parameters, string name, object expectedValue)
  {
    var parameter = Assert.Single(parameters, parameter => HasParameterName(parameter, name));
    Assert.Equal(expectedValue, parameter.Value);
  }

  private static bool HasParameterName(FakeQueryParameter parameter, string expectedName)
    => string.Equals(parameter.Name.TrimStart('@'), expectedName.TrimStart('@'), StringComparison.OrdinalIgnoreCase);
}
