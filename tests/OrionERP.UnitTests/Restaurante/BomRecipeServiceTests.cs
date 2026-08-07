using System.Data;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Restaurante;
using OrionERP.Infrastructure.Features.Restaurante;
using OrionERP.UnitTests.Common;

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

  [Fact]
  public async Task DeleteDraftAsync_DeletesOnlyAfterLockedDraftAndProductionChecks()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (_, _) => CreateVersionTable("Draft"),
      ScalarResultFactory = (_, _) => false
    };
    var service = new BomRecipeService(new FakeQueryConnectionFactory(connection));

    var result = await service.DeleteDraftAsync("OHM191112Q26", 88);

    Assert.True(result.Success);
    Assert.Equal(IsolationLevel.Serializable, connection.LastTransaction!.IsolationLevel);
    Assert.True(connection.LastTransaction.WasCommitted);
    Assert.Contains("UPDLOCK, HOLDLOCK", connection.ExecutedCommands[0].CommandText, StringComparison.Ordinal);
    Assert.Contains("ProductionOrder", connection.ExecutedCommands[1].CommandText, StringComparison.Ordinal);
    Assert.Contains("DELETE FROM logistica.Recipe", connection.ExecutedCommands[2].CommandText, StringComparison.Ordinal);
    Assert.Contains("[Status] = 'Draft'", connection.ExecutedCommands[2].CommandText, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("Active", false)]
  [InlineData("Draft", true)]
  public async Task DeleteDraftAsync_RollsBackForNonDraftOrAnyProductionReference(string status, bool hasProduction)
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (_, _) => CreateVersionTable(status),
      ScalarResultFactory = (_, _) => hasProduction
    };
    var result = await new BomRecipeService(new FakeQueryConnectionFactory(connection))
      .DeleteDraftAsync("OHM191112Q26", 88);

    Assert.False(result.Success);
    Assert.True(connection.LastTransaction!.WasRolledBack);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("DELETE FROM logistica.BomVersion", StringComparison.Ordinal));
  }

  [Fact]
  public async Task RetireAsync_BlocksPlannedProduction_AndRetiresWhenClear()
  {
    var blockedConnection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (_, _) => CreateVersionTable("Active"),
      ScalarResultFactory = (_, _) => 1
    };
    var blocked = await new BomRecipeService(new FakeQueryConnectionFactory(blockedConnection))
      .RetireAsync("OHM191112Q26", 88, "admin@orionerp.local");

    Assert.False(blocked.Success);
    Assert.Contains("producción pendiente", blocked.Message, StringComparison.OrdinalIgnoreCase);
    Assert.True(blockedConnection.LastTransaction!.WasRolledBack);

    var clearConnection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (_, _) => CreateVersionTable("Active"),
      ScalarResultFactory = (_, _) => 0,
      NonQueryResultFactory = (_, _) => 1
    };
    var retired = await new BomRecipeService(new FakeQueryConnectionFactory(clearConnection))
      .RetireAsync("OHM191112Q26", 88, "admin@orionerp.local");

    Assert.True(retired.Success);
    Assert.True(clearConnection.LastTransaction!.WasCommitted);
    Assert.Contains("[Status] = 'Retired'", clearConnection.ExecutedCommands[2].CommandText, StringComparison.Ordinal);
    Assert.Contains("[Status] IN ('Draft', 'Active')", clearConnection.ExecutedCommands[2].CommandText, StringComparison.Ordinal);
    Assert.Contains("admin@orionerp.local", retired.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task DeleteConversionAsync_BlocksCurrentBomUsage_AndDeletesWhenClear()
  {
    var blockedConnection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (_, _) => CreateConversionTable(),
      ScalarResultFactory = (_, _) => 1
    };
    var blocked = await new BomRecipeService(new FakeQueryConnectionFactory(blockedConnection))
      .DeleteMaterialUnitConversionAsync("OHM191112Q26", 7);

    Assert.False(blocked.Success);
    Assert.True(blockedConnection.LastTransaction!.WasRolledBack);
    Assert.Contains("[Status] IN ('Draft', 'Active')", blockedConnection.ExecutedCommands[1].CommandText, StringComparison.Ordinal);
    Assert.DoesNotContain(blockedConnection.ExecutedCommands, command => command.CommandText.StartsWith("DELETE FROM logistica.MaterialUnitConversion", StringComparison.Ordinal));

    var clearConnection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (_, _) => CreateConversionTable(),
      ScalarResultFactory = (_, _) => 0,
      NonQueryResultFactory = (_, _) => 1
    };
    var deleted = await new BomRecipeService(new FakeQueryConnectionFactory(clearConnection))
      .DeleteMaterialUnitConversionAsync("OHM191112Q26", 7);

    Assert.True(deleted.Success);
    Assert.True(clearConnection.LastTransaction!.WasCommitted);
    Assert.Equal(IsolationLevel.Serializable, clearConnection.LastTransaction.IsolationLevel);
    Assert.Contains("UPDLOCK, HOLDLOCK", clearConnection.ExecutedCommands[0].CommandText, StringComparison.Ordinal);
    Assert.StartsWith("DELETE FROM logistica.MaterialUnitConversion", clearConnection.ExecutedCommands[2].CommandText, StringComparison.Ordinal);
  }

  private static DataTable CreateVersionTable(string status)
  {
    var table = new DataTable();
    table.Columns.Add("Id", typeof(long));
    table.Columns.Add("BomHeaderId", typeof(long));
    table.Columns.Add("Status", typeof(string));
    table.Columns.Add("ProductMaterialId", typeof(int));
    table.Rows.Add(88L, 12L, status, 42);
    return table;
  }

  private static DataTable CreateConversionTable()
  {
    var table = new DataTable();
    table.Columns.Add("Id", typeof(int));
    table.Columns.Add("MaterialId", typeof(int));
    table.Columns.Add("FromUnitId", typeof(int));
    table.Columns.Add("ToUnitId", typeof(int));
    table.Rows.Add(7, 42, 2, 1);
    return table;
  }

  private sealed class UnavailableConnectionFactory : IDbConnectionFactory
  {
    public IDbConnection Create() => throw new InvalidOperationException("Validation should run before opening a database connection.");
  }
}
