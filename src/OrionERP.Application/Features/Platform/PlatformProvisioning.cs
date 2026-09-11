namespace OrionERP.Application.Features.Platform;

public sealed record ModuleSiteBinding(
  long CompanyId,
  long SiteId,
  string ModuleCode,
  int ModuleLocalSiteId);

public interface IModuleSiteBindingResolver
{
  Task<ModuleSiteBinding> ResolveRequiredAsync(
    PlatformExecutionScope scope,
    CancellationToken ct = default);
}

public interface IEnabledModuleSiteEnumerator
{
  Task<IReadOnlyList<PlatformExecutionScope>> ListAsync(
    string? moduleCode = null,
    CancellationToken ct = default);
}

public interface IModuleJobLease : IAsyncDisposable
{
  bool IsAcquired { get; }
}

public interface IModuleJobLeaseManager
{
  Task<IModuleJobLease> TryAcquireAsync(
    string jobName,
    PlatformExecutionScope scope,
    CancellationToken ct = default);
}

public sealed record PublicIntegrationSetting(
  string IntegrationCode,
  string SettingsJson,
  string? SecretReference,
  long ConfigurationVersion);

public interface IPublicIntegrationSettingsResolver
{
  Task<IReadOnlyList<PublicIntegrationSetting>> ListAsync(
    PlatformExecutionScope scope,
    CancellationToken ct = default);
}

public sealed record ProvisioningSite(
  string SiteKey,
  string DisplayName,
  string TimeZoneId,
  IReadOnlyList<string> EnabledModules);

public sealed record ProvisioningPublicSite(
  string PublicSiteKey,
  string SiteKey,
  string ModuleCode,
  string CanonicalHost,
  string? SqlPrincipalName = null);

public sealed record PublicSiteProvisioningRequest(
  Guid OperationId,
  string Rfc,
  string? TaxRfc,
  string LegacyTenantKey,
  string DisplayName,
  string? LegalName,
  IReadOnlyList<ProvisioningSite> Sites,
  IReadOnlyList<ProvisioningPublicSite> PublicSites);

public sealed record PublicSiteProvisioningStep(
  string StepCode,
  string Description,
  bool AlreadySatisfied,
  bool IsExternal);

public sealed record PublicSiteProvisioningPlan(
  Guid OperationId,
  string Rfc,
  bool CanApply,
  IReadOnlyList<string> Errors,
  IReadOnlyList<PublicSiteProvisioningStep> Steps);

public interface IPublicSiteProvisioner
{
  Task<PublicSiteProvisioningPlan> PreviewAsync(
    PublicSiteProvisioningRequest request,
    CancellationToken ct = default);

  Task<PublicSiteProvisioningPlan> ApplyAsync(
    PublicSiteProvisioningRequest request,
    CancellationToken ct = default);
}
