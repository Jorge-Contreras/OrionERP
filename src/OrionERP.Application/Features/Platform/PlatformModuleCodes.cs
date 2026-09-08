namespace OrionERP.Application.Features.Platform;

/// <summary>
/// Stable identifiers for the platform module catalog. These values are data
/// contracts; they are not tenant-to-module assignments.
/// </summary>
public static class PlatformModuleCodes
{
  public const string AccountingCore = "ACCOUNTING_CORE";
  public const string Hospitality = "HOSPITALITY";
  public const string Restaurant = "RESTAURANT";

  public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
  {
    AccountingCore,
    Hospitality,
    Restaurant
  };

  public static readonly IReadOnlySet<string> PublicWebsiteModules = new HashSet<string>(StringComparer.Ordinal)
  {
    Hospitality,
    Restaurant
  };
}

public static class PlatformCompanyModuleStatuses
{
  public const string Provisioning = "Provisioning";
  public const string Enabled = "Enabled";
  public const string Suspended = "Suspended";

  public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
  {
    Provisioning,
    Enabled,
    Suspended
  };
}
