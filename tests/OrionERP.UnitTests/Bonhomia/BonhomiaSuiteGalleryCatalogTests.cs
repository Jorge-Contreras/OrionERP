using OrionERP.Application.Features.Bonhomia.PublicBooking;
using OrionERP.Bonhomia.Web.Features.Bonhomia;

namespace OrionERP.UnitTests.Bonhomia;

public class BonhomiaSuiteGalleryCatalogTests
{
  [Theory]
  [InlineData("Suite Uno")]
  [InlineData("Uno")]
  [InlineData("suite-uno")]
  public void FindSuite_MapsAliasesToConfiguredOrderedGallery(string suiteName)
  {
    var website = HospitalityWebsitePolicy.Create(
      HospitalityPresentationTestData.CreateHospitalityOptions(),
      HospitalityPresentationTestData.CreatePresentation());
    var gallery = BonhomiaSuiteGalleryCatalog.FindSuite(website, suiteName);

    Assert.NotNull(gallery);
    Assert.Equal("/assets/room.jpg", gallery.PrimaryImage);
    Assert.Equal(["/assets/room.jpg", "/assets/room-two.jpg"], gallery.Images.Select(image => image.Source));
  }

  [Fact]
  public void BuildingImages_UseConfiguredPresentationAssets()
  {
    var website = HospitalityWebsitePolicy.Create(
      HospitalityPresentationTestData.CreateHospitalityOptions(),
      HospitalityPresentationTestData.CreatePresentation());
    var images = BonhomiaSuiteGalleryCatalog.GetBuildingImages(website, "Marca de Prueba");

    var image = Assert.Single(images);
    Assert.Equal("/assets/building.jpg", image.Source);
    Assert.Contains("Marca de Prueba", image.Alt, StringComparison.Ordinal);
  }
}
