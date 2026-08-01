namespace OrionERP.Bruno.Web.Configuration;

public sealed class BrunoSiteOptions
{
  public const string SectionName = "BrunoSite";
  public string PublicBaseUrl { get; set; } = "https://brunosgarden.com";
  public string CloudflareAnalyticsToken { get; set; } = string.Empty;
}

public sealed class BrunoTurnstileOptions
{
  public const string SectionName = "Turnstile";
  public string SiteKey { get; set; } = string.Empty;
  public string SecretKey { get; set; } = string.Empty;
  public string ExpectedHostname { get; set; } = "brunosgarden.com";
  public bool HasSiteKey => !string.IsNullOrWhiteSpace(SiteKey);
  public bool HasSecretKey => !string.IsNullOrWhiteSpace(SecretKey);
  public bool IsConfigured => !string.IsNullOrWhiteSpace(SiteKey) && !string.IsNullOrWhiteSpace(SecretKey);
  public bool HasConsistentKeyPair => HasSiteKey == HasSecretKey;
}
