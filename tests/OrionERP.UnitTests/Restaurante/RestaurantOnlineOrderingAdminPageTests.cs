namespace OrionERP.UnitTests.Restaurante;

using OrionERP.UnitTests.Common;

public sealed class RestaurantOnlineOrderingAdminPageTests
{
  [Fact]
  public void AdminPage_ExposesGuardedOnlineOrderingConfiguration()
  {
    var page = RepoFile.Read("src/OrionERP.Web/Features/Restaurante/RestaurantAdminPage.razor");
    var panel = RepoFile.Read("src/OrionERP.Web/Features/Restaurante/RestaurantOnlineOrdersAdminPanel.razor");

    Assert.Contains("Pedidos en línea", page, StringComparison.Ordinal);
    Assert.Contains("<RestaurantOnlineOrdersAdminPanel />", page, StringComparison.Ordinal);
    Assert.Contains("IOnlineRestaurantOrderingAdminService", panel, StringComparison.Ordinal);
    Assert.Contains("configuration.IsReady", panel, StringComparison.Ordinal);
    Assert.Contains("configuration.ReadinessBlockers", panel, StringComparison.Ordinal);
    Assert.Contains("disabled=\"@(!CanChangeEnabled)\"", panel, StringComparison.Ordinal);
    Assert.Contains("editor.PickupEnabled = true", panel, StringComparison.Ordinal);
    Assert.Contains("OnlineOrderingService.SaveAsync", panel, StringComparison.Ordinal);
  }

  [Fact]
  public void AdminPage_ManagesIndependentHoursProductsAndRecoveryQueue()
  {
    var panel = RepoFile.Read("src/OrionERP.Web/Features/Restaurante/RestaurantOnlineOrdersAdminPanel.razor");

    Assert.Contains("Disponibilidad independiente", panel, StringComparison.Ordinal);
    Assert.Contains("TrySerializeSchedule", panel, StringComparison.Ordinal);
    Assert.Contains("EnabledProductIds", panel, StringComparison.Ordinal);
    Assert.Contains("Publicar un producto en el menú no lo habilita", panel, StringComparison.Ordinal);
    Assert.Contains("configuration.RecoveryItems", panel, StringComparison.Ordinal);
    Assert.Contains("Desactivar o pausar pedidos no detiene esta cola", panel, StringComparison.Ordinal);
    Assert.Contains("summary.RefundGrossAmount", panel, StringComparison.Ordinal);
    Assert.Contains("summary.RefundFeeAmount", panel, StringComparison.Ordinal);
    Assert.Contains("summary.RefundNetAmount", panel, StringComparison.Ordinal);
    Assert.Contains("summary.NetAfterRefunds", panel, StringComparison.Ordinal);
    Assert.Contains("summary.UnreconciledRefundCount", panel, StringComparison.Ordinal);
  }

  [Fact]
  public void OperationalScreens_ShowAndFilterWebClipOrders()
  {
    var orders = RepoFile.Read("src/OrionERP.Web/Features/Restaurante/RestaurantOrdersPage.razor");
    var kitchen = RepoFile.Read("src/OrionERP.Web/Features/Restaurante/RestaurantKitchenPage.razor");

    Assert.Contains("orders-source-filters", orders, StringComparison.Ordinal);
    Assert.Contains("Web · Clip", orders, StringComparison.Ordinal);
    Assert.Contains("QuickPinService.VerifySupervisorPinAsync", orders, StringComparison.Ordinal);
    Assert.Contains("RequestedByUserName = requestedBy", orders, StringComparison.Ordinal);
    Assert.Contains("SupervisorUserName = verification.UserName!", orders, StringComparison.Ordinal);
    Assert.Contains("OnlineOrderingService.RequestRefundAsync", orders, StringComparison.Ordinal);
    Assert.Contains("La solicitud se enviará primero a Clip", orders, StringComparison.Ordinal);
    Assert.Contains("kds-source-filter", kitchen, StringComparison.Ordinal);
    Assert.Contains("Web · Clip", kitchen, StringComparison.Ordinal);
  }

  [Fact]
  public void CanonicalOrderService_PersistsOnlineScopeAndQueuesReadyEmail()
  {
    var models = RepoFile.Read("src/OrionERP.Application/Features/Restaurante/RestaurantModels.cs");
    var service = RepoFile.Read("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOrderService.cs");

    Assert.Contains("public long? PublicSiteId", models, StringComparison.Ordinal);
    Assert.Contains("public Guid? OnlineCheckoutAttemptId", models, StringComparison.Ordinal);
    Assert.Contains("public string? CustomerEmail", models, StringComparison.Ordinal);
    Assert.Contains("@SalesChannel, @DiningTableId", service, StringComparison.Ordinal);
    Assert.Contains("attemptInfo.[State] IN ('Captured','CapturedNeedsOrder')", service, StringComparison.Ordinal);
    Assert.Contains("EnqueueReadyNotificationAsync", service, StringComparison.Ordinal);
    Assert.Contains("restaurante.OnlineOrderNotificationEnqueue", service, StringComparison.Ordinal);
    Assert.Contains("NotificationType = \"Ready\"", service, StringComparison.Ordinal);
  }

  [Fact]
  public void ImportWorker_HeartbeatsAndRetriesIndependentlyFromSalesSwitch()
  {
    var worker = RepoFile.Read("src/OrionERP.Web/Features/Restaurante/RestaurantOnlineOrderImportWorker.cs");
    var registration = RepoFile.Read("src/OrionERP.Web/Configuration/ServiceRegistration.cs");
    var program = RepoFile.Read("src/OrionERP.Web/Program.cs");
    var trainingSettings = RepoFile.Read("src/OrionERP.Web/appsettings.Training.json");

    Assert.Contains("RecordHeartbeatAsync", worker, StringComparison.Ordinal);
    Assert.Contains("ProcessPendingAsync", worker, StringComparison.Ordinal);
    Assert.Contains("catch (Exception ex)", worker, StringComparison.Ordinal);
    Assert.Contains("Task.Delay(pollInterval, _clock, stoppingToken)", worker, StringComparison.Ordinal);
    Assert.Contains("AddScoped<IOnlineOrderImportProcessor, RestaurantOnlineOrderImportProcessor>()", registration, StringComparison.Ordinal);
    Assert.Contains("AddHostedService<RestaurantOnlineOrderImportWorker>()", program, StringComparison.Ordinal);
    Assert.Contains("\"OnlineOrderingProcessing\"", trainingSettings, StringComparison.Ordinal);
    Assert.Contains("\"Enabled\": false", trainingSettings, StringComparison.Ordinal);
  }
}
