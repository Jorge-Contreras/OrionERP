using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Mail;

namespace OrionERP.Application.Features.Platform;

/// <summary>
/// Versioned, non-secret presentation bundle for one public website process.
/// A deployment must explicitly bind this bundle to the same PublicSiteKey as
/// its trusted <see cref="PublicWebsiteInstanceOptions"/> configuration.
/// </summary>
public sealed class PublicWebsitePresentationOptions
{
  public const string SectionName = "PublicWebsitePresentation";

  public string PublicSiteKey { get; set; } = string.Empty;
  public long BrandingVersion { get; set; }
  public long ContentVersion { get; set; }
  public string PublicName { get; set; } = string.Empty;
  public string ShortName { get; set; } = string.Empty;
  public string? MembershipProgramName { get; set; }
  public string LegalName { get; set; } = string.Empty;
  public string LocationName { get; set; } = string.Empty;
  public string Tagline { get; set; } = string.Empty;
  public string FooterSummary { get; set; } = string.Empty;
  public string SeoDescription { get; set; } = string.Empty;
  public string Locale { get; set; } = "es-MX";
  public string PublicEmail { get; set; } = string.Empty;
  public string? PhoneE164 { get; set; }
  public string? PhoneDisplay { get; set; }
  public string WhatsAppE164 { get; set; } = string.Empty;
  public string WhatsAppDisplay { get; set; } = string.Empty;
  public string OperatingAddress { get; set; } = string.Empty;
  public string FiscalAddress { get; set; } = string.Empty;
  public string PrivacyEmail { get; set; } = string.Empty;
  public string PrivacyVersion { get; set; } = string.Empty;
  public string PrivacyUpdatedDisplay { get; set; } = string.Empty;
  public string TermsVersion { get; set; } = string.Empty;
  public string TermsUpdatedDisplay { get; set; } = string.Empty;
  public string PrimaryColor { get; set; } = string.Empty;
  public string PrimaryDarkColor { get; set; } = string.Empty;
  public string AccentColor { get; set; } = string.Empty;
  public Dictionary<string, string> Assets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PublicWebsitePresentationDefinition
{
  internal PublicWebsitePresentationDefinition(
    string publicSiteKey,
    long brandingVersion,
    long contentVersion,
    string publicName,
    string shortName,
    string? membershipProgramName,
    string legalName,
    string locationName,
    string tagline,
    string footerSummary,
    string seoDescription,
    string locale,
    string publicEmail,
    string? phoneE164,
    string? phoneDisplay,
    string whatsAppE164,
    string whatsAppDisplay,
    string operatingAddress,
    string fiscalAddress,
    string privacyEmail,
    string privacyVersion,
    string privacyUpdatedDisplay,
    string termsVersion,
    string termsUpdatedDisplay,
    string primaryColor,
    string primaryDarkColor,
    string accentColor,
    IReadOnlyDictionary<string, string> assets)
  {
    PublicSiteKey = publicSiteKey;
    BrandingVersion = brandingVersion;
    ContentVersion = contentVersion;
    PublicName = publicName;
    ShortName = shortName;
    MembershipProgramName = membershipProgramName;
    LegalName = legalName;
    LocationName = locationName;
    Tagline = tagline;
    FooterSummary = footerSummary;
    SeoDescription = seoDescription;
    Locale = locale;
    PublicEmail = publicEmail;
    PhoneE164 = phoneE164;
    PhoneDisplay = phoneDisplay;
    WhatsAppE164 = whatsAppE164;
    WhatsAppDisplay = whatsAppDisplay;
    OperatingAddress = operatingAddress;
    FiscalAddress = fiscalAddress;
    PrivacyEmail = privacyEmail;
    PrivacyVersion = privacyVersion;
    PrivacyUpdatedDisplay = privacyUpdatedDisplay;
    TermsVersion = termsVersion;
    TermsUpdatedDisplay = termsUpdatedDisplay;
    PrimaryColor = primaryColor;
    PrimaryDarkColor = primaryDarkColor;
    AccentColor = accentColor;
    Assets = assets;
  }

  public string PublicSiteKey { get; }
  public long BrandingVersion { get; }
  public long ContentVersion { get; }
  public string PublicName { get; }
  public string ShortName { get; }
  public string? MembershipProgramName { get; }
  public string LegalName { get; }
  public string LocationName { get; }
  public string Tagline { get; }
  public string FooterSummary { get; }
  public string SeoDescription { get; }
  public string Locale { get; }
  public string PublicEmail { get; }
  public string? PhoneE164 { get; }
  public string? PhoneDisplay { get; }
  public string WhatsAppE164 { get; }
  public string WhatsAppDisplay { get; }
  public string OperatingAddress { get; }
  public string FiscalAddress { get; }
  public string PrivacyEmail { get; }
  public string PrivacyVersion { get; }
  public string PrivacyUpdatedDisplay { get; }
  public string TermsVersion { get; }
  public string TermsUpdatedDisplay { get; }
  public string PrimaryColor { get; }
  public string PrimaryDarkColor { get; }
  public string AccentColor { get; }
  public IReadOnlyDictionary<string, string> Assets { get; }

  public string LogoPath => Asset("logo");
  public string FaviconPath => Asset("favicon");

  public string Asset(string key)
  {
    var normalizedKey = PublicWebsitePresentationPolicy.NormalizeAssetKey(key);
    return Assets.TryGetValue(normalizedKey, out var path)
      ? path
      : throw new PublicWebsitePresentationException(
        $"The presentation for PublicSite '{PublicSiteKey}' has no '{normalizedKey}' asset.");
  }

  public string WhatsAppUrl(string message)
    => $"https://wa.me/{WhatsAppE164.TrimStart('+')}?text={Uri.EscapeDataString(message ?? string.Empty)}";

  public string? PhoneUrl
    => PhoneE164 is null ? null : $"tel:{PhoneE164}";
}

public sealed class PublicWebsitePresentationException : InvalidOperationException
{
  public PublicWebsitePresentationException(string message)
    : base(message)
  {
  }
}

public readonly record struct PublicWebsitePresentationBindingMatch(
  bool IsFallback,
  DateTime? ValidUntilUtc)
{
  public static PublicWebsitePresentationBindingMatch Active { get; } = new(false, null);
}

public static class PublicWebsitePresentationPolicy
{
  public static PublicWebsitePresentationDefinition Create(
    PublicWebsitePresentationOptions options,
    PublicWebsiteInstanceDefinition instance,
    params string[] requiredAssetKeys)
  {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(instance);

    string publicSiteKey;
    try
    {
      publicSiteKey = PublicSiteResolutionPolicy.NormalizePublicSiteKey(options.PublicSiteKey);
    }
    catch (ArgumentException exception)
    {
      throw Invalid(exception.Message);
    }

    if (!string.Equals(publicSiteKey, instance.PublicSiteKey, StringComparison.Ordinal))
      throw Invalid("PublicSiteKey does not match the configured public website instance.");
    if (options.BrandingVersion <= 0)
      throw Invalid("BrandingVersion must be greater than zero.");
    if (options.ContentVersion <= 0)
      throw Invalid("ContentVersion must be greater than zero.");

    var phoneE164 = NormalizeOptionalE164(options.PhoneE164, nameof(options.PhoneE164));
    var phoneDisplay = OptionalText(options.PhoneDisplay, nameof(options.PhoneDisplay), 50);
    if ((phoneE164 is null) != (phoneDisplay is null))
      throw Invalid("PhoneE164 and PhoneDisplay must either both be configured or both be empty.");
    if (phoneE164 is not null)
      EnsureDisplayMatchesE164(phoneDisplay!, phoneE164, nameof(options.PhoneDisplay));

    var whatsAppE164 = NormalizeRequiredE164(options.WhatsAppE164, nameof(options.WhatsAppE164));
    var whatsAppDisplay = RequiredText(options.WhatsAppDisplay, nameof(options.WhatsAppDisplay), 50);
    EnsureDisplayMatchesE164(whatsAppDisplay, whatsAppE164, nameof(options.WhatsAppDisplay));

    var assets = NormalizeAssets(options.Assets);
    foreach (var requiredAssetKey in requiredAssetKeys.Append("logo").Append("favicon"))
    {
      var key = NormalizeAssetKey(requiredAssetKey);
      if (!assets.ContainsKey(key))
        throw Invalid($"Assets:{key} is required for this website host.");
    }

    return new PublicWebsitePresentationDefinition(
      publicSiteKey,
      options.BrandingVersion,
      options.ContentVersion,
      RequiredText(options.PublicName, nameof(options.PublicName), 200),
      RequiredText(options.ShortName, nameof(options.ShortName), 100),
      OptionalText(options.MembershipProgramName, nameof(options.MembershipProgramName), 160),
      RequiredText(options.LegalName, nameof(options.LegalName), 300),
      RequiredText(options.LocationName, nameof(options.LocationName), 160),
      RequiredText(options.Tagline, nameof(options.Tagline), 300),
      RequiredText(options.FooterSummary, nameof(options.FooterSummary), 500),
      RequiredText(options.SeoDescription, nameof(options.SeoDescription), 500),
      NormalizeLocale(options.Locale),
      NormalizeEmail(options.PublicEmail, nameof(options.PublicEmail)),
      phoneE164,
      phoneDisplay,
      whatsAppE164,
      whatsAppDisplay,
      RequiredText(options.OperatingAddress, nameof(options.OperatingAddress), 500),
      RequiredText(options.FiscalAddress, nameof(options.FiscalAddress), 500),
      NormalizeEmail(options.PrivacyEmail, nameof(options.PrivacyEmail)),
      RequiredText(options.PrivacyVersion, nameof(options.PrivacyVersion), 30),
      RequiredText(options.PrivacyUpdatedDisplay, nameof(options.PrivacyUpdatedDisplay), 100),
      RequiredText(options.TermsVersion, nameof(options.TermsVersion), 30),
      RequiredText(options.TermsUpdatedDisplay, nameof(options.TermsUpdatedDisplay), 100),
      NormalizeColor(options.PrimaryColor, nameof(options.PrimaryColor)),
      NormalizeColor(options.PrimaryDarkColor, nameof(options.PrimaryDarkColor)),
      NormalizeColor(options.AccentColor, nameof(options.AccentColor)),
      new ReadOnlyDictionary<string, string>(assets));
  }

  public static PublicWebsitePresentationBindingMatch EnsureMatchesBinding(
    PublicWebsitePresentationDefinition presentation,
    PublicSiteBinding binding)
    => EnsureMatchesBinding(presentation, binding, DateTime.UtcNow);

  public static PublicWebsitePresentationBindingMatch EnsureMatchesBinding(
    PublicWebsitePresentationDefinition presentation,
    PublicSiteBinding binding,
    DateTime utcNow)
  {
    ArgumentNullException.ThrowIfNull(presentation);
    ArgumentNullException.ThrowIfNull(binding);

    if (!string.Equals(presentation.PublicSiteKey, binding.PublicSiteKey, StringComparison.Ordinal))
      throw Invalid("The presentation belongs to a different PublicSite.");

    if (presentation.BrandingVersion == binding.BrandingVersion
        && presentation.ContentVersion == binding.ContentVersion)
      return PublicWebsitePresentationBindingMatch.Active;

    var normalizedUtcNow = utcNow.Kind == DateTimeKind.Utc
      ? utcNow
      : utcNow.ToUniversalTime();
    var fallbackUntilUtc = binding.FallbackUntilUtc.HasValue
      ? DateTime.SpecifyKind(binding.FallbackUntilUtc.Value, DateTimeKind.Utc)
      : (DateTime?)null;
    if (binding.FallbackBrandingVersion.HasValue
        && binding.FallbackContentVersion.HasValue
        && fallbackUntilUtc > normalizedUtcNow
        && presentation.BrandingVersion == binding.FallbackBrandingVersion.Value
        && presentation.ContentVersion == binding.FallbackContentVersion.Value)
      return new PublicWebsitePresentationBindingMatch(true, fallbackUntilUtc);

    throw Invalid(
      $"Presentation version mismatch for PublicSite '{binding.PublicSiteKey}': " +
      $"configured ({presentation.BrandingVersion}, {presentation.ContentVersion}), " +
      $"active ({binding.BrandingVersion}, {binding.ContentVersion}).");
  }

  public static void EnsureAssetsExist(
    PublicWebsitePresentationDefinition presentation,
    string webRootPath)
  {
    ArgumentNullException.ThrowIfNull(presentation);
    ArgumentException.ThrowIfNullOrWhiteSpace(webRootPath);

    foreach (var asset in presentation.Assets)
      ResolveExistingAssetPhysicalPath(webRootPath, asset.Value, $"Assets:{asset.Key}");
  }

  public static string ResolveExistingAssetPhysicalPath(
    string webRootPath,
    string rootRelativePath)
    => ResolveExistingAssetPhysicalPath(webRootPath, rootRelativePath, "Asset path");

  public static string NormalizeAssetKey(string? value)
  {
    var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
    if (normalized.Length is < 2 or > 80
        || normalized[0] == '-' || normalized[^1] == '-'
        || normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
      throw Invalid("Asset keys must use 2 to 80 lowercase-compatible letters, numbers or hyphens.");
    return normalized;
  }

  private static Dictionary<string, string> NormalizeAssets(IReadOnlyDictionary<string, string>? values)
  {
    var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var pair in values ?? new Dictionary<string, string>())
    {
      var key = NormalizeAssetKey(pair.Key);
      if (!normalized.TryAdd(key, NormalizeAssetPath(pair.Value, $"Assets:{key}")))
        throw Invalid($"Assets contains the duplicated key '{key}'.");
    }
    return normalized;
  }

  private static string ResolveExistingAssetPhysicalPath(
    string webRootPath,
    string rootRelativePath,
    string field)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(webRootPath);
    var normalizedPath = NormalizeAssetPath(rootRelativePath, field);
    var root = Path.GetFullPath(webRootPath).TrimEnd(
      Path.DirectorySeparatorChar,
      Path.AltDirectorySeparatorChar);
    var relativePath = normalizedPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
    var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
    if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || !File.Exists(fullPath))
      throw Invalid($"{field} does not resolve to an existing file inside wwwroot.");
    return fullPath;
  }

  private static string NormalizeAssetPath(string? value, string field)
  {
    var path = RequiredText(value, field, 500);
    if (!path.StartsWith("/", StringComparison.Ordinal)
        || path.StartsWith("//", StringComparison.Ordinal)
        || path.Contains('\\')
        || path.Contains('%')
        || path.Contains('?')
        || path.Contains('#')
        || path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
      throw Invalid($"{field} must be a root-relative local asset path without query, fragment or traversal segments.");
    return path;
  }

  private static string NormalizeLocale(string? value)
  {
    var locale = RequiredText(value, nameof(PublicWebsitePresentationOptions.Locale), 20);
    try
    {
      return CultureInfo.GetCultureInfo(locale).Name;
    }
    catch (CultureNotFoundException)
    {
      throw Invalid("Locale is not a recognized culture name.");
    }
  }

  private static string NormalizeEmail(string? value, string field)
  {
    var normalized = RequiredText(value, field, 320);
    try
    {
      var address = new MailAddress(normalized);
      if (!string.Equals(address.Address, normalized, StringComparison.OrdinalIgnoreCase))
        throw new FormatException();
      return address.Address;
    }
    catch (FormatException)
    {
      throw Invalid($"{field} must be a single valid email address.");
    }
  }

  private static string NormalizeRequiredE164(string? value, string field)
    => NormalizeOptionalE164(value, field)
      ?? throw Invalid($"{field} is required.");

  private static string? NormalizeOptionalE164(string? value, string field)
  {
    var normalized = OptionalText(value, field, 16);
    if (normalized is null)
      return null;
    if (normalized.Length is < 9 or > 16
        || normalized[0] != '+'
        || normalized[1] == '0'
        || normalized.Skip(1).Any(character => !char.IsAsciiDigit(character)))
      throw Invalid($"{field} must use E.164 format, for example +527491234567.");
    return normalized;
  }

  private static void EnsureDisplayMatchesE164(string display, string e164, string field)
  {
    var displayDigits = new string(display.Where(char.IsAsciiDigit).ToArray());
    var e164Digits = e164.TrimStart('+');
    if (!string.Equals(displayDigits, e164Digits, StringComparison.Ordinal))
      throw Invalid($"{field} must display the same digits configured in its E.164 value.");
  }

  private static string NormalizeColor(string? value, string field)
  {
    var normalized = RequiredText(value, field, 7).ToUpperInvariant();
    if (normalized.Length != 7
        || normalized[0] != '#'
        || normalized.Skip(1).Any(character => !Uri.IsHexDigit(character)))
      throw Invalid($"{field} must be a six-digit hexadecimal color such as #1A2B3C.");
    return normalized;
  }

  private static string RequiredText(string? value, string field, int maximum)
    => OptionalText(value, field, maximum)
      ?? throw Invalid($"{field} is required.");

  private static string? OptionalText(string? value, string field, int maximum)
  {
    var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    if (normalized is not null
        && (normalized.Length > maximum || normalized.Any(char.IsControl)))
      throw Invalid($"{field} cannot exceed {maximum} characters or contain control characters.");
    return normalized;
  }

  private static PublicWebsitePresentationException Invalid(string message)
    => new($"Invalid {PublicWebsitePresentationOptions.SectionName} configuration: {message}");
}
