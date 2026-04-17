using OrionERP.Application.Features.Logistica.Stock;
using OrionERP.Infrastructure.Features.Logistica.Stock;
using OrionERP.UnitTests.Common;
using System.Data;

namespace OrionERP.UnitTests.Logistica;

public class StockServiceThresholdTests
{
  [Fact]
  public async Task SaveStockThresholdsAsync_UpdatesThresholds_WhenRequestIsValid()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (_, _) => CreateStockBalanceStateTable(id: 41, locationId: 5, materialId: 9, quantity: 0m, isRemoved: false),
      NonQueryResultFactory = (_, _) => 1
    };
    var service = new StockService(new FakeQueryConnectionFactory(connection));

    var result = await service.SaveStockThresholdsAsync(new StockThresholdUpdateRequest
    {
      StockBalanceId = 41,
      MinQuantity = 1.5m,
      MaxQuantity = 8m
    });

    Assert.True(result.Success);
    Assert.Equal(41, result.EntityId);
    Assert.NotNull(connection.LastCommandText);
    Assert.Contains("UPDATE logistica.StockBalance", connection.LastCommandText!, StringComparison.Ordinal);
    Assert.Contains("MinQuantity = @MinQuantity", connection.LastCommandText!, StringComparison.Ordinal);
    Assert.Contains("MaxQuantity = @MaxQuantity", connection.LastCommandText!, StringComparison.Ordinal);
    AssertParameter(connection.LastParameters, "@StockBalanceId", 41);
    AssertParameter(connection.LastParameters, "@MinQuantity", 1.5m);
    AssertParameter(connection.LastParameters, "@MaxQuantity", 8m);
  }

  [Fact]
  public async Task SaveStockThresholdsAsync_FailsValidation_WhenMinimumExceedsMaximum()
  {
    var connection = new FakeQueryDbConnection();
    var service = new StockService(new FakeQueryConnectionFactory(connection));

    var result = await service.SaveStockThresholdsAsync(new StockThresholdUpdateRequest
    {
      StockBalanceId = 41,
      MinQuantity = 10m,
      MaxQuantity = 8m
    });

    Assert.False(result.Success);
    Assert.Equal("El mínimo no puede ser mayor que el máximo.", result.Message);
    Assert.Null(connection.LastCommandText);
  }

  [Fact]
  public async Task SaveStockThresholdsAsync_Fails_WhenStockBalanceDoesNotExist()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (_, _) => CreateEmptyStockBalanceStateTable(),
      NonQueryResultFactory = (_, _) => 0
    };
    var service = new StockService(new FakeQueryConnectionFactory(connection));

    var result = await service.SaveStockThresholdsAsync(new StockThresholdUpdateRequest
    {
      StockBalanceId = 99,
      MinQuantity = null,
      MaxQuantity = 5m
    });

    Assert.False(result.Success);
    Assert.Equal("El registro de inventario ya no existe.", result.Message);
    Assert.NotNull(connection.LastCommandText);
  }

  [Fact]
  public async Task SaveStockThresholdsAsync_Fails_WhenStockBalanceIsRemoved()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (_, _) => CreateStockBalanceStateTable(id: 41, locationId: 5, materialId: 9, quantity: 0m, isRemoved: true)
    };
    var service = new StockService(new FakeQueryConnectionFactory(connection));

    var result = await service.SaveStockThresholdsAsync(new StockThresholdUpdateRequest
    {
      StockBalanceId = 41,
      MinQuantity = 1m,
      MaxQuantity = 3m
    });

    Assert.False(result.Success);
    Assert.Equal("Reactiva el material antes de modificar sus parámetros.", result.Message);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("UPDATE logistica.StockBalance", StringComparison.Ordinal));
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
}
