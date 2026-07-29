namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantOrdersPageTests
{
  [Fact]
  public void OrdersQuery_KeepsOrdersInFolioSequence()
  {
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOrderService.cs");
    var operationalOrdersStart = service.IndexOf(
      "public async Task<IReadOnlyList<RestaurantOrderDto>> GetOperationalOrdersAsync",
      StringComparison.Ordinal);
    var updateStatusStart = service.IndexOf(
      "public async Task<RestaurantCommandResult> UpdateOrderStatusAsync",
      operationalOrdersStart,
      StringComparison.Ordinal);
    var operationalOrdersQuery = service[operationalOrdersStart..updateStatusStart];

    Assert.Contains(
      "ORDER BY orderInfo.OperationalDate, orderInfo.Folio, orderInfo.CreatedAt, orderInfo.Id;",
      operationalOrdersQuery,
      StringComparison.Ordinal);
    Assert.DoesNotContain("ORDER BY CASE", operationalOrdersQuery, StringComparison.Ordinal);
  }

  [Fact]
  public void OrdersPage_UsesStableFolioOrderingAndKitchenProgressColors()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantOrdersPage.razor");
    var styles = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantOrdersPage.razor.css");

    Assert.Contains(".ThenBy(order => order.Folio)", page, StringComparison.Ordinal);
    Assert.Contains("RestaurantKitchenProgress.Ready => \"order-card--complete\"", page, StringComparison.Ordinal);
    Assert.Contains("RestaurantKitchenProgress.Preparing => \"order-card--partial\"", page, StringComparison.Ordinal);
    Assert.Contains("_ => \"order-card--not-started\"", page, StringComparison.Ordinal);
    Assert.Contains(".order-card.order-card--complete", styles, StringComparison.Ordinal);
    Assert.Contains(".order-card.order-card--partial", styles, StringComparison.Ordinal);
    Assert.Contains(".order-card.order-card--not-started", styles, StringComparison.Ordinal);
  }

  [Fact]
  public void OrdersPage_DisplaysOrderNotesOnEachCard()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantOrdersPage.razor");
    var styles = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantOrdersPage.razor.css");

    Assert.Contains("!string.IsNullOrWhiteSpace(order.Notes)", page, StringComparison.Ordinal);
    Assert.Contains("Notas de la orden", page, StringComparison.Ordinal);
    Assert.Contains("@order.Notes", page, StringComparison.Ordinal);
    Assert.Contains(".order-card__note", styles, StringComparison.Ordinal);
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath)));
}
