using System.ComponentModel.DataAnnotations;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantCustomerNameFlowTests
{
  [Fact]
  public void OrderResponses_CarryTheCapturedCustomerName()
  {
    var result = new RestaurantOrderResult { CustomerName = "María" };
    var publicOrder = new RestaurantPublicOrderDto { CustomerName = "María" };

    Assert.Equal("María", result.CustomerName);
    Assert.Equal("María", publicOrder.CustomerName);
  }

  [Fact]
  public void OrderRequest_RequiresCustomerName()
  {
    var request = new RestaurantOrderCreateRequest
    {
      CustomerName = string.Empty,
      Rfc = "OHM191112Q26",
      SiteId = 1,
      IdempotencyKey = "test",
      Lines = [new() { ProductId = 1 }]
    };
    var validationResults = new List<ValidationResult>();

    var isValid = Validator.TryValidateObject(request, new ValidationContext(request), validationResults, validateAllProperties: true);

    Assert.False(isValid);
    Assert.Contains(validationResults, result => result.MemberNames.Contains(nameof(RestaurantOrderCreateRequest.CustomerName)));
  }

  [Fact]
  public void RestaurantScreens_ShowAndAnnounceCustomerNameWithTheFolio()
  {
    var ordersPage = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantOrdersPage.razor");
    var posPage = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPosPage.razor");
    var publicBoard = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPublicBoardPage.razor");
    var announcement = ReadRepoFile("src/OrionERP.Web/wwwroot/js/restaurant-ui.js");
    var orderService = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOrderService.cs");

    Assert.Contains("order.Folio", ordersPage, StringComparison.Ordinal);
    Assert.Contains("order.CustomerName", ordersPage, StringComparison.Ordinal);
    Assert.Contains("order.Folio", publicBoard, StringComparison.Ordinal);
    Assert.Contains("order.CustomerName", publicBoard, StringComparison.Ordinal);
    Assert.Contains("ready.CustomerName", publicBoard, StringComparison.Ordinal);
    Assert.Contains("El nombre del cliente es obligatorio.", posPage, StringComparison.Ordinal);
    Assert.Contains("El nombre del cliente es obligatorio.", orderService, StringComparison.Ordinal);
    Assert.Contains("aria-required=\"true\"", posPage, StringComparison.Ordinal);
    Assert.Contains("customerName.trim()", announcement, StringComparison.Ordinal);
    Assert.Contains("su orden ${folio}", announcement, StringComparison.Ordinal);
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath)));
}
