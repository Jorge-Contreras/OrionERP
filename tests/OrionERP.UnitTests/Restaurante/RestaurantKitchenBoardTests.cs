using OrionERP.Application.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantKitchenBoardTests
{
  [Fact]
  public void KitchenProgress_IsNotStartedWhenEveryKitchenLineIsPending()
  {
    var order = OrderWithKitchenStatuses("Pending", "Pending");

    var progress = RestaurantKitchenProgressRules.Classify(order);

    Assert.Equal(RestaurantKitchenProgress.NotStarted, progress);
  }

  [Theory]
  [InlineData("Preparing", "Pending")]
  [InlineData("Ready", "Pending")]
  public void KitchenProgress_IsPreparingWhenKitchenLinesArePartiallyFulfilled(
    string firstStatus,
    string secondStatus)
  {
    var order = OrderWithKitchenStatuses(firstStatus, secondStatus);

    var progress = RestaurantKitchenProgressRules.Classify(order);

    Assert.Equal(RestaurantKitchenProgress.Preparing, progress);
  }

  [Fact]
  public void KitchenProgress_IsReadyWhenEveryKitchenLineIsReady()
  {
    var order = OrderWithKitchenStatuses("Ready", "Ready");

    var progress = RestaurantKitchenProgressRules.Classify(order);

    Assert.Equal(RestaurantKitchenProgress.Ready, progress);
  }

  [Fact]
  public void KitchenProgress_IncludesCustomAndIgnoresCancelledLines()
  {
    var order = OrderWithKitchenStatuses("Ready");
    order.Lines = order.Lines.Concat(
    [
      new RestaurantOrderLineDto { IsCustom = true, Status = "Pending" },
      new RestaurantOrderLineDto { Status = "Cancelled" }
    ]).ToList();

    var progress = RestaurantKitchenProgressRules.Classify(order);

    Assert.Equal(RestaurantKitchenProgress.Preparing, progress);
  }

  [Theory]
  [InlineData("Pending", RestaurantKitchenProgress.NotStarted)]
  [InlineData("Preparing", RestaurantKitchenProgress.Preparing)]
  [InlineData("Ready", RestaurantKitchenProgress.Ready)]
  public void KitchenProgress_ClassifiesCustomOnlyOrder(string status, RestaurantKitchenProgress expected)
  {
    var order = new RestaurantOrderDto
    {
      Lines =
      [
        new RestaurantOrderLineDto { IsCustom = true, Status = status }
      ]
    };

    var progress = RestaurantKitchenProgressRules.Classify(order);

    Assert.Equal(expected, progress);
  }

  [Fact]
  public void KitchenBoard_KeepsOrdersInFolioSequence()
  {
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOrderService.cs");
    var kitchenQueryStart = service.IndexOf(
      "public async Task<RestaurantKitchenBoardDto> GetKitchenBoardAsync",
      StringComparison.Ordinal);
    var publicBoardStart = service.IndexOf(
      "public async Task<IReadOnlyList<RestaurantPublicOrderDto>> GetPublicBoardAsync",
      kitchenQueryStart,
      StringComparison.Ordinal);
    var kitchenQuery = service[kitchenQueryStart..publicBoardStart];

    Assert.Contains(
      "ORDER BY orderInfo.OperationalDate, orderInfo.Folio, orderInfo.CreatedAt, orderInfo.Id;",
      kitchenQuery,
      StringComparison.Ordinal);
    Assert.DoesNotContain("ORDER BY orderInfo.Priority", kitchenQuery, StringComparison.Ordinal);
    Assert.DoesNotContain("CASE orderInfo.[Status]", kitchenQuery, StringComparison.Ordinal);
    Assert.Contains("AND lineInfo.[Status] <> 'Cancelled'", kitchenQuery, StringComparison.Ordinal);
    Assert.DoesNotContain("lineInfo.IsCustom = 0", kitchenQuery, StringComparison.Ordinal);
  }

  [Fact]
  public void KitchenPage_UsesStableFolioOrderingAndProgressColors()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantKitchenPage.razor");
    var styles = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantKitchenPage.razor.css");

    Assert.Contains("@foreach (var sectionGroup in VisibleSectionGroups)", page, StringComparison.Ordinal);
    Assert.Contains("@foreach (var card in sectionGroup.Cards)", page, StringComparison.Ordinal);
    Assert.Contains(".ThenBy(order => order.Folio)", page, StringComparison.Ordinal);
    Assert.Contains("RestaurantKitchenProgress.Ready => \"kds-ticket--complete\"", page, StringComparison.Ordinal);
    Assert.Contains("RestaurantKitchenProgress.Preparing => \"kds-ticket--partial\"", page, StringComparison.Ordinal);
    Assert.Contains("_ => \"kds-ticket--not-started\"", page, StringComparison.Ordinal);
    Assert.Contains(".kds-ticket--complete", styles, StringComparison.Ordinal);
    Assert.Contains(".kds-ticket--partial", styles, StringComparison.Ordinal);
    Assert.Contains(".kds-ticket--not-started", styles, StringComparison.Ordinal);
  }

  [Fact]
  public void KitchenPage_GroupsSectionsAndPersistsDisabledFiltersPerRfcAndSite()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantKitchenPage.razor");

    Assert.Contains("Cargo Personalizado", page, StringComparison.Ordinal);
    Assert.Contains("GroupBy(line => SectionForLine(line).Key", page, StringComparison.Ordinal);
    Assert.Contains("orion.restaurant.kds.sections:{CurrentRfc}:{selectedSiteId}", page, StringComparison.Ordinal);
    Assert.Contains("localStorage.setItem", page, StringComparison.Ordinal);
    Assert.Contains("localStorage.getItem", page, StringComparison.Ordinal);
    Assert.Contains("disabledSectionKeys", page, StringComparison.Ordinal);
    Assert.DoesNotContain("line => !line.IsCustom", page, StringComparison.Ordinal);
  }

  [Fact]
  public void Pos_CapturesTheSelectedMenuSectionAndSendsCustomChargesToKitchen()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPosPage.razor");

    Assert.Contains("SelectProduct(product, activeSectionId)", page, StringComparison.Ordinal);
    Assert.Contains("MenuSectionId = sectionId", page, StringComparison.Ordinal);
    Assert.Contains("MenuSectionId = item.MenuSectionId is > 0 ? item.MenuSectionId : null", page, StringComparison.Ordinal);
    Assert.Contains("se enviará a Cocina en la sección Cargo Personalizado", page, StringComparison.Ordinal);
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath)));

  private static RestaurantOrderDto OrderWithKitchenStatuses(params string[] statuses)
    => new()
    {
      Lines = statuses
        .Select(status => new RestaurantOrderLineDto { Status = status })
        .ToList()
    };
}
