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
  [InlineData("[]")]
  [InlineData("{}")]
  [InlineData("{\"Tuesday\":{}}")]
  [InlineData("{\"Tuesday\":[null]}")]
  [InlineData("{\"Tuesday\":[{\"opens\":\"later\",\"closes\":\"22:00\"}]}")]
  [InlineData("{\"Tuesday\":[{\"opens\":\"09:00\",\"closes\":\"09:00\"}]}")]
  public void Invalid_opening_hours_never_invent_a_schedule(string? value)
  {
    var at = new DateTimeOffset(2026, 9, 1, 18, 0, 0, TimeSpan.Zero);

    Assert.Equal("Consulta el horario", RestaurantOpeningHours.Today(value, "UTC", at));
    Assert.Equal("Horario no disponible", RestaurantOpeningHours.Week(value)[1].Hours);
    Assert.Null(RestaurantOpeningHours.IsOpen(value, "UTC", at));
  }

  [Theory]
  [InlineData("9", "\"22:00\"")]
  [InlineData("true", "\"22:00\"")]
  [InlineData("[]", "\"22:00\"")]
  [InlineData("{}", "\"22:00\"")]
  [InlineData("null", "\"22:00\"")]
  [InlineData("\"09:00\"", "22")]
  [InlineData("\"09:00\"", "false")]
  [InlineData("\"09:00\"", "[]")]
  [InlineData("\"09:00\"", "{}")]
  [InlineData("\"09:00\"", "null")]
  public void Non_string_interval_times_leave_both_schedule_and_status_unknown(string opens, string closes)
  {
    var hours = $$"""
      {"Monday":[],"Tuesday":[{"opens":{{opens}},"closes":{{closes}}}]}
      """;
    var at = new DateTimeOffset(2026, 9, 1, 18, 0, 0, TimeSpan.Zero);

    Assert.Equal("Consulta el horario", RestaurantOpeningHours.Today(hours, "UTC", at));
    Assert.Equal("Horario no disponible", RestaurantOpeningHours.Week(hours)[1].Hours);
    Assert.Null(RestaurantOpeningHours.IsOpen(hours, "UTC", at));
  }

  [Theory]
  [InlineData(8, 59, false)]
  [InlineData(9, 0, true)]
  [InlineData(13, 59, true)]
  [InlineData(14, 0, false)]
  [InlineData(15, 59, false)]
  [InlineData(16, 0, true)]
  [InlineData(21, 59, true)]
  [InlineData(22, 0, false)]
  public void Split_day_status_includes_opening_and_excludes_closing_and_the_break(
    int hour,
    int minute,
    bool expected)
  {
    var at = new DateTimeOffset(2026, 9, 1, hour, minute, 0, TimeSpan.Zero);

    Assert.Equal(expected, RestaurantOpeningHours.IsOpen(HoursJson, "UTC", at));
  }

  [Theory]
  [InlineData("2026-09-06T07:00:00Z", "22:00–02:00", false)]
  [InlineData("2026-09-07T03:59:00Z", "22:00–02:00", false)]
  [InlineData("2026-09-07T04:00:00Z", "22:00–02:00", true)]
  [InlineData("2026-09-07T05:59:00Z", "22:00–02:00", true)]
  [InlineData("2026-09-07T06:00:00Z", "Cerrado", true)]
  [InlineData("2026-09-07T07:59:00Z", "Cerrado", true)]
  [InlineData("2026-09-07T08:00:00Z", "Cerrado", false)]
  public void Overnight_status_follows_the_previous_local_day_across_midnight_and_the_week_boundary(
    string utcInstant,
    string expectedToday,
    bool expectedOpen)
  {
    const string hours = """
      {"Saturday":[],"Sunday":[{"opens":"22:00","closes":"02:00"}],"Monday":[]}
      """;
    var at = DateTimeOffset.Parse(utcInstant);

    Assert.Equal(expectedToday, RestaurantOpeningHours.Today(hours, "America/Mexico_City", at));
    Assert.Equal(expectedOpen, RestaurantOpeningHours.IsOpen(hours, "America/Mexico_City", at));
  }

  [Theory]
  [InlineData(1, null)]
  [InlineData(12, true)]
  [InlineData(22, null)]
  public void Partial_schedule_can_prove_open_but_cannot_prove_closed(int hour, bool? expectedOpen)
  {
    const string hours = """
      {"Tuesday":[{"opens":"09:00","closes":"22:00"}]}
      """;
    var at = new DateTimeOffset(2026, 9, 1, hour, 0, 0, TimeSpan.Zero);

    Assert.Equal("09:00–22:00", RestaurantOpeningHours.Today(hours, "UTC", at));
    Assert.Equal(expectedOpen, RestaurantOpeningHours.IsOpen(hours, "UTC", at));
  }

  [Theory]
  [InlineData(1, true)]
  [InlineData(2, null)]
  public void Previous_overnight_interval_can_prove_open_when_today_is_missing(int hour, bool? expectedOpen)
  {
    const string hours = """
      {"Monday":[{"opens":"22:00","closes":"02:00"}]}
      """;
    var at = new DateTimeOffset(2026, 9, 1, hour, 0, 0, TimeSpan.Zero);

    Assert.Equal("Consulta el horario", RestaurantOpeningHours.Today(hours, "UTC", at));
    Assert.Equal(expectedOpen, RestaurantOpeningHours.IsOpen(hours, "UTC", at));
  }

  [Fact]
  public void Unknown_time_zone_leaves_schedule_and_status_unknown()
  {
    var at = new DateTimeOffset(2026, 9, 1, 18, 0, 0, TimeSpan.Zero);

    Assert.Equal("Consulta el horario", RestaurantOpeningHours.Today(HoursJson, "Unknown/Restaurant", at));
    Assert.Null(RestaurantOpeningHours.IsOpen(HoursJson, "Unknown/Restaurant", at));
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

    // The bound site must come from the SiteKey/SiteCode space. binding.SiteId is
    // the platform site id (orion.Site) and does not address restaurante.Site,
    // so scoping the image query with it 404s every product on the public site.
    Assert.Contains("binding.SiteKey", program, StringComparison.Ordinal);
    Assert.DoesNotContain("binding.SiteId", program, StringComparison.Ordinal);
    Assert.Contains("siteInfo.SiteCode = @SiteCode", catalog, StringComparison.Ordinal);
    Assert.Contains("siteInfo.Rfc = product.Rfc AND siteInfo.IsEnabled = 1", catalog, StringComparison.Ordinal);
    Assert.Contains("product.KitchenStationId IS NULL OR station.SiteId = scopedSite.Id", catalog, StringComparison.Ordinal);
    Assert.Contains("product.IsActive = 1", catalog, StringComparison.Ordinal);
  }

  [Fact]
  public void Public_menu_only_uses_a_published_schedule_for_the_bound_site()
  {
    var contract = ReadRepoFile(
      "src/OrionERP.Application/Features/Restaurante/IRestaurantCatalogService.cs");
    var publicCatalog = ReadRepoFile(
      "src/OrionERP.Infrastructure/Features/Restaurante/BrunoPublicCatalogService.cs");
    var catalog = ReadRepoFile(
      "src/OrionERP.Infrastructure/Features/Restaurante/RestaurantCatalogService.cs");
    var orders = ReadRepoFile(
      "src/OrionERP.Infrastructure/Features/Restaurante/RestaurantOrderService.cs");

    Assert.Contains("GetPublicCatalogAsync", contract, StringComparison.Ordinal);
    Assert.Contains("_catalogService.GetPublicCatalogAsync", publicCatalog, StringComparison.Ordinal);
    Assert.DoesNotContain("_catalogService.GetPosCatalogAsync", publicCatalog, StringComparison.Ordinal);
    Assert.Contains("FROM restaurante.MenuSchedule anySchedule", catalog, StringComparison.Ordinal);
    Assert.Contains("currentSchedule.SiteId = @SiteId", catalog, StringComparison.Ordinal);
    Assert.Contains(
      "scheduleInventory.HasAny IS NULL OR currentScheduleMatch.IsActiveNow = 1",
      catalog,
      StringComparison.Ordinal);
    Assert.Contains("currentSchedule.DayOfWeek = @PreviousDayOfWeek", catalog, StringComparison.Ordinal);
    Assert.Contains(
      "scheduleInventory.HasAny IS NULL OR currentScheduleMatch.IsActiveNow=1",
      orders,
      StringComparison.Ordinal);
    Assert.Contains("currentSchedule.DayOfWeek=@PreviousDayOfWeek", orders, StringComparison.Ordinal);
  }

  [Fact]
  public void Public_menu_never_falls_back_to_the_private_operational_catalog()
  {
    var catalog = ReadRepoFile(
      "src/OrionERP.Infrastructure/Features/Restaurante/RestaurantCatalogService.cs");

    Assert.Contains("includeActiveProductFallback: false", catalog, StringComparison.Ordinal);
    Assert.Contains(
      "sectionDtos.Count == 0 && includeActiveProductFallback",
      catalog,
      StringComparison.Ordinal);
    Assert.Contains("WHERE @IncludeOperations = 1", catalog, StringComparison.Ordinal);
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
