using System.Globalization;

namespace OrionERP.Application.Features.Platform;

public sealed record PublicSiteResolutionRequest(
  string PublicSiteKey,
  string ExpectedCompanyRfc,
  string ExpectedSiteKey,
  string ExpectedModuleCode,
  string ExpectedCanonicalHost);

public sealed record PublicSiteBinding(
  long PublicSiteId,
  string PublicSiteKey,
  long CompanyId,
  string CompanyRfc,
  string? TaxRfc,
  string? LegacyTenantKey,
  long SiteId,
  string SiteKey,
  string SiteDisplayName,
  string TimeZoneId,
  string ModuleCode,
  long CompanyModuleConfigurationVersion,
  string CanonicalHost,
  long ConfigurationVersion,
  long BrandingVersion,
  long ContentVersion,
  long? FallbackBrandingVersion = null,
  long? FallbackContentVersion = null,
  DateTime? FallbackUntilUtc = null);

public interface IPublicSiteResolver
{
  /// <summary>
  /// Resolves one configured public-site identity. The implementation must not
  /// fall back to a host, RFC, legacy key or another active site.
  /// </summary>
  Task<PublicSiteBinding> ResolveRequiredAsync(
    PublicSiteResolutionRequest request,
    CancellationToken ct = default);
}

public enum PublicSiteResolutionFailure
{
  InvalidExpectation,
  InvalidRegistration,
  NotFound,
  PublicSiteInactive,
  CompanyInactive,
  ModuleInactive,
  ModuleNotPublic,
  CompanyModuleMissing,
  CompanyModuleInactive,
  SiteInactive,
  SiteCapabilityMissing,
  SiteCapabilityInactive,
  CompanyMismatch,
  SiteMismatch,
  ModuleMismatch,
  CanonicalHostMismatch
}

public sealed class PublicSiteResolutionException : InvalidOperationException
{
  public PublicSiteResolutionException(PublicSiteResolutionFailure failure, string message)
    : base(message)
  {
    Failure = failure;
  }

  public PublicSiteResolutionFailure Failure { get; }
}

/// <summary>
/// Database projection used by the fail-closed resolver. It includes the full
/// entitlement chain so an apparently active PublicSite is never sufficient by
/// itself.
/// </summary>
public sealed record PublicSiteResolutionCandidate(
  long PublicSiteId,
  string PublicSiteKey,
  long CompanyId,
  string CompanyRfc,
  string? TaxRfc,
  string? LegacyTenantKey,
  bool CompanyIsActive,
  long SiteId,
  string SiteKey,
  string SiteDisplayName,
  string TimeZoneId,
  bool SiteIsActive,
  string ModuleCode,
  bool ModuleIsActive,
  bool ModuleRequiresSite,
  bool CompanyModuleExists,
  string? CompanyModuleStatus,
  DateTime? CompanyModuleEffectiveFromUtc,
  DateTime? CompanyModuleEffectiveToUtc,
  long CompanyModuleConfigurationVersion,
  bool SiteCapabilityExists,
  bool SiteCapabilityIsEnabled,
  string CanonicalHost,
  bool PublicSiteIsActive,
  long ConfigurationVersion,
  long BrandingVersion,
  long ContentVersion,
  long? FallbackBrandingVersion = null,
  long? FallbackContentVersion = null,
  DateTime? FallbackUntilUtc = null);

public static class PublicSiteResolutionPolicy
{
  public static PublicSiteResolutionRequest NormalizeRequest(PublicSiteResolutionRequest request)
  {
    ArgumentNullException.ThrowIfNull(request);

    return new PublicSiteResolutionRequest(
      NormalizeKey(request.PublicSiteKey, nameof(request.PublicSiteKey), 3, 100),
      NormalizeRfc(request.ExpectedCompanyRfc),
      NormalizeKey(request.ExpectedSiteKey, nameof(request.ExpectedSiteKey), 2, 100),
      NormalizeModuleCode(request.ExpectedModuleCode),
      NormalizeCanonicalHost(request.ExpectedCanonicalHost));
  }

  public static PublicSiteBinding Resolve(
    PublicSiteResolutionRequest request,
    PublicSiteResolutionCandidate? candidate,
    DateTime utcNow)
  {
    ArgumentNullException.ThrowIfNull(request);

    PublicSiteResolutionRequest normalizedRequest;
    try
    {
      normalizedRequest = NormalizeRequest(request);
    }
    catch (ArgumentException exception)
    {
      throw Failure(PublicSiteResolutionFailure.InvalidExpectation, exception.Message);
    }

    var publicSiteKey = normalizedRequest.PublicSiteKey;
    var expectedCompanyRfc = normalizedRequest.ExpectedCompanyRfc;
    var expectedSiteKey = normalizedRequest.ExpectedSiteKey;
    var expectedModuleCode = normalizedRequest.ExpectedModuleCode;
    var expectedCanonicalHost = normalizedRequest.ExpectedCanonicalHost;

    if (candidate is null)
      throw Failure(PublicSiteResolutionFailure.NotFound, $"PublicSite '{publicSiteKey}' is not registered.");

    string candidatePublicSiteKey;
    string candidateCompanyRfc;
    string candidateSiteKey;
    string candidateModuleCode;
    string candidateCanonicalHost;
    try
    {
      candidatePublicSiteKey = NormalizeKey(candidate.PublicSiteKey, nameof(candidate.PublicSiteKey), 3, 100);
      candidateCompanyRfc = NormalizeRfc(candidate.CompanyRfc);
      candidateSiteKey = NormalizeKey(candidate.SiteKey, nameof(candidate.SiteKey), 2, 100);
      candidateModuleCode = NormalizeModuleCode(candidate.ModuleCode);
      candidateCanonicalHost = NormalizeCanonicalHost(candidate.CanonicalHost);
    }
    catch (ArgumentException exception)
    {
      throw Failure(PublicSiteResolutionFailure.InvalidRegistration, exception.Message);
    }

    // PublicSiteKey is the sole lookup key. Every other configured value is an
    // expectation that must match the resulting row exactly.
    if (!string.Equals(candidatePublicSiteKey, publicSiteKey, StringComparison.Ordinal))
      throw Failure(PublicSiteResolutionFailure.NotFound, "The resolved PublicSite key is inconsistent.");
    if (!candidate.PublicSiteIsActive)
      throw Failure(PublicSiteResolutionFailure.PublicSiteInactive, "The configured PublicSite is inactive.");
    if (!candidate.CompanyIsActive)
      throw Failure(PublicSiteResolutionFailure.CompanyInactive, "The PublicSite company is inactive.");
    if (!candidate.ModuleIsActive)
      throw Failure(PublicSiteResolutionFailure.ModuleInactive, "The PublicSite module is inactive.");
    if (!candidate.ModuleRequiresSite || !PlatformModuleCodes.PublicWebsiteModules.Contains(candidateModuleCode))
      throw Failure(PublicSiteResolutionFailure.ModuleNotPublic, "The module cannot be exposed as a public website.");
    if (!candidate.CompanyModuleExists)
      throw Failure(PublicSiteResolutionFailure.CompanyModuleMissing, "The company has no explicit module assignment.");
    if (!IsEffective(candidate.CompanyModuleStatus, candidate.CompanyModuleEffectiveFromUtc, candidate.CompanyModuleEffectiveToUtc, utcNow))
      throw Failure(PublicSiteResolutionFailure.CompanyModuleInactive, "The company module is not effective.");
    if (!candidate.SiteIsActive)
      throw Failure(PublicSiteResolutionFailure.SiteInactive, "The PublicSite site is inactive.");
    if (!candidate.SiteCapabilityExists)
      throw Failure(PublicSiteResolutionFailure.SiteCapabilityMissing, "The site has no module capability assignment.");
    if (!candidate.SiteCapabilityIsEnabled)
      throw Failure(PublicSiteResolutionFailure.SiteCapabilityInactive, "The site module capability is inactive.");

    if (!string.Equals(candidateCompanyRfc, expectedCompanyRfc, StringComparison.Ordinal))
      throw Failure(PublicSiteResolutionFailure.CompanyMismatch, "PublicSiteKey does not belong to the expected company.");
    if (!string.Equals(candidateSiteKey, expectedSiteKey, StringComparison.Ordinal))
      throw Failure(PublicSiteResolutionFailure.SiteMismatch, "PublicSiteKey does not belong to the expected site.");
    if (!string.Equals(candidateModuleCode, expectedModuleCode, StringComparison.Ordinal))
      throw Failure(PublicSiteResolutionFailure.ModuleMismatch, "PublicSiteKey does not belong to the expected module.");
    if (!string.Equals(candidateCanonicalHost, expectedCanonicalHost, StringComparison.Ordinal))
      throw Failure(PublicSiteResolutionFailure.CanonicalHostMismatch, "PublicSiteKey does not belong to the expected canonical host.");

    if (candidate.ConfigurationVersion <= 0
        || candidate.BrandingVersion <= 0
        || candidate.ContentVersion <= 0)
      throw Failure(PublicSiteResolutionFailure.InvalidRegistration, "PublicSite versions must be greater than zero.");

    var hasFallbackBranding = candidate.FallbackBrandingVersion.HasValue;
    var hasFallbackContent = candidate.FallbackContentVersion.HasValue;
    var hasFallbackExpiry = candidate.FallbackUntilUtc.HasValue;
    if (hasFallbackBranding != hasFallbackContent
        || hasFallbackBranding != hasFallbackExpiry
        || (hasFallbackBranding
          && (candidate.FallbackBrandingVersion <= 0
            || candidate.FallbackContentVersion <= 0)))
      throw Failure(PublicSiteResolutionFailure.InvalidRegistration, "PublicSite presentation fallback is incomplete or invalid.");

    var fallbackUntilUtc = candidate.FallbackUntilUtc.HasValue
      ? DateTime.SpecifyKind(candidate.FallbackUntilUtc.Value, DateTimeKind.Utc)
      : (DateTime?)null;

    return new PublicSiteBinding(
      candidate.PublicSiteId,
      publicSiteKey,
      candidate.CompanyId,
      expectedCompanyRfc,
      NullIfWhiteSpace(candidate.TaxRfc),
      NullIfWhiteSpace(candidate.LegacyTenantKey),
      candidate.SiteId,
      candidateSiteKey,
      candidate.SiteDisplayName,
      candidate.TimeZoneId,
      candidateModuleCode,
      candidate.CompanyModuleConfigurationVersion,
      candidateCanonicalHost,
      candidate.ConfigurationVersion,
      candidate.BrandingVersion,
      candidate.ContentVersion,
      candidate.FallbackBrandingVersion,
      candidate.FallbackContentVersion,
      fallbackUntilUtc);
  }

  public static string NormalizePublicSiteKey(string value)
    => NormalizeKey(value, nameof(value), 3, 100);

  public static bool IsEffective(
    string? status,
    DateTime? effectiveFromUtc,
    DateTime? effectiveToUtc,
    DateTime utcNow)
  {
    var now = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
    return string.Equals(status, PlatformCompanyModuleStatuses.Enabled, StringComparison.Ordinal)
      && (!effectiveFromUtc.HasValue || effectiveFromUtc.Value <= now)
      && (!effectiveToUtc.HasValue || effectiveToUtc.Value > now);
  }

  private static string NormalizeRfc(string value)
  {
    var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
    if (normalized.Length is < 12 or > 20
        || normalized.Any(character => !char.IsAsciiLetterUpper(character) && !char.IsAsciiDigit(character) && character is not '&' and not 'Ñ'))
      throw new ArgumentException("ExpectedCompanyRfc must be a valid platform RFC identifier.", nameof(value));
    return normalized;
  }

  private static string NormalizeModuleCode(string value)
  {
    var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
    if (!PlatformModuleCodes.Known.Contains(normalized))
      throw new ArgumentException("ExpectedModuleCode is not a known platform module code.", nameof(value));
    return normalized;
  }

  private static string NormalizeKey(string value, string parameterName, int minLength, int maxLength)
  {
    var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
    if (normalized.Length < minLength || normalized.Length > maxLength
        || normalized[0] == '-' || normalized[^1] == '-'
        || normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
      throw new ArgumentException($"{parameterName} must be a lowercase-compatible slug between {minLength} and {maxLength} characters.", parameterName);
    return normalized;
  }

  private static string NormalizeCanonicalHost(string value)
  {
    var host = value?.Trim().TrimEnd('.') ?? string.Empty;
    if (host.Length is 0 or > 253
        || host.Contains('/') || host.Contains(':') || host.Contains('@') || host.Contains('?') || host.Contains('#') || host.Contains('*'))
      throw new ArgumentException("ExpectedCanonicalHost must contain only a DNS host, without scheme, port, path or wildcard.", nameof(value));

    string asciiHost;
    try
    {
      asciiHost = new IdnMapping().GetAscii(host).ToLowerInvariant();
    }
    catch (ArgumentException)
    {
      throw new ArgumentException("ExpectedCanonicalHost is not a valid DNS host.", nameof(value));
    }

    if (Uri.CheckHostName(asciiHost) != UriHostNameType.Dns || !asciiHost.Contains('.'))
      throw new ArgumentException("ExpectedCanonicalHost must be a fully-qualified DNS host.", nameof(value));
    return asciiHost;
  }

  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private static PublicSiteResolutionException Failure(PublicSiteResolutionFailure failure, string message)
    => new(failure, message);
}
