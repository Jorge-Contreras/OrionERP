using OrionERP.Application.Features.Bonhomia.PublicBooking;
using OrionERP.Application.Features.Platform;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Bonhomia;

public class BonhomiaSiteContentTests
{
  [Fact]
  public void Create_UsesVersionedPresentationAsIdentityAndLegalAuthority()
  {
    var presentation = HospitalityPresentationTestData.CreatePresentation();
    var website = HospitalityWebsitePolicy.Create(
      HospitalityPresentationTestData.CreateHospitalityOptions(),
      presentation);

    Assert.Equal("Prueba Hospedaje", presentation.PublicName);
    Assert.Equal("Operadora de Prueba, S.A. de C.V.", presentation.LegalName);
    Assert.Equal("privacidad@example.test", presentation.PrivacyEmail);
    Assert.Equal(3, presentation.BrandingVersion);
    Assert.Equal(7, presentation.ContentVersion);
    Assert.Equal("Portal de reservaciones", website.ReservationSourceLabel);
    Assert.Equal("cuenta-prueba", website.AccountingAccount);
    var extra = Assert.Single(website.FeaturedExtras);
    Assert.Equal("late-checkout", extra.Code);
    Assert.Equal(["CHECK-OUT TARDIO", "LATE CHECK-OUT"], extra.Aliases);
    Assert.Equal(1, extra.MaxQuantity);
    Assert.Equal("bi bi-clock-history", extra.Icon);
  }

  [Fact]
  public void Create_RejectsRoomPresentationThatReferencesMissingAsset()
  {
    var options = HospitalityPresentationTestData.CreateHospitalityOptions();
    options.Rooms[0].PrimaryAssetKey = "missing-room-image";

    var exception = Assert.Throws<PublicWebsitePresentationException>(() =>
      HospitalityWebsitePolicy.Create(options, HospitalityPresentationTestData.CreatePresentation()));

    Assert.Contains("missing-room-image", exception.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Create_RejectsDuplicateOperationalExtraAliases()
  {
    var options = HospitalityPresentationTestData.CreateHospitalityOptions();
    options.FeaturedExtras.Add(new()
    {
      Code = "another-extra",
      Aliases = ["late check-out"],
      Name = "Otro extra",
      Detail = "Prueba",
      MaxQuantity = 1,
      Icon = "bi bi-plus"
    });

    var exception = Assert.Throws<PublicWebsitePresentationException>(() =>
      HospitalityWebsitePolicy.Create(options, HospitalityPresentationTestData.CreatePresentation()));

    Assert.Contains("alias", exception.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void LegalAcknowledgements_AreBoundToBothPrivacyAndTermsVersions()
  {
    var layout = RepoFile.Read("src/OrionERP.Bonhomia.Web/Features/Bonhomia/BonhomiaPublicLayout.razor");
    var checkout = RepoFile.Read("src/OrionERP.Bonhomia.Web/Features/Bonhomia/BonhomiaReservationPage.razor");

    Assert.Contains("Presentation.PrivacyVersion}|{Presentation.TermsVersion", layout, StringComparison.Ordinal);
    Assert.Contains("stored.PrivacyVersion, Presentation.PrivacyVersion", checkout, StringComparison.Ordinal);
    Assert.Contains("PrivacyVersion = Presentation.PrivacyVersion", checkout, StringComparison.Ordinal);
  }

  [Fact]
  public void PublicExtras_UseTheScopedCatalogPriceAndRejectAmbiguousAliases()
  {
    var page = RepoFile.Read("src/OrionERP.Bonhomia.Web/Features/Bonhomia/BonhomiaServicesPage.razor");
    var reader = RepoFile.Read("src/OrionERP.Infrastructure/Features/Bonhomia/PublicBooking/BonhomiaScopedPublicDataReader.cs");

    Assert.Contains("HospitalityData.GetExtraOptionsAsync", page, StringComparison.Ordinal);
    Assert.Contains("extra.UnitPrice", page, StringComparison.Ordinal);
    Assert.DoesNotContain("PriceLabel", page, StringComparison.Ordinal);
    Assert.Contains("matches.Length == 0", reader, StringComparison.Ordinal);
    Assert.Contains("matches.Length > 1", reader, StringComparison.Ordinal);
  }
}

internal static class HospitalityPresentationTestData
{
  public static PublicWebsitePresentationDefinition CreatePresentation()
  {
    var instance = PublicWebsiteInstancePolicy.Create(
      new PublicWebsiteInstanceOptions
      {
        PublicSiteKey = "hospitality-test",
        ExpectedCompanyRfc = "AAA010101AAA",
        SiteKey = "test-site",
        ModuleCode = PlatformModuleCodes.Hospitality,
        CanonicalHost = "hospitality.example.test",
        LoopbackPort = 5010
      },
      PlatformModuleCodes.Hospitality);

    return PublicWebsitePresentationPolicy.Create(
      new PublicWebsitePresentationOptions
      {
        PublicSiteKey = instance.PublicSiteKey,
        BrandingVersion = 3,
        ContentVersion = 7,
        PublicName = "Prueba Hospedaje",
        ShortName = "Prueba",
        LegalName = "Operadora de Prueba, S.A. de C.V.",
        LocationName = "Ciudad de Prueba",
        Tagline = "Descansa bien.",
        FooterSummary = "Hospedaje de prueba.",
        SeoDescription = "Hospedaje de prueba para validar un perfil reutilizable.",
        Locale = "es-MX",
        PublicEmail = "reservas@example.test",
        WhatsAppE164 = "+525500000001",
        WhatsAppDisplay = "+52 55 0000 0001",
        OperatingAddress = "Domicilio operativo de prueba",
        FiscalAddress = "Domicilio fiscal de prueba",
        PrivacyEmail = "privacidad@example.test",
        PrivacyVersion = "2026-01-01",
        PrivacyUpdatedDisplay = "1 de enero de 2026",
        TermsVersion = "2026-02-01",
        TermsUpdatedDisplay = "1 de febrero de 2026",
        PrimaryColor = "#123456",
        PrimaryDarkColor = "#102030",
        AccentColor = "#ABCDEF",
        Assets = new Dictionary<string, string>
        {
          ["logo"] = "/assets/logo.svg",
          ["favicon"] = "/assets/favicon.png",
          ["home"] = "/assets/home.jpg",
          ["building"] = "/assets/building.jpg",
          ["room"] = "/assets/room.jpg",
          ["room-two"] = "/assets/room-two.jpg"
        }
      },
      instance);
  }

  public static HospitalityWebsiteOptions CreateHospitalityOptions()
    => new()
    {
      ReservationSourceLabel = "Portal de reservaciones",
      AccountingAccount = "cuenta-prueba",
      PdfFilePrefix = "reserva-prueba",
      CheckInDisplay = "A partir de las 15:00",
      CheckOutDisplay = "Hasta las 11:00",
      LateCheckoutWindowDisplay = "De 12:00 a 14:00",
      LateCheckoutFeeDisplay = "$200 MXN",
      CancellationAdvanceDays = 30,
      RefundPercent = 85,
      LostPropertyRetentionDays = 30,
      HomeGalleryAssetKeys = ["home"],
      BuildingGalleryAssetKeys = ["building"],
      GuestProfiles = [new() { Title = "Trabajo", Text = "Estancia de trabajo", Icon = "bi bi-tools" }],
      Services = [new() { Title = "Internet", Text = "Conexion disponible", Icon = "bi bi-wifi" }],
      FeaturedExtras =
      [
        new()
        {
          Code = "late-checkout",
          Aliases = ["CHECK-OUT TARDIO", "LATE CHECK-OUT"],
          Name = "Salida tardia",
          Detail = "Sujeta a disponibilidad",
          MaxQuantity = 1,
          Icon = "bi bi-clock-history"
        }
      ],
      Faqs = [new() { Category = "Reserva", Question = "Como reservar?", Answer = "Desde el portal." }],
      Rooms =
      [
        new()
        {
          RoomCode = "Suite Uno",
          Aliases = ["Uno"],
          Tag = "Ejecutiva",
          Ideal = "Viajes de trabajo",
          Capacity = 2,
          Bedrooms = 1,
          Bathrooms = 1,
          PrimaryAssetKey = "room",
          GalleryAssetKeys = ["room", "room-two"]
        }
      ]
    };
}
