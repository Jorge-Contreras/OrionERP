using System.Text.Json;
using OrionERP.Bruno.Web.Services;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantPublicPresentationTests
{
  private const string HoursJson =
    """
    {
      "Monday": [],
      "Tuesday": [
        { "opens": "09:00", "closes": "14:00" },
        { "opens": "16:00", "closes": "22:00" }
      ]
    }
    """;

  [Fact]
  public void Opening_hours_use_the_configured_time_zone_and_support_split_days()
  {
    var mondayUtc = new DateTimeOffset(2026, 8, 31, 18, 0, 0, TimeSpan.Zero);
    var tuesdayUtc = mondayUtc.AddDays(1);

    Assert.Equal("Cerrado", RestaurantOpeningHours.Today(HoursJson, "UTC", mondayUtc));
    Assert.Equal(
      "09:00–14:00 · 16:00–22:00",
      RestaurantOpeningHours.Today(HoursJson, "UTC", tuesdayUtc));
    Assert.Equal("Cerrado", RestaurantOpeningHours.Week(HoursJson)[0].Hours);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("not-json")]
  [InlineData("{\"Tuesday\":[{\"opens\":\"later\",\"closes\":\"22:00\"}]}")]
  public void Invalid_opening_hours_never_invent_a_schedule(string? value)
  {
    var at = new DateTimeOffset(2026, 9, 1, 18, 0, 0, TimeSpan.Zero);

    Assert.Equal("Consulta el horario", RestaurantOpeningHours.Today(value, "UTC", at));
  }

  [Fact]
  public void Public_settings_lookup_requires_one_exact_rfc_and_site()
  {
    var contract = ReadRepoFile(
      "src/OrionERP.Application/Features/Restaurante/IBrunoPublicCatalogService.cs");
    var service = ReadRepoFile(
      "src/OrionERP.Infrastructure/Features/Restaurante/BrunoPublicCatalogService.cs");

    Assert.DoesNotContain("int? siteId", contract, StringComparison.Ordinal);
    Assert.DoesNotContain("SELECT TOP(1)", service, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("@SiteId IS NULL", service, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("WHERE Rfc=@Rfc AND SiteId=@SiteId", service, StringComparison.Ordinal);
    Assert.Contains("site.SiteCode=@SiteCode", service, StringComparison.Ordinal);
    Assert.Contains("site.IsEnabled=1", service, StringComparison.Ordinal);
  }

  [Fact]
  public void Restaurant_host_requires_a_complete_physical_presentation_manifest()
  {
    var program = ReadRepoFile("src/OrionERP.Bruno.Web/Program.cs");
    var developmentJson = ReadRepoFile("src/OrionERP.Bruno.Web/appsettings.Development.json");
    using var document = JsonDocument.Parse(developmentJson);
    var presentation = document.RootElement.GetProperty("PublicWebsitePresentation");

    Assert.Contains("PublicWebsitePresentationPolicy.Create", program, StringComparison.Ordinal);
    Assert.Contains("PublicWebsitePresentationPolicy.EnsureAssetsExist", program, StringComparison.Ordinal);
    Assert.Contains("AddPublicWebsiteInstance(connectionString, publicWebsite, presentation)", program, StringComparison.Ordinal);
    Assert.Equal("brunos-main", presentation.GetProperty("PublicSiteKey").GetString());
    Assert.True(presentation.GetProperty("Assets").EnumerateObject().Count() >= 9);
  }

  [Fact]
  public void Runtime_surfaces_do_not_embed_the_development_restaurant_identity()
  {
    var hostRoot = RepoPath("src/OrionERP.Bruno.Web");
    var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
      ".cs", ".razor", ".cshtml", ".css"
    };
    var runtimeFiles = Directory
      .EnumerateFiles(hostRoot, "*", SearchOption.AllDirectories)
      .Where(path => extensions.Contains(Path.GetExtension(path)))
      .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
      .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
    var content = string.Join('\n', runtimeFiles.Select(File.ReadAllText));

    Assert.DoesNotContain("Bruno's", content, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("Club Bruno", content, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("brunosgarden.com", content, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("/Images/Brunos", content, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Consent_versions_are_persisted_from_the_bound_presentation()
  {
    var registration = ReadRepoFile("src/OrionERP.Bruno.Web/Pages/Account/Register.cshtml.cs");
    var preferences = ReadRepoFile("src/OrionERP.Bruno.Web/Pages/Account/Member/Index.cshtml.cs");

    Assert.Contains("PrivacyVersion = _presentation.PrivacyVersion", registration, StringComparison.Ordinal);
    Assert.Contains("TermsVersion = _presentation.TermsVersion", registration, StringComparison.Ordinal);
    Assert.Contains("PrivacyVersion = _presentation.PrivacyVersion", preferences, StringComparison.Ordinal);
    Assert.Contains("TermsVersion = _presentation.TermsVersion", preferences, StringComparison.Ordinal);
    Assert.DoesNotContain("BrunoSiteConstants.PrivacyVersion", registration + preferences, StringComparison.Ordinal);
    Assert.DoesNotContain("BrunoSiteConstants.TermsVersion", registration + preferences, StringComparison.Ordinal);
  }

  [Fact]
  public void Public_product_images_are_scoped_to_the_bound_restaurant_site()
  {
    var program = ReadRepoFile("src/OrionERP.Bruno.Web/Program.cs");
    var catalog = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantCatalogService.cs");

    Assert.Contains("binding.SiteId", program, StringComparison.Ordinal);
    Assert.Contains("product.KitchenStationId IS NULL OR station.SiteId = @SiteId", catalog, StringComparison.Ordinal);
    Assert.Contains("siteInfo.Id = @SiteId AND siteInfo.IsEnabled = 1", catalog, StringComparison.Ordinal);
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
