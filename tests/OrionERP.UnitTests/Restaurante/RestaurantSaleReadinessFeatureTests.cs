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

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath)));
}

