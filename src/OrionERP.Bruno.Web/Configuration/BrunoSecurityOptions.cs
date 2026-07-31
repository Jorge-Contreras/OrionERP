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
  public bool IsConfigured => !string.IsNullOrWhiteSpace(SiteKey) && !string.IsNullOrWhiteSpace(SecretKey);
}

public sealed class BrunoTwilioVerifyOptions
{
  public const string SectionName = "TwilioVerify";
  public string AccountSid { get; set; } = string.Empty;
  public string AuthToken { get; set; } = string.Empty;
  public string ServiceSid { get; set; } = string.Empty;
  public bool IsConfigured =>
    !string.IsNullOrWhiteSpace(AccountSid) &&
    !string.IsNullOrWhiteSpace(AuthToken) &&
    !string.IsNullOrWhiteSpace(ServiceSid);
}
