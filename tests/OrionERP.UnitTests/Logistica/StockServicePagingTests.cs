using OrionERP.Application.Features.Logistica.Stock;
using OrionERP.Infrastructure.Features.Logistica.Stock;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Logistica;

public class StockServicePagingTests
{
  [Fact]
  public async Task GetStockAsync_AddsPagingAndSelectedFilters_WhenTakeIsProvided()
  {
    var connection = new FakeQueryDbConnection();
    var service = new StockService(new FakeQueryConnectionFactory(connection));

    await service.GetStockAsync(new StockFilter
    {
      SearchText = "MAT-001",
      RoomId = 2,
      LocationId = 9,
      LowStockOnly = true,
      CountDueOnly = true,
      IncludeZeroBalances = false,
      Skip = 100,
      Take = 51
    });

    Assert.NotNull(connection.LastCommandText);
    Assert.Contains("sb.Quantity <> 0", connection.LastCommandText!, StringComparison.Ordinal);
    Assert.Contains("l.RoomId = @RoomId", connection.LastCommandText!, StringComparison.Ordinal);
    Assert.Contains("(l.Id = @LocationId OR l.ParentLocationId = @LocationId)", connection.LastCommandText!, StringComparison.Ordinal);
    Assert.Contains("m.MaterialCode LIKE @Search", connection.LastCommandText!, StringComparison.Ordinal);
    Assert.Contains("sb.MinQuantity IS NOT NULL AND sb.Quantity <= sb.MinQuantity", connection.LastCommandText!, StringComparison.Ordinal);
    Assert.Contains("sb.CountFrequencyDays IS NOT NULL", connection.LastCommandText!, StringComparison.Ordinal);
    Assert.Contains("OFFSET @Skip ROWS", connection.LastCommandText!, StringComparison.Ordinal);
    Assert.Contains("FETCH NEXT @Take ROWS ONLY;", connection.LastCommandText!, StringComparison.Ordinal);

    AssertParameter(connection.LastParameters, "@Search", "%MAT-001%");
    AssertParameter(connection.LastParameters, "@RoomId", 2);
    AssertParameter(connection.LastParameters, "@LocationId", 9);
    AssertParameter(connection.LastParameters, "@Skip", 100);
    AssertParameter(connection.LastParameters, "@Take", 51);
  }

  [Fact]
  public async Task GetStockAsync_OmitsPagingClause_WhenTakeIsZero()
  {
    var connection = new FakeQueryDbConnection();
    var service = new StockService(new FakeQueryConnectionFactory(connection));

    await service.GetStockAsync(new StockFilter
    {
      IncludeZeroBalances = true,
      Skip = 10,
      Take = 0
    });

    Assert.NotNull(connection.LastCommandText);
    Assert.DoesNotContain("OFFSET @Skip ROWS", connection.LastCommandText!, StringComparison.Ordinal);
    Assert.DoesNotContain("FETCH NEXT @Take ROWS ONLY;", connection.LastCommandText!, StringComparison.Ordinal);
    Assert.DoesNotContain(connection.LastParameters, parameter => HasParameterName(parameter, "@Skip"));
    Assert.DoesNotContain(connection.LastParameters, parameter => HasParameterName(parameter, "@Take"));
  }

  [Fact]
  public async Task GetStockAsync_ExcludesRemovedRows_UnlessRequested()
  {
    var connection = new FakeQueryDbConnection();
    var service = new StockService(new FakeQueryConnectionFactory(connection));

    await service.GetStockAsync(new StockFilter
    {
      IncludeRemoved = false
    });

    Assert.NotNull(connection.LastCommandText);
    Assert.Contains("ISNULL(sb.IsRemoved, 0) = 0", connection.LastCommandText!, StringComparison.Ordinal);

    await service.GetStockAsync(new StockFilter
    {
      IncludeRemoved = true
    });

    Assert.NotNull(connection.LastCommandText);
    Assert.DoesNotContain("AND ISNULL(sb.IsRemoved, 0) = 0", connection.LastCommandText!, StringComparison.Ordinal);
  }

  private static void AssertParameter(IReadOnlyList<FakeQueryParameter> parameters, string name, object expectedValue)
  {
    var parameter = Assert.Single(parameters, parameter => HasParameterName(parameter, name));
    Assert.Equal(expectedValue, parameter.Value);
  }

  private static bool HasParameterName(FakeQueryParameter parameter, string expectedName)
    => string.Equals(parameter.Name.TrimStart('@'), expectedName.TrimStart('@'), StringComparison.OrdinalIgnoreCase);
}
