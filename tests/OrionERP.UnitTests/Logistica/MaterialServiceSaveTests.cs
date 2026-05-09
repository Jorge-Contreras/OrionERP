using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Infrastructure.Features.Logistica.Materials;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Logistica;

public class MaterialServiceSaveTests
{
  [Fact]
  public async Task SaveMaterialAsync_SeparatesWhereClause_WhenUpdatingWithoutNewImage()
  {
    var connection = new FakeQueryDbConnection
    {
      NonQueryResultFactory = (_, _) => 1
    };
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    var result = await service.SaveMaterialAsync(new MaterialUpsertRequest
    {
      Id = 42,
      Description = "Aceite hidraulico",
      BaseUnitId = 1,
      PurchaseQuantity = 1m,
      Status = "ACTIVO",
      MaterialClass = "Consumable",
      IsActive = true
    });

    Assert.True(result.Success);

    var updateCommand = Assert.Single(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("UPDATE logistica.Material", StringComparison.Ordinal));

    Assert.DoesNotContain("@IsActiveWHERE", updateCommand.CommandText, StringComparison.Ordinal);
    Assert.Contains("IsActive = @IsActive", updateCommand.CommandText, StringComparison.Ordinal);
    Assert.Contains("WHERE Id = @Id;", updateCommand.CommandText, StringComparison.Ordinal);
  }
}
