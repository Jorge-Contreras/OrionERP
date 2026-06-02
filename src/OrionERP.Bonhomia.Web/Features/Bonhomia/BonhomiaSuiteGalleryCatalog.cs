using OrionERP.Application.Features.Reservaciones.OpenClaw;

namespace OrionERP.Bonhomia.Web.Features.Bonhomia;

public static class BonhomiaSuiteGalleryCatalog
{
  private const int ImageCount = 5;
  private const string DefaultSuiteImage = "/Images/Bonhomia/suites/manhattan/01.jpg";

  public static IReadOnlyList<BonhomiaSuiteGallery> Suites { get; } =
  [
    new("Casa Berlin", "berlin", ["Casa Berlin", "Berlin"], CreateSuiteImages("Casa Berlin", "berlin", "CASA BERLIN PLANTA - BAJA-RENDER.png", "CASA BERLIN - PLANTA ALTA-RENDER.png")),
    new("Suite Manhattan", "manhattan", ["Suite Manhattan", "Manhattan"], CreateSuiteImages("Suite Manhattan", "manhattan", "MANHATTAN - RENDER.png")),
    new("Suite Seul", "seul", ["Suite Seul", "Seul"], CreateSuiteImages("Suite Seul", "seul", "SEUL - RENDER.png")),
    new("Suite Moscu", "moscu", ["Suite Moscu", "Moscu"], CreateSuiteImages("Suite Moscu", "moscu", "MOSCU - RENDER.png")),
    new("Suite Paris", "paris", ["Suite Paris", "Paris"], CreateSuiteImages("Suite Paris", "paris", "PARIS - RENDER.png")),
    new("Penthouse", "penthouse", ["Penthouse"], CreateSuiteImages("Penthouse", "penthouse", "PENTHOUSE - RENDER.png")),
    new("Casa Grecia", "grecia", ["Casa Grecia", "Grecia"], CreateSuiteImages("Casa Grecia", "grecia", "Grecia_Render.png")),
    new("Casa London", "london", ["Casa London", "London"], CreateSuiteImages("Casa London", "london", "London Render.png"))
  ];

  public static IReadOnlyList<BonhomiaGalleryImage> BuildingImages { get; }
    = CreateImages("Edificio Bonhomia", "/Images/Bonhomia/building");

  private static readonly IReadOnlyDictionary<string, BonhomiaSuiteGallery> ByAlias = BuildAliasIndex();

  public static BonhomiaSuiteGallery? FindSuite(string? suiteName)
  {
    var key = OpenClawReservationNaming.NormalizeLookupKey(suiteName);
    return ByAlias.TryGetValue(key, out var gallery) ? gallery : null;
  }

  public static string GetPrimaryImageForSuite(string suiteName)
    => FindSuite(suiteName)?.PrimaryImage ?? DefaultSuiteImage;

  private static IReadOnlyDictionary<string, BonhomiaSuiteGallery> BuildAliasIndex()
  {
    var index = new Dictionary<string, BonhomiaSuiteGallery>(StringComparer.Ordinal);
    foreach (var suite in Suites)
    {
      index[OpenClawReservationNaming.NormalizeLookupKey(suite.Name)] = suite;
      foreach (var alias in suite.Aliases)
      {
        index[OpenClawReservationNaming.NormalizeLookupKey(alias)] = suite;
      }
    }

    return index;
  }

  private static IReadOnlyList<BonhomiaGalleryImage> CreateSuiteImages(string suiteName, string slug, params string[] extraFileNames)
  {
    var basePath = $"/Images/Bonhomia/suites/{slug}";
    var images = CreateImages(suiteName, basePath).ToList();
    for (var index = 0; index < extraFileNames.Length; index++)
    {
      images.Add(new BonhomiaGalleryImage(
        $"{basePath}/{Uri.EscapeDataString(extraFileNames[index])}",
        $"{suiteName} render {index + 1}"));
    }

    return images;
  }

  private static IReadOnlyList<BonhomiaGalleryImage> CreateImages(string subject, string basePath)
  {
    var images = new BonhomiaGalleryImage[ImageCount];
    for (var index = 1; index <= ImageCount; index++)
    {
      images[index - 1] = new BonhomiaGalleryImage(
        $"{basePath}/{index:D2}.jpg",
        $"{subject} imagen {index}");
    }

    return images;
  }
}

public sealed record BonhomiaSuiteGallery(
  string Name,
  string Slug,
  IReadOnlyList<string> Aliases,
  IReadOnlyList<BonhomiaGalleryImage> Images)
{
  public string PrimaryImage => Images.Count > 0 ? Images[0].Source : string.Empty;
}

public sealed record BonhomiaGalleryImage(string Source, string Alt);
