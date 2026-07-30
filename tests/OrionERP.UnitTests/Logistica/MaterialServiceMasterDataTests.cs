using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Infrastructure.Features.Logistica.Materials;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Logistica;

public class MaterialServiceMasterDataTests
{
  [Fact]
  public async Task CreateCategoryAsync_UsesRfcScopeAndReturnsCreatedLookup()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = (_, _) => 17
    };
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    var result = await service.CreateCategoryAsync(new MaterialCategoryCreateRequest
    {
      Rfc = "OHM191112Q26",
      Name = "  Blancos y textiles  ",
      Description = "Toallas y ropa de cama"
    });

    Assert.True(result.Success);
    Assert.Equal(17, result.EntityId);
    Assert.NotNull(connection.LastCommandText);
    Assert.Contains("FROM logistica.MaterialCategory", connection.LastCommandText!, StringComparison.Ordinal);
    Assert.Contains("WHERE Rfc = @Rfc", connection.LastCommandText!, StringComparison.Ordinal);
    Assert.Contains("SET IsActive = 1", connection.LastCommandText!, StringComparison.Ordinal);
    Assert.Contains("INSERT INTO logistica.MaterialCategory", connection.LastCommandText!, StringComparison.Ordinal);
    AssertParameter(connection.LastParameters, "Rfc", "OHM191112Q26");
    AssertParameter(connection.LastParameters, "Name", "Blancos y textiles");
  }

  [Fact]
  public async Task CreateUnitAsync_ReactivatesOrCreatesSharedUnit()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = (_, _) => 23
    };
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    var result = await service.CreateUnitAsync(new UnitOfMeasureCreateRequest
    {
      Name = "  Caja de 12  ",
      Abbreviation = " CJ12 ",
      Description = "Presentación de proveedor"
    });

    Assert.True(result.Success);
    Assert.Equal(23, result.EntityId);
    Assert.NotNull(connection.LastCommandText);
    Assert.Contains("FROM logistica.UnitOfMeasure", connection.LastCommandText!, StringComparison.Ordinal);
    Assert.Contains("SET IsActive = 1", connection.LastCommandText!, StringComparison.Ordinal);
    Assert.Contains("INSERT INTO logistica.UnitOfMeasure", connection.LastCommandText!, StringComparison.Ordinal);
    AssertParameter(connection.LastParameters, "Name", "Caja de 12");
    AssertParameter(connection.LastParameters, "Abbreviation", "CJ12");
  }

  [Fact]
  public async Task QuickCreate_ReturnsFriendlyValidation_WhenNameIsMissing()
  {
    var connection = new FakeQueryDbConnection();
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    var categoryResult = await service.CreateCategoryAsync(new MaterialCategoryCreateRequest
    {
      Rfc = "OHM191112Q26",
      Name = " "
    });
    var unitResult = await service.CreateUnitAsync(new UnitOfMeasureCreateRequest
    {
      Name = " "
    });

    Assert.False(categoryResult.Success);
    Assert.Contains("nombre", categoryResult.Message, StringComparison.OrdinalIgnoreCase);
    Assert.False(unitResult.Success);
    Assert.Contains("nombre", unitResult.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(connection.ExecutedCommands);
  }

  private static void AssertParameter(
    IReadOnlyList<FakeQueryParameter> parameters,
    string expectedName,
    object expectedValue)
  {
    var parameter = Assert.Single(
      parameters,
      parameter => string.Equals(
        parameter.Name.TrimStart('@'),
        expectedName.TrimStart('@'),
        StringComparison.OrdinalIgnoreCase));

    Assert.Equal(expectedValue, parameter.Value);
  }
}
