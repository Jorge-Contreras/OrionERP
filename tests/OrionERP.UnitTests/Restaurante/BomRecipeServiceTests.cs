using System.Data;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Restaurante;
using OrionERP.Infrastructure.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class BomRecipeServiceTests
{
  [Fact]
  public async Task SaveDraftAsync_ReturnsValidationError_WhenBaseUnitsAreMissing()
  {
    var service = new BomRecipeService(new UnavailableConnectionFactory());
    var request = new BomDraftSaveRequest
    {
      Rfc = "OHM191112Q26",
      ProductMaterialId = 10,
      YieldQuantity = 1,
      YieldUnitId = 0,
      Components =
      [
        new BomComponentSaveRequest
        {
          MaterialId = 20,
          Quantity = 1,
          UnitId = 0
        }
      ]
    };

    var result = await service.SaveDraftAsync(request);

    Assert.False(result.Success);
    Assert.Equal("Selecciona el producto, los ingredientes y sus unidades base.", result.Message);
  }

  private sealed class UnavailableConnectionFactory : IDbConnectionFactory
  {
    public IDbConnection Create() => throw new InvalidOperationException("Validation should run before opening a database connection.");
  }
}
