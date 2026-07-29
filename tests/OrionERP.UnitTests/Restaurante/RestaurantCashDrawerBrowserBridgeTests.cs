namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantCashDrawerBrowserBridgeTests
{
  [Fact]
  public void Pos_OpensLaptopDrawerOnlyAfterSuccessfulCashOrderCreation()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPosPage.razor");

    Assert.Contains("shouldOpenCashDrawer = cashReceived > 0.01m", page, StringComparison.Ordinal);
    var orderCreation = page.IndexOf("lastOrder = await OrderService.CreateOrderAsync", StringComparison.Ordinal);
    var drawerPulse = page.IndexOf(
      "Js.InvokeAsync<string>(\"restaurantUi.openCashDrawer\", \"TM-T20\")",
      StringComparison.Ordinal);
    Assert.True(orderCreation >= 0);
    Assert.True(drawerPulse > orderCreation);
  }

  [Fact]
  public void BrowserBridge_ConnectsToLocalQzTrayAndSendsEpsonDrawerPulse()
  {
    var script = ReadRepoFile("src/OrionERP.Web/wwwroot/js/restaurant-ui.js");

    Assert.Contains("qzClient.websocket.connect", script, StringComparison.Ordinal);
    Assert.Contains("qzClient.printers.find()", script, StringComparison.Ordinal);
    Assert.Contains("qzClient.security.setCertificatePromise", script, StringComparison.Ordinal);
    Assert.Contains("qzClient.security.setSignaturePromise", script, StringComparison.Ordinal);
    Assert.Contains("qzClient.security.setSignatureAlgorithm(\"SHA512\")", script, StringComparison.Ordinal);
    Assert.Contains("/api/restaurant/qz/certificate", script, StringComparison.Ordinal);
    Assert.Contains("/api/restaurant/qz/sign", script, StringComparison.Ordinal);
    Assert.Contains("flavor: \"hex\"", script, StringComparison.Ordinal);
    Assert.Contains("\"1B700032FA\"", script, StringComparison.Ordinal);
    Assert.Contains("TM-T20", script, StringComparison.Ordinal);
  }

  [Fact]
  public void Host_LoadsVendoredQzLibraryBeforeRestaurantBridge()
  {
    var host = ReadRepoFile("src/OrionERP.Web/Pages/_Host.cshtml");

    var qzScript = host.IndexOf("qz-tray-2.2.6.js", StringComparison.Ordinal);
    var restaurantScript = host.IndexOf("restaurant-ui.js", StringComparison.Ordinal);
    Assert.True(qzScript >= 0);
    Assert.True(restaurantScript > qzScript);
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(
      AppContext.BaseDirectory,
      "../../../../../",
      relativePath)));
}
