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

  [Fact]
  public void OrdersPage_ExposesOrderEventTimelineForEveryCard()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantOrdersPage.razor");
    var styles = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantOrdersPage.razor.css");
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOrderService.cs");
    var migration = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/Sql/20260728_restaurant_order_event_log.sql");

    Assert.Contains("OpenOrderLogAsync(order)", page, StringComparison.Ordinal);
    Assert.Contains("Bitácora de la orden", page, StringComparison.Ordinal);
    Assert.Contains("GetOrderEventsAsync(CurrentRfc, logOrder.Id)", page, StringComparison.Ordinal);
    Assert.Contains("new[]{\"Active\",\"Ready\",\"Dispatched\",\"Delivered\",\"Completed\",\"Cancelled\"}", page, StringComparison.Ordinal);
    Assert.Contains(".order-log__timeline", styles, StringComparison.Ordinal);
    Assert.Contains(".order-log__event--kitchen", styles, StringComparison.Ordinal);
    Assert.Contains(".order-log__event--payment", styles, StringComparison.Ordinal);
    Assert.Contains("public async Task<IReadOnlyList<RestaurantOrderEventDto>> GetOrderEventsAsync", service, StringComparison.Ordinal);
    Assert.Contains("CREATE TABLE restaurante.OrderEvent", migration, StringComparison.Ordinal);
    Assert.Contains("UX_RestaurantOrderEvent_Source", migration, StringComparison.Ordinal);
    Assert.Contains("Give existing orders a useful audit trail", migration, StringComparison.Ordinal);
  }

  [Fact]
  public void OrdersPage_ReprintsCustomerAndSectionTicketsFromPersistedSnapshots()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantOrdersPage.razor");
    var styles = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantOrdersPage.razor.css");
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOrderService.cs");
    var pdfService = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantReceiptPdfService.cs");

    Assert.Contains("OpenReprintAsync(order)", page, StringComparison.Ordinal);
    Assert.Contains("Reimprimir tickets", page, StringComparison.Ordinal);
    Assert.DoesNotContain("ticket de cliente@if", page, StringComparison.Ordinal);
    Assert.Contains("ReceiptPdfService.Generate(reprintReceipt)", page, StringComparison.Ordinal);
    Assert.Contains("GetReceiptAsync(CurrentRfc, order.Id)", page, StringComparison.Ordinal);
    Assert.Contains("public async Task<RestaurantReceiptDto?> GetReceiptAsync", service, StringComparison.Ordinal);
    Assert.Contains("orderInfo.TaxRateSnapshot AS TaxRate", service, StringComparison.Ordinal);
    Assert.Contains("FROM restaurante.OrderPromotion", service, StringComparison.Ordinal);
    Assert.Contains("REIMPRESIÓN · TICKET DE CLIENTE", pdfService, StringComparison.Ordinal);
    Assert.Contains(".order-reprint__card", styles, StringComparison.Ordinal);
  }

  [Fact]
  public void OrderService_AuditsLifecycleAndRelatedFinancialEvents()
  {
    var orderService = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOrderService.cs");
    var backofficeService = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantBackofficeService.cs");
    var accountingService = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantAccountingService.cs");

    foreach (var eventType in new[]
             {
               "OrderCreated", "InventoryReserved", "InventoryConsumed", "InventoryReleased",
               "PaymentReceived", "OrderPaid", "SentToKitchen",
               "LinePreparing", "LineReady", "LineReopened", "PrioritySet",
               "DeliveryRequested", "OrderDispatched", "OrderDelivered", "OrderCompleted", "OrderCancelled",
               "PaymentRefunded", "AdditionalPaymentAuthorized", "RefundAuthorized"
             })
    {
      Assert.Contains($"\"{eventType}\"", orderService, StringComparison.Ordinal);
    }

    Assert.Contains("\"OrderSettled\"", backofficeService, StringComparison.Ordinal);
    Assert.Contains("'AccountingLinked'", accountingService, StringComparison.Ordinal);
    Assert.Contains("'CfdiLinked'", accountingService, StringComparison.Ordinal);
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath)));
}
