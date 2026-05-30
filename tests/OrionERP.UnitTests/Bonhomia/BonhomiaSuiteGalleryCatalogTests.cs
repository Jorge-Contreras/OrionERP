using OrionERP.Bonhomia.Web.Features.Bonhomia;

namespace OrionERP.UnitTests.Bonhomia;

public class BonhomiaSuiteGalleryCatalogTests
{
  [Theory]
  [InlineData("Casa Berlin", "/Images/Bonhomia/suites/berlin/01.jpg")]
  [InlineData("Berlin", "/Images/Bonhomia/suites/berlin/01.jpg")]
  [InlineData("Suite Manhattan", "/Images/Bonhomia/suites/manhattan/01.jpg")]
  [InlineData("Seul", "/Images/Bonhomia/suites/seul/01.jpg")]
  [InlineData("Moscu", "/Images/Bonhomia/suites/moscu/01.jpg")]
  [InlineData("Paris", "/Images/Bonhomia/suites/paris/01.jpg")]
  [InlineData("Penthouse", "/Images/Bonhomia/suites/penthouse/01.jpg")]
  [InlineData("Grecia", "/Images/Bonhomia/suites/grecia/01.jpg")]
  [InlineData("London", "/Images/Bonhomia/suites/london/01.jpg")]
  public void FindSuite_MapsAliasesToOrderedGallery(string suiteName, string expectedPrimaryImage)
  {
    var gallery = BonhomiaSuiteGalleryCatalog.FindSuite(suiteName);

    Assert.NotNull(gallery);
    Assert.Equal(expectedPrimaryImage, gallery.PrimaryImage);
    Assert.Equal(5, gallery.Images.Count);
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
