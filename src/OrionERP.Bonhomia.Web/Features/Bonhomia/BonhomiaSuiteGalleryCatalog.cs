using OrionERP.Application.Features.Bonhomia.PublicBooking;

namespace OrionERP.Bonhomia.Web.Features.Bonhomia;

public static class BonhomiaSuiteGalleryCatalog
{
  public static IReadOnlyList<BonhomiaSuiteGallery> GetSuites(HospitalityWebsiteDefinition website)
    => website.Rooms.Select(ToGallery).ToArray();

  public static IReadOnlyList<BonhomiaGalleryImage> GetBuildingImages(
    HospitalityWebsiteDefinition website,
    string publicName)
    => website.BuildingGalleryImages
      .Select((source, index) => new BonhomiaGalleryImage(
        source,
        $"{publicName}, edificio, imagen {index + 1}"))
      .ToArray();

  public static BonhomiaSuiteGallery? FindSuite(
    HospitalityWebsiteDefinition website,
    string? suiteName)
  {
    var room = website.FindRoom(suiteName);
    return room is null ? null : ToGallery(room);
  }

  private static BonhomiaSuiteGallery ToGallery(HospitalityRoomPresentation room)
    => new(
      room.RoomCode,
      room.RoomCode.ToLowerInvariant().Replace(' ', '-'),
      room.Aliases,
      room.GalleryImages
        .Select((source, index) => new BonhomiaGalleryImage(
          source,
          $"{room.RoomCode}, imagen {index + 1}"))
        .ToArray());
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
