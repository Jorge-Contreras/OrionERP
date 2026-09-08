using System.Data;
using OrionERP.Infrastructure.Features.Restaurante;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantOrderLocationScopeTests
{
  [Theory]
  [InlineData("detail")]
  [InlineData("receipt")]
  [InlineData("payments")]
  public async Task ForeignRequestedCompanyStopsBeforeReadingBusinessData(string operation)
  {
    var connection = new FakeQueryDbConnection
    {
      NonQueryResultFactory = (sql, _) => sql.Contains("THROW 51935", StringComparison.Ordinal)
        ? throw new UnauthorizedAccessException("Foreign company") : 0,
      ReaderResultFactory = (_, _) => throw new InvalidOperationException("Business data must not be read.")
    };
    var service = new RestaurantOrderService(new FakeQueryConnectionFactory(connection));
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => operation switch
    {
      "detail" => service.GetOrderAsync("TEST010101AAA", Guid.NewGuid()),
      "receipt" => service.GetReceiptAsync("TEST010101AAA", Guid.NewGuid()),
      _ => (Task)service.GetPaymentsAsync("TEST010101AAA", Guid.NewGuid())
    });
  }

  [Theory]
  [InlineData("cancel")]
  [InlineData("priority")]
  [InlineData("line")]
  [InlineData("revert")]
  public async Task HiddenInventoryStopsWholeOrderMutationInsideTransaction(string operation)
  {
    var connection = new FakeQueryDbConnection
    {
      NonQueryResultFactory = (sql, _) => sql.Contains("THROW 51936", StringComparison.Ordinal)
        ? throw new UnauthorizedAccessException("Hidden inventory") : 0,
      ReaderResultFactory = (_, _) => throw new InvalidOperationException("Business data must not be read.")
    };
    var service = new RestaurantOrderService(new FakeQueryConnectionFactory(connection));
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => operation switch
    {
      "cancel" => service.CancelOrderAsync("TEST010101AAA", Guid.NewGuid(), "reason", "supervisor"),
      "priority" => service.SetOrderPriorityAsync("TEST010101AAA", Guid.NewGuid(), 1, "reason", "supervisor"),
      "line" => service.UpdateLineStatusAsync("TEST010101AAA", 7, "Preparing", "test"),
      _ => service.RevertLineStatusAsync("TEST010101AAA", 7, "test")
    });
    Assert.NotNull(connection.LastTransaction);
    Assert.Equal(IsolationLevel.Serializable, connection.LastTransaction!.IsolationLevel);
    Assert.DoesNotContain(connection.ExecutedCommands, c => c.CommandText.Contains("UPDATE logistica.StockBalance", StringComparison.Ordinal));
    Assert.DoesNotContain(connection.ExecutedCommands, c => c.CommandText.Contains("UPDATE restaurante.OrderLine", StringComparison.Ordinal));
  }
}
