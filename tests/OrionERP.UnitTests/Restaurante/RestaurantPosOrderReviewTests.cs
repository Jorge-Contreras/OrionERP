namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantPosOrderReviewTests
{
  [Fact]
  public void Pos_UsesAnOrderFirstWorkspaceWithCatalogThumbnails()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPosPage.razor");

    Assert.Contains("class=\"pos-workspace-tabs\" role=\"tablist\"", page, StringComparison.Ordinal);
    Assert.Contains("aria-selected=\"@(!isMenuOpen)\"", page, StringComparison.Ordinal);
    Assert.Contains("SelectProduct(product, activeSectionId)", page, StringComparison.Ordinal);
    Assert.Contains("line.ProductId is long productId && ProductHasImage(productId)", page, StringComparison.Ordinal);
    Assert.Contains("MenuSectionId = sectionId", page, StringComparison.Ordinal);
    Assert.Contains("CloseProductModal(); isMenuOpen = false;", page, StringComparison.Ordinal);
  }

  [Fact]
  public void Pos_LoadsOnlyVisibleProductImagesThroughTheAuthorizedThumbnailEndpoint()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPosPage.razor");
    var api = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantProductImagesApi.cs");
    var program = ReadRepoFile("src/OrionERP.Web/Program.cs");

    Assert.Contains("src=\"@ProductImageUrl(product.Id)\"", page, StringComparison.Ordinal);
    Assert.Contains("loading=\"lazy\" decoding=\"async\"", page, StringComparison.Ordinal);
    Assert.DoesNotContain("Convert.ToBase64String(image.Value.Bytes)", page, StringComparison.Ordinal);
    Assert.Contains("/api/restaurant/products/{productId:long}/thumbnail", api, StringComparison.Ordinal);
    Assert.Contains("RequireAuthorization(\"RestaurantPos\")", api, StringComparison.Ordinal);
    Assert.Contains("companyContext.RequireRfc()", api, StringComparison.Ordinal);
    Assert.Contains("app.MapRestaurantProductImagesApi();", program, StringComparison.Ordinal);
  }

  [Fact]
  public void Pos_LoadsItsCatalogOnceAfterPrerenderingAndShowsAnHonestLoadingState()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPosPage.razor");

    Assert.Contains("private bool isBusy = true;", page, StringComparison.Ordinal);
    Assert.Contains("protected override void OnInitialized() => Realtime.EventReceived += OnRealtimeEventAsync;", page, StringComparison.Ordinal);
    Assert.Contains("protected override async Task OnAfterRenderAsync(bool firstRender)", page, StringComparison.Ordinal);
    Assert.Contains("if (!firstRender) return;\n    await LoadAsync();\n    StateHasChanged();", page, StringComparison.Ordinal);

    var loadingState = page.IndexOf("@if (isBusy || (sites.Count > 0 && catalog is null))", StringComparison.Ordinal);
    var emptyState = page.IndexOf("else if (sites.Count == 0)", StringComparison.Ordinal);
    Assert.True(loadingState >= 0 && emptyState > loadingState, "Loading must win over the real no-site empty state.");
  }

  [Fact]
  public void Pos_ReturnsToTheMenuWhenTheOrderBecomesEmpty()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPosPage.razor");
    var styles = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPosPage.razor.css");

    Assert.Contains("if (cart.Count == 0) ShowMenu();", page, StringComparison.Ordinal);
    Assert.Contains("private async Task ClearCartAsync() { cart = []; promotionQuote = null; ShowMenu();", page, StringComparison.Ordinal);
    Assert.Contains("grid-template-columns: minmax(0, 1fr) minmax(370px, 420px);", styles, StringComparison.Ordinal);
    Assert.DoesNotContain("max-height: 32vh", styles, StringComparison.Ordinal);
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath)));
}
