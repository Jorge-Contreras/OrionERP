using System.Collections.ObjectModel;
using OrionERP.Application.Features.Platform;
using OrionERP.Application.Features.Reservaciones;

namespace OrionERP.Application.Features.Bonhomia.PublicBooking;

/// <summary>
/// Non-secret, hospitality-specific presentation layered on top of the common
/// public website presentation. Operational room prices and availability stay
/// in the scoped reservation catalog; this model only describes how they are
/// presented to guests.
/// </summary>
public sealed class HospitalityWebsiteOptions
{
  public const string SectionName = "HospitalityWebsite";

  public string ReservationSourceLabel { get; set; } = string.Empty;
  public string AccountingAccount { get; set; } = string.Empty;
  public string PdfFilePrefix { get; set; } = string.Empty;
  public string CheckInDisplay { get; set; } = string.Empty;
  public string CheckOutDisplay { get; set; } = string.Empty;
  public string LateCheckoutWindowDisplay { get; set; } = string.Empty;
  public string LateCheckoutFeeDisplay { get; set; } = string.Empty;
  public int CancellationAdvanceDays { get; set; }
  public decimal RefundPercent { get; set; }
  public int LostPropertyRetentionDays { get; set; }
  public List<string> HomeGalleryAssetKeys { get; set; } = [];
  public List<string> BuildingGalleryAssetKeys { get; set; } = [];
  public List<HospitalityContentCardOptions> GuestProfiles { get; set; } = [];
  public List<HospitalityContentCardOptions> Services { get; set; } = [];
  public List<HospitalityFeaturedExtraOptions> FeaturedExtras { get; set; } = [];
  public List<HospitalityFaqOptions> Faqs { get; set; } = [];
  public List<HospitalityRoomPresentationOptions> Rooms { get; set; } = [];
}

public sealed class HospitalityContentCardOptions
{
  public string Title { get; set; } = string.Empty;
  public string Text { get; set; } = string.Empty;
  public string Icon { get; set; } = string.Empty;
}

public sealed class HospitalityFeaturedExtraOptions
{
  public string Code { get; set; } = string.Empty;
  public List<string> Aliases { get; set; } = [];
  public string Name { get; set; } = string.Empty;
  public string Detail { get; set; } = string.Empty;
  public int MaxQuantity { get; set; } = 1;
  public string Icon { get; set; } = string.Empty;
}

public sealed class HospitalityFaqOptions
{
  public string Category { get; set; } = string.Empty;
  public string Question { get; set; } = string.Empty;
  public string Answer { get; set; } = string.Empty;
}

public sealed class HospitalityRoomPresentationOptions
{
  public string RoomCode { get; set; } = string.Empty;
  public List<string> Aliases { get; set; } = [];
  public string Tag { get; set; } = string.Empty;
  public string Ideal { get; set; } = string.Empty;
  public int Capacity { get; set; }
  public int Bedrooms { get; set; }
  public decimal Bathrooms { get; set; }
  public string PrimaryAssetKey { get; set; } = string.Empty;
  public List<string> GalleryAssetKeys { get; set; } = [];
}

public sealed record HospitalityContentCard(string Title, string Text, string Icon);

public sealed record HospitalityFeaturedExtra(
  string Code,
  IReadOnlyList<string> Aliases,
  string Name,
  string Detail,
  int MaxQuantity,
  string Icon);

public sealed record HospitalityFaq(string Category, string Question, string Answer);

public sealed record HospitalityRoomPresentation(
  string RoomCode,
  IReadOnlyList<string> Aliases,
  string Tag,
  string Ideal,
  int Capacity,
  int Bedrooms,
  decimal Bathrooms,
  string PrimaryImage,
  IReadOnlyList<string> GalleryImages);

public sealed class HospitalityWebsiteDefinition
{
  private readonly IReadOnlyDictionary<string, HospitalityRoomPresentation> _roomsByAlias;

  internal HospitalityWebsiteDefinition(
    PublicWebsitePresentationDefinition presentation,
    string reservationSourceLabel,
    string accountingAccount,
    string pdfFilePrefix,
    string checkInDisplay,
    string checkOutDisplay,
    string lateCheckoutWindowDisplay,
    string lateCheckoutFeeDisplay,
    int cancellationAdvanceDays,
    decimal refundPercent,
    int lostPropertyRetentionDays,
    IReadOnlyList<string> homeGalleryImages,
    IReadOnlyList<string> buildingGalleryImages,
    IReadOnlyList<HospitalityContentCard> guestProfiles,
    IReadOnlyList<HospitalityContentCard> services,
    IReadOnlyList<HospitalityFeaturedExtra> featuredExtras,
    IReadOnlyList<HospitalityFaq> faqs,
    IReadOnlyList<HospitalityRoomPresentation> rooms,
    IReadOnlyDictionary<string, HospitalityRoomPresentation> roomsByAlias)
  {
    Presentation = presentation;
    ReservationSourceLabel = reservationSourceLabel;
    AccountingAccount = accountingAccount;
    PdfFilePrefix = pdfFilePrefix;
    CheckInDisplay = checkInDisplay;
    CheckOutDisplay = checkOutDisplay;
    LateCheckoutWindowDisplay = lateCheckoutWindowDisplay;
    LateCheckoutFeeDisplay = lateCheckoutFeeDisplay;
    CancellationAdvanceDays = cancellationAdvanceDays;
    RefundPercent = refundPercent;
    LostPropertyRetentionDays = lostPropertyRetentionDays;
    HomeGalleryImages = homeGalleryImages;
    BuildingGalleryImages = buildingGalleryImages;
    GuestProfiles = guestProfiles;
    Services = services;
    FeaturedExtras = featuredExtras;
    Faqs = faqs;
    Rooms = rooms;
    _roomsByAlias = roomsByAlias;
  }

  public PublicWebsitePresentationDefinition Presentation { get; }
  public string ReservationSourceLabel { get; }
  public string AccountingAccount { get; }
  public string PdfFilePrefix { get; }
  public string CheckInDisplay { get; }
  public string CheckOutDisplay { get; }
  public string LateCheckoutWindowDisplay { get; }
  public string LateCheckoutFeeDisplay { get; }
  public int CancellationAdvanceDays { get; }
  public decimal RefundPercent { get; }
  public int LostPropertyRetentionDays { get; }
  public IReadOnlyList<string> HomeGalleryImages { get; }
  public IReadOnlyList<string> BuildingGalleryImages { get; }
  public IReadOnlyList<HospitalityContentCard> GuestProfiles { get; }
  public IReadOnlyList<HospitalityContentCard> Services { get; }
  public IReadOnlyList<HospitalityFeaturedExtra> FeaturedExtras { get; }
  public IReadOnlyList<HospitalityFaq> Faqs { get; }
  public IReadOnlyList<HospitalityRoomPresentation> Rooms { get; }

  public HospitalityRoomPresentation? FindRoom(string? roomName)
  {
    var key = ReservationCatalogNaming.NormalizeLookupKey(roomName);
    return _roomsByAlias.TryGetValue(key, out var room) ? room : null;
  }
}

public static class HospitalityWebsitePolicy
{
  public static HospitalityWebsiteDefinition Create(
    HospitalityWebsiteOptions options,
    PublicWebsitePresentationDefinition presentation)
  {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(presentation);

    if (options.CancellationAdvanceDays is < 1 or > 365)
      throw Invalid("CancellationAdvanceDays must be between 1 and 365.");
    if (options.RefundPercent is < 0m or > 100m)
      throw Invalid("RefundPercent must be between 0 and 100.");
    if (options.LostPropertyRetentionDays is < 1 or > 365)
      throw Invalid("LostPropertyRetentionDays must be between 1 and 365.");

    var homeGallery = ResolveAssetList(options.HomeGalleryAssetKeys, presentation, "HomeGalleryAssetKeys", 1);
    var buildingGallery = ResolveAssetList(options.BuildingGalleryAssetKeys, presentation, "BuildingGalleryAssetKeys", 1);
    var guestProfiles = NormalizeCards(options.GuestProfiles, "GuestProfiles");
    var services = NormalizeCards(options.Services, "Services");
    var featuredExtras = NormalizeFeaturedExtras(options.FeaturedExtras);
    var faqs = NormalizeFaqs(options.Faqs);
    var rooms = NormalizeRooms(options.Rooms, presentation, out var roomsByAlias);

    return new HospitalityWebsiteDefinition(
      presentation,
      RequiredText(options.ReservationSourceLabel, nameof(options.ReservationSourceLabel), 100),
      RequiredText(options.AccountingAccount, nameof(options.AccountingAccount), 200),
      NormalizeSlug(options.PdfFilePrefix, nameof(options.PdfFilePrefix)),
      RequiredText(options.CheckInDisplay, nameof(options.CheckInDisplay), 80),
      RequiredText(options.CheckOutDisplay, nameof(options.CheckOutDisplay), 80),
      RequiredText(options.LateCheckoutWindowDisplay, nameof(options.LateCheckoutWindowDisplay), 100),
      RequiredText(options.LateCheckoutFeeDisplay, nameof(options.LateCheckoutFeeDisplay), 80),
      options.CancellationAdvanceDays,
      options.RefundPercent,
      options.LostPropertyRetentionDays,
      homeGallery,
      buildingGallery,
      guestProfiles,
      services,
      featuredExtras,
      faqs,
      rooms,
      roomsByAlias);
  }

  private static IReadOnlyList<HospitalityRoomPresentation> NormalizeRooms(
    IReadOnlyList<HospitalityRoomPresentationOptions>? values,
    PublicWebsitePresentationDefinition presentation,
    out IReadOnlyDictionary<string, HospitalityRoomPresentation> roomsByAlias)
  {
    if (values is not { Count: > 0 })
      throw Invalid("Rooms must contain at least one configured room presentation.");

    var rooms = new List<HospitalityRoomPresentation>(values.Count);
    var aliases = new Dictionary<string, HospitalityRoomPresentation>(StringComparer.Ordinal);
    foreach (var value in values)
    {
      if (value is null)
        throw Invalid("Rooms cannot contain null items.");
      if (value.Capacity is < 1 or > 100)
        throw Invalid("Every room Capacity must be between 1 and 100.");
      if (value.Bedrooms is < 0 or > 100)
        throw Invalid("Every room Bedrooms must be between 0 and 100.");
      if (value.Bathrooms is < 0m or > 100m)
        throw Invalid("Every room Bathrooms must be between 0 and 100.");

      var roomCode = RequiredText(value.RoomCode, nameof(value.RoomCode), 160);
      var roomAliases = (value.Aliases ?? [])
        .Select(alias => RequiredText(alias, nameof(value.Aliases), 160))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
      var primaryImage = presentation.Asset(value.PrimaryAssetKey);
      var galleryImages = ResolveAssetList(value.GalleryAssetKeys, presentation, $"Rooms:{roomCode}:GalleryAssetKeys", 1);
      if (!galleryImages.Contains(primaryImage, StringComparer.Ordinal))
        galleryImages = [primaryImage, .. galleryImages];

      var room = new HospitalityRoomPresentation(
        roomCode,
        roomAliases,
        RequiredText(value.Tag, nameof(value.Tag), 100),
        RequiredText(value.Ideal, nameof(value.Ideal), 500),
        value.Capacity,
        value.Bedrooms,
        value.Bathrooms,
        primaryImage,
        galleryImages);
      rooms.Add(room);

      foreach (var alias in roomAliases.Prepend(roomCode))
      {
        var normalized = ReservationCatalogNaming.NormalizeLookupKey(alias);
        if (normalized.Length == 0 || !aliases.TryAdd(normalized, room))
          throw Invalid($"Room code or alias '{alias}' is empty or duplicated.");
      }
    }

    roomsByAlias = new ReadOnlyDictionary<string, HospitalityRoomPresentation>(aliases);
    return rooms.AsReadOnly();
  }

  private static IReadOnlyList<HospitalityContentCard> NormalizeCards(
    IReadOnlyList<HospitalityContentCardOptions>? values,
    string field)
  {
    if (values is not { Count: > 0 })
      throw Invalid($"{field} must contain at least one item.");
    return values.Select(value =>
    {
      if (value is null)
        throw Invalid($"{field} cannot contain null items.");
      return new HospitalityContentCard(
        RequiredText(value.Title, $"{field}:Title", 160),
        RequiredText(value.Text, $"{field}:Text", 500),
        NormalizeIcon(value.Icon, $"{field}:Icon"));
    }).ToArray();
  }

  private static IReadOnlyList<HospitalityFeaturedExtra> NormalizeFeaturedExtras(
    IReadOnlyList<HospitalityFeaturedExtraOptions>? values)
  {
    if (values is not { Count: > 0 })
      throw Invalid("FeaturedExtras must contain at least one item.");
    var extras = new List<HospitalityFeaturedExtra>(values.Count);
    var codes = new HashSet<string>(StringComparer.Ordinal);
    var aliases = new HashSet<string>(StringComparer.Ordinal);
    foreach (var value in values)
    {
      if (value is null)
        throw Invalid("FeaturedExtras cannot contain null items.");

      var code = NormalizeSlug(value.Code, "FeaturedExtras:Code");
      if (!codes.Add(code))
        throw Invalid($"FeaturedExtras code '{code}' is duplicated.");
      if (value.MaxQuantity is < 1 or > 1000)
        throw Invalid("Every FeaturedExtras MaxQuantity must be between 1 and 1000.");

      var configuredAliases = (value.Aliases ?? [])
        .Select(alias => RequiredText(alias, "FeaturedExtras:Aliases", 160))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
      if (configuredAliases.Length == 0)
        throw Invalid($"FeaturedExtras '{code}' must contain at least one catalog alias.");
      foreach (var alias in configuredAliases)
      {
        var normalizedAlias = ReservationCatalogNaming.NormalizeLookupKey(alias);
        if (normalizedAlias.Length == 0 || !aliases.Add(normalizedAlias))
          throw Invalid($"FeaturedExtras catalog alias '{alias}' is empty or duplicated.");
      }

      extras.Add(new HospitalityFeaturedExtra(
        code,
        configuredAliases,
        RequiredText(value.Name, "FeaturedExtras:Name", 160),
        RequiredText(value.Detail, "FeaturedExtras:Detail", 300),
        value.MaxQuantity,
        NormalizeIcon(value.Icon, "FeaturedExtras:Icon")));
    }

    return extras.AsReadOnly();
  }

  private static string NormalizeIcon(string? value, string field)
  {
    var icon = RequiredText(value, field, 80);
    if (!icon.StartsWith("bi bi-", StringComparison.Ordinal)
        || icon.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ' ' and not '-'))
      throw Invalid($"{field} must be one Bootstrap icon class.");
    return icon;
  }

  private static IReadOnlyList<HospitalityFaq> NormalizeFaqs(IReadOnlyList<HospitalityFaqOptions>? values)
  {
    if (values is not { Count: > 0 })
      throw Invalid("Faqs must contain at least one item.");
    return values.Select(value =>
    {
      if (value is null)
        throw Invalid("Faqs cannot contain null items.");
      return new HospitalityFaq(
        RequiredText(value.Category, "Faqs:Category", 100),
        RequiredText(value.Question, "Faqs:Question", 300),
        RequiredText(value.Answer, "Faqs:Answer", 1000));
    }).ToArray();
  }

  private static IReadOnlyList<string> ResolveAssetList(
    IReadOnlyList<string>? keys,
    PublicWebsitePresentationDefinition presentation,
    string field,
    int minimum)
  {
    var normalizedKeys = (keys ?? [])
      .Select(PublicWebsitePresentationPolicy.NormalizeAssetKey)
      .Distinct(StringComparer.Ordinal)
      .ToArray();
    if (normalizedKeys.Length < minimum)
      throw Invalid($"{field} must contain at least {minimum} unique asset key(s).");
    return normalizedKeys.Select(presentation.Asset).ToArray();
  }

  private static string NormalizeSlug(string? value, string field)
  {
    var normalized = RequiredText(value, field, 80).ToLowerInvariant();
    if (normalized[0] == '-' || normalized[^1] == '-'
        || normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
      throw Invalid($"{field} must use letters, numbers and internal hyphens only.");
    return normalized;
  }

  private static string RequiredText(string? value, string field, int maximum)
  {
    var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    if (normalized is null)
      throw Invalid($"{field} is required.");
    if (normalized.Length > maximum || normalized.Any(char.IsControl))
      throw Invalid($"{field} cannot exceed {maximum} characters or contain control characters.");
    return normalized;
  }

  private static PublicWebsitePresentationException Invalid(string message)
    => new($"Invalid {HospitalityWebsiteOptions.SectionName} configuration: {message}");
}
