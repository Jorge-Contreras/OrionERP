using System.Data;
using OrionERP.Application.Features.Logistica.Stock;
using OrionERP.Infrastructure.Features.Logistica.Stock;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Logistica;

public class StockServiceRemovalTests
{
  [Fact]
  public async Task AddMaterialToLocationAsync_InsertsZeroBalance_AndWritesAudit()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("FROM logistica.Location l", StringComparison.Ordinal))
        {
          return CreateLocationStateTable(isInventoryEnabled: true, isActive: true);
        }

        if (commandText.Contains("FROM logistica.Material m", StringComparison.Ordinal))
        {
          return CreateMaterialStateTable(status: "ACTIVO", isActive: true);
        }

        return CreateEmptyStockBalanceStateTable();
      },
      NonQueryResultFactory = (_, _) => 1,
      ScalarResultFactory = (_, _) => 73
    };
    var service = new StockService(new FakeQueryConnectionFactory(connection));

    var result = await service.AddMaterialToLocationAsync(new LocationMaterialAddRequest
    {
      LocationId = 5,
      MaterialId = 9,
      AddedBy = "Ana"
    });

    Assert.True(result.Success);
    Assert.Equal(73, result.EntityId);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasCommitted);

    var stockInsert = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO logistica.StockBalance", StringComparison.Ordinal));
    AssertParameter(stockInsert.Parameters, "@LocationId", 5);
    AssertParameter(stockInsert.Parameters, "@MaterialId", 9);

    var auditInsert = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO logistica.StockTransaction", StringComparison.Ordinal));
    AssertParameter(auditInsert.Parameters, "@TransactionType", "Added");
    AssertParameter(auditInsert.Parameters, "@QuantityAfter", 0m);
    AssertParameter(auditInsert.Parameters, "@PerformedBy", "Ana");
  }

  [Fact]
  public async Task AddMaterialToLocationAsync_Fails_WhenMaterialIsAlreadyActiveInLocation()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("FROM logistica.Location l", StringComparison.Ordinal))
        {
          return CreateLocationStateTable(isInventoryEnabled: true, isActive: true);
        }

        if (commandText.Contains("FROM logistica.Material m", StringComparison.Ordinal))
        {
          return CreateMaterialStateTable(status: "ACTIVO", isActive: true);
        }

        if (commandText.Contains("WHERE sb.LocationId = @LocationId", StringComparison.Ordinal))
        {
          return CreateStockBalanceStateTable(id: 41, locationId: 5, materialId: 9, quantity: 0m, isRemoved: false);
        }

        return CreateEmptyStockBalanceStateTable();
      }
    };
    var service = new StockService(new FakeQueryConnectionFactory(connection));

    var result = await service.AddMaterialToLocationAsync(new LocationMaterialAddRequest
    {
      LocationId = 5,
      MaterialId = 9,
      AddedBy = "Ana"
    });

    Assert.False(result.Success);
    Assert.Equal("El material ya está activo en la ubicación seleccionada.", result.Message);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasRolledBack);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO logistica.StockBalance", StringComparison.Ordinal));
  }

  [Fact]
  public async Task AddMaterialToLocationAsync_ReactivatesRemovedStockAndAttachments_AndWritesAudit()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("FROM logistica.Location l", StringComparison.Ordinal))
        {
          return CreateLocationStateTable(isInventoryEnabled: true, isActive: true);
        }

        if (commandText.Contains("FROM logistica.Material m", StringComparison.Ordinal))
        {
          return CreateMaterialStateTable(status: "ACTIVO", isActive: true);
        }

        if (commandText.Contains("WHERE sb.LocationId = @LocationId", StringComparison.Ordinal))
        {
          return CreateStockBalanceStateTable(id: 41, locationId: 5, materialId: 9, quantity: 0m, isRemoved: true);
        }

        return CreateEmptyStockBalanceStateTable();
      },
      NonQueryResultFactory = (_, _) => 1
    };
    var service = new StockService(new FakeQueryConnectionFactory(connection));

    var result = await service.AddMaterialToLocationAsync(new LocationMaterialAddRequest
    {
      LocationId = 5,
      MaterialId = 9,
      AddedBy = "Ana"
    });

    Assert.True(result.Success);
    Assert.Equal(41, result.EntityId);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasCommitted);

    var stockUpdate = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("SET IsRemoved = 0", StringComparison.Ordinal));
    AssertParameter(stockUpdate.Parameters, "@StockBalanceId", 41);

    var attachmentUpdate = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("SET IsDeleted = 0", StringComparison.Ordinal));
    AssertParameter(attachmentUpdate.Parameters, "@LocationId", 5);
    AssertParameter(attachmentUpdate.Parameters, "@MaterialId", 9);

    var auditInsert = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO logistica.StockTransaction", StringComparison.Ordinal));
    AssertParameter(auditInsert.Parameters, "@TransactionType", "Reactivated");
    AssertParameter(auditInsert.Parameters, "@PerformedBy", "Ana");
  }

  [Fact]
  public async Task RemoveLocationMaterialAsync_Fails_WhenQuantityIsNotZero()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (commandText, _) => commandText.Contains("WHERE sb.Id = @StockBalanceId", StringComparison.Ordinal)
        ? CreateStockBalanceStateTable(id: 41, locationId: 5, materialId: 9, quantity: 2m, isRemoved: false)
        : CreateEmptyStockBalanceStateTable()
    };
    var service = new StockService(new FakeQueryConnectionFactory(connection));

    var result = await service.RemoveLocationMaterialAsync(41, "Ana");

    Assert.False(result.Success);
    Assert.Equal("Solo puedes quitar materiales con cantidad 0. Ajusta el inventario antes de eliminarlo.", result.Message);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasRolledBack);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("UPDATE logistica.StockBalance", StringComparison.Ordinal));
  }

  [Fact]
  public async Task RemoveLocationMaterialAsync_SoftDeletesStockAndAttachments_AndWritesAudit()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (commandText, _) => commandText.Contains("WHERE sb.Id = @StockBalanceId", StringComparison.Ordinal)
        ? CreateStockBalanceStateTable(id: 41, locationId: 5, materialId: 9, quantity: 0m, isRemoved: false)
        : CreateEmptyStockBalanceStateTable(),
      NonQueryResultFactory = (_, _) => 1
    };
    var service = new StockService(new FakeQueryConnectionFactory(connection));

    var result = await service.RemoveLocationMaterialAsync(41, "Ana");

    Assert.True(result.Success);
    Assert.Equal(41, result.EntityId);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasCommitted);

    var stockUpdate = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("SET IsRemoved = 1", StringComparison.Ordinal));
    AssertParameter(stockUpdate.Parameters, "@StockBalanceId", 41);
    AssertParameter(stockUpdate.Parameters, "@RemovedBy", "Ana");

    var attachmentUpdate = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("SET IsDeleted = 1", StringComparison.Ordinal));
    AssertParameter(attachmentUpdate.Parameters, "@LocationId", 5);
    AssertParameter(attachmentUpdate.Parameters, "@MaterialId", 9);
    AssertParameter(attachmentUpdate.Parameters, "@DeletedBy", "Ana");

    var auditInsert = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO logistica.StockTransaction", StringComparison.Ordinal));
    AssertParameter(auditInsert.Parameters, "@TransactionType", "Removed");
    AssertParameter(auditInsert.Parameters, "@QuantityAfter", 0m);
    AssertParameter(auditInsert.Parameters, "@PerformedBy", "Ana");
  }

  [Fact]
  public async Task ReactivateLocationMaterialAsync_RestoresStockAndAttachments_AndWritesAudit()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (commandText, _) => commandText.Contains("WHERE sb.Id = @StockBalanceId", StringComparison.Ordinal)
        ? CreateStockBalanceStateTable(id: 41, locationId: 5, materialId: 9, quantity: 0m, isRemoved: true)
        : CreateEmptyStockBalanceStateTable(),
      NonQueryResultFactory = (_, _) => 1
    };
    var service = new StockService(new FakeQueryConnectionFactory(connection));

    var result = await service.ReactivateLocationMaterialAsync(41, "Ana");

    Assert.True(result.Success);
    Assert.Equal(41, result.EntityId);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasCommitted);

    var stockUpdate = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("SET IsRemoved = 0", StringComparison.Ordinal));
    AssertParameter(stockUpdate.Parameters, "@StockBalanceId", 41);

    var attachmentUpdate = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("SET IsDeleted = 0", StringComparison.Ordinal));
    AssertParameter(attachmentUpdate.Parameters, "@LocationId", 5);
    AssertParameter(attachmentUpdate.Parameters, "@MaterialId", 9);

    var auditInsert = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO logistica.StockTransaction", StringComparison.Ordinal));
    AssertParameter(auditInsert.Parameters, "@TransactionType", "Reactivated");
    AssertParameter(auditInsert.Parameters, "@QuantityAfter", 0m);
    AssertParameter(auditInsert.Parameters, "@PerformedBy", "Ana");
  }

  [Fact]
  public async Task SaveLocationMaterialAttachmentAsync_Fails_WhenStockBalanceIsRemoved()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (commandText, _) => commandText.Contains("WHERE sb.LocationId = @LocationId", StringComparison.Ordinal)
        ? CreateStockBalanceStateTable(id: 41, locationId: 5, materialId: 9, quantity: 0m, isRemoved: true)
        : CreateEmptyStockBalanceStateTable()
    };
    var service = new StockService(new FakeQueryConnectionFactory(connection));

    var result = await service.SaveLocationMaterialAttachmentAsync(new LocationMaterialAttachmentCreateRequest
    {
      LocationId = 5,
      MaterialId = 9,
      FileName = "evidencia.pdf",
      FileExtension = "pdf",
      Bytes = [1, 2, 3],
      ContentType = "application/pdf"
    });

    Assert.False(result.Success);
    Assert.Equal("Reactiva el material antes de guardar adjuntos.", result.Message);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO logistica.LocationMaterialAttachment", StringComparison.Ordinal));
  }

  private static void AssertParameter(IReadOnlyList<FakeQueryParameter> parameters, string name, object expectedValue)
  {
    var parameter = Assert.Single(parameters, parameter => HasParameterName(parameter, name));
    Assert.Equal(expectedValue, parameter.Value);
  }

  private static bool HasParameterName(FakeQueryParameter parameter, string expectedName)
    => string.Equals(parameter.Name.TrimStart('@'), expectedName.TrimStart('@'), StringComparison.OrdinalIgnoreCase);

  private static DataTable CreateStockBalanceStateTable(int id, int locationId, int materialId, decimal quantity, bool isRemoved)
  {
    var table = CreateEmptyStockBalanceStateTable();
    table.Rows.Add(id, locationId, materialId, quantity, isRemoved);
    return table;
  }

  private static DataTable CreateEmptyStockBalanceStateTable()
  {
    var table = new DataTable();
    table.Columns.Add("Id", typeof(int));
    table.Columns.Add("LocationId", typeof(int));
    table.Columns.Add("MaterialId", typeof(int));
    table.Columns.Add("Quantity", typeof(decimal));
    table.Columns.Add("IsRemoved", typeof(bool));
    return table;
  }

  private static DataTable CreateLocationStateTable(bool isInventoryEnabled, bool isActive)
  {
    var table = new DataTable();
    table.Columns.Add("Id", typeof(int));
    table.Columns.Add("IsInventoryEnabled", typeof(bool));
    table.Columns.Add("IsActive", typeof(bool));
    table.Rows.Add(5, isInventoryEnabled, isActive);
    return table;
  }

  private static DataTable CreateMaterialStateTable(string status, bool isActive)
  {
    var table = new DataTable();
    table.Columns.Add("Id", typeof(int));
    table.Columns.Add("MaterialStatus", typeof(string));
    table.Columns.Add("IsActive", typeof(bool));
    table.Rows.Add(9, status, isActive);
    return table;
  }
}
