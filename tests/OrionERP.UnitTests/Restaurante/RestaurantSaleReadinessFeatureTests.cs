namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantSaleReadinessFeatureTests
{
  [Fact]
  public void PosExposesAuthorizedInconspicuousDiagnosticDownload()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPosPage.razor");

    Assert.Contains("AuthorizeView Policy=\"RestaurantAdmin\"", page, StringComparison.Ordinal);
    Assert.Contains("DownloadSaleReadinessAsync", page, StringComparison.Ordinal);
    Assert.Contains("Descargar diagnóstico preventivo de venta", page, StringComparison.Ordinal);
    Assert.Contains("triggerFileDownload", page, StringComparison.Ordinal);
    Assert.Contains("isDownloadingReadiness", page, StringComparison.Ordinal);
  }

  [Fact]
  public void DiagnosticServiceContainsNoOrderOrReservationMutations()
  {
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantSaleReadinessService.cs");
    var orderService = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOrderService.cs");

    Assert.DoesNotContain("INSERT INTO restaurante.[Order]", service, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("INSERT INTO logistica.InventoryReservation", service, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("UPDATE logistica.StockBalance", service, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("RestaurantSaleRequirementCalculator.Calculate", service, StringComparison.Ordinal);
    Assert.Contains("RestaurantSaleRequirementCalculator.Calculate", orderService, StringComparison.Ordinal);
  }

  [Fact]
  public void ComboReadinessEvaluatesComponentRecipesInventoryAndModifiers()
  {
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantSaleReadinessService.cs");

    Assert.Contains("option.ComponentProduct", service, StringComparison.Ordinal);
    Assert.Contains("component.MaterialId.Value", service, StringComparison.Ordinal);
    Assert.Contains("option.Quantity", service, StringComparison.Ordinal);
    Assert.Contains("EvaluateModifiers(", service, StringComparison.Ordinal);
    Assert.Contains("defaultRequirements", service, StringComparison.Ordinal);
    Assert.Contains("EvaluateInventory(material, requirement.Value", service, StringComparison.Ordinal);
  }

  [Fact]
  public void PosShowsTheShortageBadgeToEveryCashierButGatesTheCountBehindSupervisor()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPosPage.razor");

    // El badge vive antes del bloque autorizado: la caja tiene que ver el aviso aunque no pueda
    // levantar el conteo.
    var badgeIndex = page.IndexOf("pos-sale-alerts", StringComparison.Ordinal);
    var adminGateIndex = page.IndexOf("AuthorizeView Policy=\"RestaurantAdmin\"", StringComparison.Ordinal);
    Assert.True(badgeIndex >= 0, "El POS debe mostrar el botón de faltantes.");
    Assert.True(badgeIndex < adminGateIndex, "El badge de faltantes no debe quedar detrás de la política de administrador.");

    Assert.Contains("RefreshSaleAlertsAsync", page, StringComparison.Ordinal);
    Assert.Contains("RestaurantSaleAlertBuilder.Build", page, StringComparison.Ordinal);
    Assert.Contains("<RestaurantPosSaleAlertsPanel", page, StringComparison.Ordinal);
  }

  [Fact]
  public void SaleAlertsPanelCreatesTheCountOnlyForSupervisorsAndChecksTheScopeFirst()
  {
    var panel = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPosSaleAlertsPanel.razor");

    var gateIndex = panel.IndexOf("AuthorizeView Policy=\"RestaurantAdmin\"", StringComparison.Ordinal);
    var createButtonIndex = panel.IndexOf("PrepareOrCreateCountAsync", StringComparison.Ordinal);
    Assert.True(gateIndex >= 0, "Crear el conteo debe exigir la política de administrador.");
    Assert.True(gateIndex < createButtonIndex, "El botón de crear conteo debe quedar dentro del bloque autorizado.");

    // La vista previa va antes de crear: una sesión abierta que reclame el mismo material aborta
    // la creación completa del lado del servicio.
    Assert.Contains("PreviewScopeAsync", panel, StringComparison.Ordinal);
    Assert.True(
      panel.IndexOf("PreviewScopeAsync", StringComparison.Ordinal)
        < panel.IndexOf("CreateSessionAsync", StringComparison.Ordinal),
      "El alcance se revisa antes de crear el conteo.");
    Assert.Contains("PhysicalCountSessionScopeTypes.Material", panel, StringComparison.Ordinal);
  }

  [Fact]
  public void ThePredictedShortageMessageIsWordForWordTheOneThePosThrowsWhenCharging()
  {
    // El panel promete lo que va a pasar al cobrar. Si los dos textos se separan, el cajero ve un
    // aviso y luego un mensaje distinto en el modal de supervisor.
    const string sharedFormat =
      "$\"Inventario insuficiente para {material.Code} · {material.Name}. Faltan {";

    var diagnostic = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantSaleReadinessService.cs");
    var orderService = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOrderService.cs");

    Assert.Contains(sharedFormat, diagnostic, StringComparison.Ordinal);
    Assert.Contains(sharedFormat, orderService, StringComparison.Ordinal);
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath)));
}
