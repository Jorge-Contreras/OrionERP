using OrionERP.Application.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantKitchenNotesFlowTests
{
  [Fact]
  public void KitchenOrderDto_CarriesOrderNotes()
  {
    var order = new RestaurantOrderDto { Notes = "Sin cebolla en toda la orden" };

    Assert.Equal("Sin cebolla en toda la orden", order.Notes);
  }

  [Fact]
  public void KitchenBoard_LoadsAndDisplaysOrderNotes()
  {
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOrderService.cs");
    var kitchenPage = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantKitchenPage.razor");

    Assert.Contains("diningTable.[Name] AS TableName, orderInfo.Notes", service, StringComparison.Ordinal);
    Assert.Contains("!string.IsNullOrWhiteSpace(order.Notes)", kitchenPage, StringComparison.Ordinal);
    Assert.Contains("Nota de la orden", kitchenPage, StringComparison.Ordinal);
    Assert.Contains("@order.Notes", kitchenPage, StringComparison.Ordinal);
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath)));
}
