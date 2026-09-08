using System.Data;
using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Application.Features.Logistica.Stock;
using OrionERP.Infrastructure.Features.Logistica.Materials;
using OrionERP.Infrastructure.Features.Logistica.Stock;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Logistica;

public sealed class StockMaterialLocationScopeTests
{
  [Theory]
  [InlineData("stock")]
  [InlineData("movements")]
  [InlineData("attachments")]
  [InlineData("download")]
  public async Task StockReadsStopWhenSessionScopeIsRejected(string operation)
  {
    var connection = new FakeQueryDbConnection
    {
      NonQueryResultFactory = (sql, _) => sql.Contains("THROW 51930", StringComparison.Ordinal)
        ? throw new UnauthorizedAccessException("Missing authenticated RFC") : 0,
      ReaderResultFactory = (_, _) => throw new InvalidOperationException("Business data must not be read.")
    };
    var service = new StockService(new FakeQueryConnectionFactory(connection));
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => operation switch
    {
      "stock" => service.GetStockAsync(new StockFilter()),
      "movements" => service.GetStockTransactionsAsync(31),
      "attachments" => service.GetLocationMaterialAttachmentsAsync(5, 9),
      _ => (Task)service.GetLocationMaterialAttachmentContentAsync(41)
    });
    Assert.Single(connection.ExecutedCommands);
  }

  [Theory]
  [InlineData("inventory")]
  [InlineData("movements")]
  [InlineData("image")]
  public async Task MaterialReadsStopWhenRequestedCompanyDiffersFromSession(string operation)
  {
    var connection = new FakeQueryDbConnection
    {
      NonQueryResultFactory = (sql, _) => sql.Contains("THROW 51861", StringComparison.Ordinal)
        ? throw new UnauthorizedAccessException("Foreign requested company") : 0,
      ReaderResultFactory = (_, _) => throw new InvalidOperationException("Business data must not be read.")
    };
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => operation switch
    {
      "inventory" => service.GetMaterialInventoryAsync("FOREIGN010101AA", 9),
      "movements" => service.GetMaterialMovementsAsync(new MaterialMovementFilter { Rfc = "FOREIGN010101AA", MaterialId = 9 }),
      _ => (Task)service.GetMaterialImageAsync("FOREIGN010101AA", 9)
    });
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("AS Bytes", StringComparison.Ordinal));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task LifecycleDoesNotExposeHiddenDependenciesOrDeactivateSharedMaster(bool deactivate)
  {
    var connection = new FakeQueryDbConnection
    {
      NonQueryResultFactory = (sql, _) => sql.Contains("THROW 51862", StringComparison.Ordinal)
        ? throw new UnauthorizedAccessException("Dependency belongs to an inaccessible location") : 0,
      ReaderResultFactory = (_, _) => throw new InvalidOperationException("Dependency details must not be read.")
    };
    var service = new MaterialService(new FakeQueryConnectionFactory(connection));
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => deactivate
      ? service.DeactivateMaterialAsync(new MaterialDeactivateRequest { Rfc = "TEST010101AAA", MaterialId = 9 })
      : (Task)service.GetMaterialLifecycleAssessmentAsync("TEST010101AAA", 9));

    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("WITH DependencyRows", StringComparison.Ordinal));
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("UPDATE logistica.Material", StringComparison.Ordinal));
    Assert.NotNull(connection.LastTransaction);
    Assert.Equal(IsolationLevel.Serializable, connection.LastTransaction!.IsolationLevel);
  }

  [Fact]
  public async Task ForeignStockBalanceCannotWriteThresholdsOrAudit()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (sql, _) =>
      {
        Assert.Contains("#OrionVisibleLocations", sql, StringComparison.Ordinal);
        Assert.Contains("sb.Rfc = CONVERT(varchar(50), SESSION_CONTEXT(N'OrionRfc'))", sql, StringComparison.Ordinal);
        return new DataTable();
      }
    };
    var result = await new StockService(new FakeQueryConnectionFactory(connection)).SaveStockThresholdsAsync(
      new StockThresholdUpdateRequest { StockBalanceId = 401, MinQuantity = 1, MaxQuantity = 2 });
    Assert.False(result.Success);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("UPDATE logistica.StockBalance", StringComparison.Ordinal));
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO logistica.StockTransaction", StringComparison.Ordinal));
  }
}
