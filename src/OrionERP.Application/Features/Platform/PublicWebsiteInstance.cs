using OrionERP.Application.Common;

namespace OrionERP.Application.Features.Platform;

/// <summary>
/// Trusted process configuration for one deployment of a reusable public
/// website host. None of these values may be populated from an HTTP request.
/// </summary>
public sealed class PublicWebsiteInstanceOptions
{
  public const string SectionName = "PublicWebsite";

  public string PublicSiteKey { get; set; } = string.Empty;
  public string ExpectedCompanyRfc { get; set; } = string.Empty;
  public string SiteKey { get; set; } = string.Empty;
  public string ModuleCode { get; set; } = string.Empty;
  public string CanonicalHost { get; set; } = string.Empty;
  public int LoopbackPort { get; set; }
}

public sealed record PublicWebsiteInstanceDefinition(
  string PublicSiteKey,
  string ExpectedCompanyRfc,
  string SiteKey,
  string ModuleCode,
  string CanonicalHost,
  int LoopbackPort)
{
  public Uri CanonicalBaseUri => new($"https://{CanonicalHost}/", UriKind.Absolute);

  public PublicSiteResolutionRequest ToResolutionRequest()
    => new(PublicSiteKey, ExpectedCompanyRfc, SiteKey, ModuleCode, CanonicalHost);
}

public static class PublicWebsiteInstancePolicy
{
  public static PublicWebsiteInstanceDefinition Create(
    PublicWebsiteInstanceOptions options,
    string requiredModuleCode)
  {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentException.ThrowIfNullOrWhiteSpace(requiredModuleCode);

    PublicSiteResolutionRequest normalized;
    try
    {
      normalized = PublicSiteResolutionPolicy.NormalizeRequest(new PublicSiteResolutionRequest(
        options.PublicSiteKey,
        options.ExpectedCompanyRfc,
        options.SiteKey,
        string.IsNullOrWhiteSpace(options.ModuleCode) ? requiredModuleCode : options.ModuleCode,
        options.CanonicalHost));
    }
    catch (ArgumentException exception)
    {
      throw new InvalidOperationException(
        $"Invalid {PublicWebsiteInstanceOptions.SectionName} configuration: {exception.Message}",
        exception);
    }

    if (!string.Equals(normalized.ExpectedModuleCode, requiredModuleCode, StringComparison.Ordinal))
    {
      throw new InvalidOperationException(
        $"Invalid {PublicWebsiteInstanceOptions.SectionName}:ModuleCode. " +
        $"This host requires the fixed module '{requiredModuleCode}'.");
    }

    if (options.LoopbackPort is < 1 or > 65535)
    {
      throw new InvalidOperationException(
        $"Invalid {PublicWebsiteInstanceOptions.SectionName}:LoopbackPort. " +
        "The port must be between 1 and 65535.");
    }

    return new PublicWebsiteInstanceDefinition(
      normalized.PublicSiteKey,
      normalized.ExpectedCompanyRfc,
      normalized.ExpectedSiteKey,
      normalized.ExpectedModuleCode,
      normalized.ExpectedCanonicalHost,
      options.LoopbackPort);
  }
}

/// <summary>
/// Process-scoped identity plus its database-verified binding. The legacy RFC
/// accessor is intentionally sourced from process configuration, never from a
/// route, query string, header or requested host.
/// </summary>
public interface IPublicWebsiteInstanceContext : ICurrentRfcAccessor
{
  PublicWebsiteInstanceDefinition Instance { get; }
  new string CurrentRfc { get; }

  Task<PublicSiteBinding> ResolveRequiredAsync(CancellationToken ct = default);
}
