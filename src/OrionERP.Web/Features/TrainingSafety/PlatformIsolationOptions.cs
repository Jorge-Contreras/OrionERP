namespace OrionERP.Web.Features.TrainingSafety;

public sealed class PlatformIsolationOptions
{
  public const string SectionName = "PlatformIsolation";
  public const string ProductionDataProtectionApplicationName = "OrionERP";
  public const string ProductionDataProtectionKeyPath = "App_Data/keys";
  public const string ProductionAntiforgeryCookieName = ".OrionERP.Management.Antiforgery";
  public const string ProductionIdentityCookieName = ".AspNetCore.Identity.Application";
  public const string ProductionKioskDeviceCookieName = "orion-kiosk-device";

  public string DataProtectionApplicationName { get; init; } = ProductionDataProtectionApplicationName;
  public string DataProtectionKeyPath { get; init; } = ProductionDataProtectionKeyPath;
  public string AntiforgeryCookieName { get; init; } = ProductionAntiforgeryCookieName;
  public string IdentityCookieName { get; init; } = ProductionIdentityCookieName;
  public string KioskDeviceCookieName { get; init; } = ProductionKioskDeviceCookieName;

  public static PlatformIsolationOptions FromConfiguration(IConfiguration configuration)
  {
    var options = configuration.GetSection(SectionName).Get<PlatformIsolationOptions>()
      ?? new PlatformIsolationOptions();

    if (string.IsNullOrWhiteSpace(options.DataProtectionApplicationName))
      throw new InvalidOperationException($"{SectionName}:DataProtectionApplicationName is required.");
    if (string.IsNullOrWhiteSpace(options.DataProtectionKeyPath))
      throw new InvalidOperationException($"{SectionName}:DataProtectionKeyPath is required.");
    if (string.IsNullOrWhiteSpace(options.AntiforgeryCookieName))
      throw new InvalidOperationException($"{SectionName}:AntiforgeryCookieName is required.");
    if (string.IsNullOrWhiteSpace(options.IdentityCookieName))
      throw new InvalidOperationException($"{SectionName}:IdentityCookieName is required.");
    if (string.IsNullOrWhiteSpace(options.KioskDeviceCookieName))
      throw new InvalidOperationException($"{SectionName}:KioskDeviceCookieName is required.");

    return options;
  }

  public string ResolveDataProtectionKeyDirectory(string baseDirectory)
  {
    var expanded = Environment.ExpandEnvironmentVariables(DataProtectionKeyPath.Trim());
    return Path.GetFullPath(Path.IsPathRooted(expanded)
      ? expanded
      : Path.Combine(baseDirectory, expanded));
  }
}
