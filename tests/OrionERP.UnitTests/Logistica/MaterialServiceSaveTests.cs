using System.Data;
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
      ReaderResultFactory = (_, _) => CreateLifecycleStateTable(isActive: true),
      NonQueryResultFactory = (_, _) => 1
    };
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    var result = await service.SaveMaterialAsync(new MaterialUpsertRequest
    {
      Rfc = "OHM191112Q26",
      Id = 42,
      Description = "Aceite hidraulico",
      BaseUnitId = 1,
      PurchaseQuantity = 1m,
      Status = "ACTIVO",
      MaterialClass = "Consumable"
    });

    Assert.True(result.Success);

    var updateCommand = Assert.Single(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("UPDATE logistica.Material", StringComparison.Ordinal));

    Assert.DoesNotContain("IsActive =", updateCommand.CommandText, StringComparison.Ordinal);
    Assert.Contains("WHERE Rfc = @Rfc AND Id = @Id;", updateCommand.CommandText, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SaveMaterialAsync_ClearsStoredImage_WhenRemovalIsRequested()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (_, _) => CreateLifecycleStateTable(isActive: true),
      NonQueryResultFactory = (_, _) => 1
    };
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    var result = await service.SaveMaterialAsync(new MaterialUpsertRequest
    {
      Rfc = "OHM191112Q26",
      Id = 42,
      Description = "Aceite hidraulico",
      BaseUnitId = 1,
      PurchaseQuantity = 1m,
      Status = "ACTIVO",
      MaterialClass = "Consumable",
      RemovePrimaryImage = true
    });

    Assert.True(result.Success);

    var updateCommand = Assert.Single(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("UPDATE logistica.Material", StringComparison.Ordinal));

    Assert.Contains("PrimaryImage = @PrimaryImage", updateCommand.CommandText, StringComparison.Ordinal);
    Assert.Contains("PrimaryImageThumbnail = @PrimaryImageThumbnail", updateCommand.CommandText, StringComparison.Ordinal);
    Assert.Contains(
      updateCommand.Parameters,
      parameter => string.Equals(parameter.Name.TrimStart('@'), "PrimaryImage", StringComparison.OrdinalIgnoreCase)
        && parameter.Value is null or DBNull);
  }

  [Fact]
  public async Task SaveMaterialAsync_PreservesInactiveLifecycleState()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (_, _) => CreateLifecycleStateTable(isActive: false),
      NonQueryResultFactory = (_, _) => 1
    };
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));

    var result = await service.SaveMaterialAsync(new MaterialUpsertRequest
    {
      Rfc = "OHM191112Q26",
      Id = 42,
      Description = "Aceite hidráulico corregido",
      BaseUnitId = 1,
      PurchaseQuantity = 1m,
      Status = "ACTIVO",
      MaterialClass = "Consumable"
    });

    Assert.True(result.Success);
    var update = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("UPDATE logistica.Material", StringComparison.Ordinal));
    Assert.DoesNotContain("IsActive =", update.CommandText, StringComparison.Ordinal);
    Assert.Contains(update.Parameters, parameter => parameter.Name.TrimStart('@') == "Status" && Equals(parameter.Value, "INACTIVO"));
  }

  [Fact]
  public async Task SaveMaterialAsync_RejectsInactiveStatusForActiveMaterial()
  {
    var connection = new FakeQueryDbConnection { ReaderResultFactory = (_, _) => CreateLifecycleStateTable(isActive: true) };
    var result = await new MaterialService(new FakeQueryConnectionFactory(connection)).SaveMaterialAsync(new MaterialUpsertRequest
    {
      Rfc = "OHM191112Q26",
      Id = 42,
      Description = "Aceite hidráulico",
      BaseUnitId = 1,
      PurchaseQuantity = 1m,
      Status = "INACTIVO",
      MaterialClass = "Consumable"
    });

    Assert.False(result.Success);
    Assert.Contains("revisión de retiro", result.Message, StringComparison.OrdinalIgnoreCase);
    Assert.True(connection.LastTransaction!.WasRolledBack);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("UPDATE logistica.Material", StringComparison.Ordinal));
  }

  private static DataTable CreateLifecycleStateTable(bool isActive)
  {
    var table = new DataTable();
    table.Columns.Add("Id", typeof(int));
    table.Columns.Add("MaterialCode", typeof(string));
    table.Columns.Add("Description", typeof(string));
    table.Columns.Add("IsActive", typeof(bool));
    table.Rows.Add(42, "MAT-000042", "Aceite hidráulico", isActive);
    return table;
  }
}
