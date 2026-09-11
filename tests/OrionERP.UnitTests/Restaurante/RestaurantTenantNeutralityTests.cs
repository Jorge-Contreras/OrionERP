namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantTenantNeutralityTests
{
  [Fact]
  public void Restaurant_administration_uses_the_company_from_the_active_session()
  {
    var claimsFactory = ReadRepoFile("src/OrionERP.Web/Identity/EmployeeCompanyClaimsPrincipalFactory.cs");
    var promotions = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPromotionsPage.razor");
    var publicSite = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantPublicSitePage.razor");
    var legacyRoute = ReadRepoFile("src/OrionERP.Web/Pages/RestaurantPublicSiteLegacy.cshtml.cs");

    Assert.DoesNotContain("BrunoRestaurantConstants", claimsFactory, StringComparison.Ordinal);
    Assert.DoesNotContain("IsInRoleAsync", claimsFactory, StringComparison.Ordinal);
    Assert.Contains("private string CurrentRfc => RfcState.RequireRfc();", promotions, StringComparison.Ordinal);
    Assert.Contains("catch (UnauthorizedAccessException)", promotions, StringComparison.Ordinal);
    Assert.DoesNotContain("BrunoRestaurantConstants", promotions, StringComparison.Ordinal);

    Assert.Contains("@page \"/restaurante/sitio-publico\"", publicSite, StringComparison.Ordinal);
    Assert.DoesNotContain("sitio-brunos", publicSite, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("private string CurrentRfc => RfcState.RequireRfc();", publicSite, StringComparison.Ordinal);
    Assert.Contains("catch (UnauthorizedAccessException)", publicSite, StringComparison.Ordinal);
    Assert.Contains("GetSettingsAsync(CurrentRfc, site.Id)", publicSite, StringComparison.Ordinal);
    Assert.Contains("settings.SiteId != selectedSiteId.Value", publicSite, StringComparison.Ordinal);
    Assert.DoesNotContain("BrunoRestaurantConstants", publicSite, StringComparison.Ordinal);
    Assert.Contains("LocalRedirect(\"/restaurante/sitio-publico\")", legacyRoute, StringComparison.Ordinal);
    Assert.Contains("Authorize(Policy = \"RestaurantAdmin\")", legacyRoute, StringComparison.Ordinal);
  }

  [Fact]
  public void Explicit_signage_routes_never_fall_back_to_another_companys_boards()
  {
    var pageModel = ReadRepoFile("src/OrionERP.Web/Pages/Menus.cshtml.cs");
    var page = ReadRepoFile("src/OrionERP.Web/Pages/Menus.cshtml");

    Assert.Contains(
      "UseLegacyStaticBoards => _allowsLegacyFallback && Screen is null",
      pageModel,
      StringComparison.Ordinal);
    Assert.Contains(
      "Screen is null && !_allowsLegacyFallback ? NotFound() : Page()",
      pageModel,
      StringComparison.Ordinal);
    Assert.Contains(
      "StatusCode(StatusCodes.Status503ServiceUnavailable)",
      pageModel,
      StringComparison.Ordinal);
    Assert.Contains("Signage:DefaultPublicSiteKey", pageModel, StringComparison.Ordinal);
    Assert.DoesNotContain("BrunoRestaurantConstants", pageModel, StringComparison.Ordinal);
    Assert.DoesNotContain("Bruno's", page, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Shared_restaurant_surfaces_do_not_show_the_seed_restaurant_brand()
  {
    var paths = new[]
    {
      "src/OrionERP.Web/Shared/NavigationCatalog.cs",
      "src/OrionERP.Web/Features/Restaurante/RestaurantPromotionsPage.razor",
      "src/OrionERP.Web/Features/Restaurante/RestaurantPublicSitePage.razor",
      "src/OrionERP.Web/Features/Restaurante/RestaurantPosPage.razor",
      "src/OrionERP.Web/Features/Restaurante/RestaurantReceiptPdfService.cs",
      "src/OrionERP.Web/Features/Restaurante/RestaurantKitchenPage.razor"
    };
    var content = string.Join('\n', paths.Select(ReadRepoFile));

    Assert.DoesNotContain("Club Bruno", content, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("brunosgarden.com", content, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("Images/Brunos", content, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("Bruno está", content, StringComparison.OrdinalIgnoreCase);
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(RepoPath(relativePath));

  private static string RepoPath(string relativePath)
  {
    var root = AppContext.BaseDirectory;
    while (!File.Exists(Path.Combine(root, "OrionERP.sln")))
    {
      root = Directory.GetParent(root)?.FullName
        ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    return Path.Combine(root, relativePath);
  }
}
