using OrionERP.Bonhomia.Web.Features.Bonhomia;

namespace OrionERP.UnitTests.Bonhomia;

public class BonhomiaSuiteGalleryCatalogTests
{
  [Theory]
  [InlineData("Casa Berlin", "/Images/Bonhomia/suites/berlin/01.jpg", 7)]
  [InlineData("Berlin", "/Images/Bonhomia/suites/berlin/01.jpg", 7)]
  [InlineData("Suite Manhattan", "/Images/Bonhomia/suites/manhattan/01.jpg", 6)]
  [InlineData("Seul", "/Images/Bonhomia/suites/seul/01.jpg", 6)]
  [InlineData("Moscu", "/Images/Bonhomia/suites/moscu/01.jpg", 6)]
  [InlineData("Paris", "/Images/Bonhomia/suites/paris/01.jpg", 6)]
  [InlineData("Penthouse", "/Images/Bonhomia/suites/penthouse/01.jpg", 6)]
  [InlineData("Grecia", "/Images/Bonhomia/suites/grecia/01.jpg", 6)]
  [InlineData("London", "/Images/Bonhomia/suites/london/01.jpg", 6)]
  public void FindSuite_MapsAliasesToOrderedGallery(string suiteName, string expectedPrimaryImage, int expectedImageCount)
  {
    var gallery = BonhomiaSuiteGalleryCatalog.FindSuite(suiteName);

    Assert.NotNull(gallery);
    Assert.Equal(expectedPrimaryImage, gallery.PrimaryImage);
    Assert.Equal(expectedImageCount, gallery.Images.Count);
    Assert.Contains(gallery.Images, image => image.Source.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
  }

  [Fact]
  public void BuildingImages_UseBuildingStaticAssetFolder()
  {
    Assert.Equal(5, BonhomiaSuiteGalleryCatalog.BuildingImages.Count);
    Assert.All(
      BonhomiaSuiteGalleryCatalog.BuildingImages,
      image => Assert.StartsWith("/Images/Bonhomia/building/", image.Source, StringComparison.Ordinal));
  }
}
