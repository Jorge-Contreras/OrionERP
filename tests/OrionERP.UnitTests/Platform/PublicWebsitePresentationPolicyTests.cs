using OrionERP.Application.Features.Platform;

namespace OrionERP.UnitTests.Platform;

public sealed class PublicWebsitePresentationPolicyTests
{
  [Fact]
  public void Create_NormalizesAndBindsPresentationToInstance()
  {
    var profile = PublicWebsitePresentationPolicy.Create(Options(), Instance(), "hero-image");

    Assert.Equal("site-one", profile.PublicSiteKey);
    Assert.Equal("es-MX", profile.Locale);
    Assert.Equal("#123ABC", profile.PrimaryColor);
    Assert.Equal("/instances/site-one/hero.webp", profile.Asset("HERO-IMAGE"));
    Assert.Equal(
      "https://wa.me/525512345678?text=Hola%20sitio",
      profile.WhatsAppUrl("Hola sitio"));
  }

  [Fact]
  public void Create_RejectsProfileForAnotherPublicSite()
  {
    var options = Options();
    options.PublicSiteKey = "other-site";

    Assert.Throws<PublicWebsitePresentationException>(() =>
      PublicWebsitePresentationPolicy.Create(options, Instance(), "hero-image"));
  }

  [Theory]
  [InlineData("https://cdn.example/logo.svg")]
  [InlineData("//cdn.example/logo.svg")]
  [InlineData("/assets/../secret.txt")]
  [InlineData("/assets/%2e%2e/secret.txt")]
  [InlineData("/assets/logo.svg?v=2")]
  public void Create_RejectsNonLocalOrAmbiguousAssetPaths(string path)
  {
    var options = Options();
    options.Assets["logo"] = path;

    Assert.Throws<PublicWebsitePresentationException>(() =>
      PublicWebsitePresentationPolicy.Create(options, Instance(), "hero-image"));
  }

  [Fact]
  public void EnsureMatchesBinding_RejectsStalePresentationVersions()
  {
    var profile = PublicWebsitePresentationPolicy.Create(Options(), Instance(), "hero-image");
    var binding = Binding() with { BrandingVersion = 2 };

    Assert.Throws<PublicWebsitePresentationException>(() =>
      PublicWebsitePresentationPolicy.EnsureMatchesBinding(profile, binding));
  }

  [Fact]
  public void EnsureMatchesBinding_AcceptsOnlyTheCompleteFallbackPairBeforeExpiry()
  {
    var profile = PublicWebsitePresentationPolicy.Create(Options(), Instance(), "hero-image");
    var now = new DateTime(2026, 9, 2, 18, 0, 0, DateTimeKind.Utc);
    var binding = Binding() with
    {
      BrandingVersion = 2,
      ContentVersion = 3,
      FallbackBrandingVersion = 1,
      FallbackContentVersion = 1,
      FallbackUntilUtc = now.AddMinutes(5)
    };

    var match = PublicWebsitePresentationPolicy.EnsureMatchesBinding(profile, binding, now);

    Assert.True(match.IsFallback);
    Assert.Equal(now.AddMinutes(5), match.ValidUntilUtc);
  }

  [Fact]
  public void EnsureMatchesBinding_RejectsFallbackAtExpiryAndMixedPairs()
  {
    var profile = PublicWebsitePresentationPolicy.Create(Options(), Instance(), "hero-image");
    var now = new DateTime(2026, 9, 2, 18, 0, 0, DateTimeKind.Utc);
    var binding = Binding() with
    {
      BrandingVersion = 2,
      ContentVersion = 2,
      FallbackBrandingVersion = 1,
      FallbackContentVersion = 1,
      FallbackUntilUtc = now
    };

    Assert.Throws<PublicWebsitePresentationException>(() =>
      PublicWebsitePresentationPolicy.EnsureMatchesBinding(profile, binding, now));
    Assert.Throws<PublicWebsitePresentationException>(() =>
      PublicWebsitePresentationPolicy.EnsureMatchesBinding(
        profile,
        binding with
        {
          BrandingVersion = 1,
          FallbackBrandingVersion = 2,
          FallbackUntilUtc = now.AddMinutes(1)
        },
        now));
  }

  [Fact]
  public void Create_RejectsLegalVersionsThatCannotBePersistedByMembership()
  {
    var options = Options();
    options.PrivacyVersion = new string('v', 31);

    Assert.Throws<PublicWebsitePresentationException>(() =>
      PublicWebsitePresentationPolicy.Create(options, Instance(), "hero-image"));
  }

  [Fact]
  public void Create_RejectsAVisiblePhoneThatPointsToDifferentDigits()
  {
    var options = Options();
    options.WhatsAppDisplay = "+52 55 9999 0000";

    Assert.Throws<PublicWebsitePresentationException>(() =>
      PublicWebsitePresentationPolicy.Create(options, Instance(), "hero-image"));
  }

  [Fact]
  public void EnsureAssetsExist_FailsClosedWhenAConfiguredAssetIsMissing()
  {
    var profile = PublicWebsitePresentationPolicy.Create(Options(), Instance(), "hero-image");
    var webRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(webRoot);
    try
    {
      Assert.Throws<PublicWebsitePresentationException>(() =>
        PublicWebsitePresentationPolicy.EnsureAssetsExist(profile, webRoot));
    }
    finally
    {
      Directory.Delete(webRoot, recursive: true);
    }
  }

  private static PublicWebsiteInstanceDefinition Instance()
    => new(
      "site-one",
      "AAA010101AAA",
      "main-site",
      PlatformModuleCodes.Hospitality,
      "stay.example.com",
      5030);

  private static PublicWebsitePresentationOptions Options()
    => new()
    {
      PublicSiteKey = " SITE-ONE ",
      BrandingVersion = 1,
      ContentVersion = 1,
      PublicName = "Estancias Uno",
      ShortName = "Uno",
      LegalName = "Estancias Uno, S.A. de C.V.",
      LocationName = "Ciudad de México",
      Tagline = "Descansa bien.",
      FooterSummary = "Estancias listas para reservar.",
      SeoDescription = "Reserva estancias en línea.",
      Locale = "es-mx",
      PublicEmail = "reservas@example.com",
      WhatsAppE164 = "+525512345678",
      WhatsAppDisplay = "+52 55 1234 5678",
      OperatingAddress = "Dirección operativa",
      FiscalAddress = "Dirección fiscal",
      PrivacyEmail = "privacidad@example.com",
      PrivacyVersion = "2026-09-02",
      PrivacyUpdatedDisplay = "2 de septiembre de 2026",
      TermsVersion = "2026-09-02",
      TermsUpdatedDisplay = "2 de septiembre de 2026",
      PrimaryColor = "#123abc",
      PrimaryDarkColor = "#101820",
      AccentColor = "#F0AA00",
      Assets = new()
      {
        ["logo"] = "/instances/site-one/logo.svg",
        ["favicon"] = "/instances/site-one/favicon.png",
        ["hero-image"] = "/instances/site-one/hero.webp"
      }
    };

  private static PublicSiteBinding Binding()
    => new(
      1,
      "site-one",
      2,
      "AAA010101AAA",
      "AAA010101AAA",
      null,
      3,
      "main-site",
      "Sede principal",
      "Central Standard Time (Mexico)",
      PlatformModuleCodes.Hospitality,
      1,
      "stay.example.com",
      1,
      1,
      1);
}
