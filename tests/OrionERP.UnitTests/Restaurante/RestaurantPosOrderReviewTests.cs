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
