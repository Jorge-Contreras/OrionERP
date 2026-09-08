namespace OrionERP.Application.Features.Platform;

public sealed record PlatformCompany(
  long CompanyId,
  string Rfc,
  string? TaxRfc,
  string? LegacyTenantKey,
  string DisplayName,
  string? LegalName,
  bool IsActive,
  long BrandingVersion,
  DateTime UpdatedAtUtc,
  string ConcurrencyToken);

public sealed record PlatformSite(
  long SiteId,
  long CompanyId,
  string SiteKey,
  string DisplayName,
  string TimeZoneId,
  bool IsActive,
  DateTime UpdatedAtUtc,
  string ConcurrencyToken);

public sealed record PlatformModule(
  string ModuleCode,
  string DisplayName,
  string? Description,
  bool IsCore,
  bool RequiresSite,
  bool IsActive,
  DateTime UpdatedAtUtc,
  string ConcurrencyToken);

public sealed record PlatformCompanyModule(
  long CompanyId,
  string ModuleCode,
  string Status,
  bool IsEnabled,
  DateTime? EffectiveFromUtc,
  DateTime? EffectiveToUtc,
  long ConfigurationVersion,
  bool IsEffective,
  DateTime UpdatedAtUtc,
  string ConcurrencyToken);

public sealed record PlatformSiteCapability(
  long CompanyId,
  long SiteId,
  string ModuleCode,
  bool IsEnabled,
  DateTime UpdatedAtUtc,
  string ConcurrencyToken);

public sealed record PlatformPublicSite(
  long PublicSiteId,
  string PublicSiteKey,
  long CompanyId,
  long SiteId,
  string ModuleCode,
  string CanonicalHost,
  bool IsActive,
  long ConfigurationVersion,
  long BrandingVersion,
  long ContentVersion,
  DateTime UpdatedAtUtc,
  string ConcurrencyToken,
  long? FallbackBrandingVersion = null,
  long? FallbackContentVersion = null,
  DateTime? FallbackUntilUtc = null);

public sealed record PlatformModuleStatus(
  PlatformModule Module,
  PlatformCompanyModule? Assignment,
  bool IsEntitled);

public sealed record PlatformAdministrationSnapshot(
  PlatformCompany Company,
  IReadOnlyList<PlatformSite> Sites,
  IReadOnlyList<PlatformModuleStatus> Modules,
  IReadOnlyList<PlatformSiteCapability> SiteCapabilities,
  IReadOnlyList<PlatformPublicSite> PublicSites,
  DateTime EvaluatedAtUtc);

public sealed record PlatformAdministrationScope(string ActorUserId, string CompanyRfc);

public static class PlatformAdministrationAuthorization
{
  public const string AdministratorRole = "Administrador";
}

/// <summary>
/// Supplies an authenticated, authorized and company-bound administration
/// scope. Implementations must derive this information from trusted server-side
/// state rather than request payloads.
/// </summary>
public interface IPlatformAdministrationScopeAccessor
{
  Task<PlatformAdministrationScope> GetRequiredScopeAsync(CancellationToken ct = default);
}

/// <summary>
/// Revalidates an administrative session against the authoritative identity
/// store. Implementations must perform a fresh persistence query on every call
/// and must not rely on, or cache a result from, authentication claims.
/// </summary>
public interface IPlatformAdministrationAccessValidator
{
  Task EnsureAuthorizedAsync(
    string actorUserId,
    string companyRfc,
    CancellationToken ct = default);
}

public interface IPlatformAdministrationReader
{
  Task<PlatformAdministrationSnapshot> GetCurrentCompanySnapshotAsync(CancellationToken ct = default);
}

public sealed record CreatePlatformSiteCommand(
  string SiteKey,
  string DisplayName,
  string TimeZoneId,
  bool IsActive);

public sealed record UpdatePlatformCompanyMetadataCommand(
  string? TaxRfc,
  string? LegacyTenantKey,
  string ConcurrencyToken);

public sealed record UpdatePlatformSiteCommand(
  long SiteId,
  string DisplayName,
  string TimeZoneId,
  bool IsActive,
  string ConcurrencyToken);

public sealed record SetPlatformCompanyModuleCommand(
  string ModuleCode,
  string Status,
  DateTime? EffectiveFromUtc,
  DateTime? EffectiveToUtc,
  string? ConcurrencyToken);

public sealed record SetPlatformSiteCapabilityCommand(
  long SiteId,
  string ModuleCode,
  bool IsEnabled,
  string? ConcurrencyToken);

public sealed record CreatePlatformPublicSiteCommand(
  string PublicSiteKey,
  long SiteId,
  string ModuleCode,
  string CanonicalHost,
  bool IsActive);

public sealed record UpdatePlatformPublicSiteCommand(
  long PublicSiteId,
  string CanonicalHost,
  bool IsActive,
  long BrandingVersion,
  long ContentVersion,
  string ConcurrencyToken);

/// <summary>
/// Mutates only the company bound to the authenticated administrator session.
/// Commands deliberately contain neither CompanyId nor RFC.
/// </summary>
public interface IPlatformAdministrationService
{
  Task UpdateCompanyMetadataAsync(UpdatePlatformCompanyMetadataCommand command, CancellationToken ct = default);

  Task<long> CreateSiteAsync(CreatePlatformSiteCommand command, CancellationToken ct = default);
  Task UpdateSiteAsync(UpdatePlatformSiteCommand command, CancellationToken ct = default);
  Task DeleteSiteAsync(long siteId, string concurrencyToken, CancellationToken ct = default);

  Task SetCompanyModuleAsync(SetPlatformCompanyModuleCommand command, CancellationToken ct = default);
  Task DeleteCompanyModuleAsync(string moduleCode, string concurrencyToken, CancellationToken ct = default);

  Task SetSiteCapabilityAsync(SetPlatformSiteCapabilityCommand command, CancellationToken ct = default);
  Task DeleteSiteCapabilityAsync(long siteId, string moduleCode, string concurrencyToken, CancellationToken ct = default);

  Task<long> CreatePublicSiteAsync(CreatePlatformPublicSiteCommand command, CancellationToken ct = default);
  Task UpdatePublicSiteAsync(UpdatePlatformPublicSiteCommand command, CancellationToken ct = default);
  Task RollbackPublicSitePresentationAsync(long publicSiteId, string concurrencyToken, CancellationToken ct = default);
  Task FinalizePublicSitePresentationAsync(long publicSiteId, string concurrencyToken, CancellationToken ct = default);
  Task DeletePublicSiteAsync(long publicSiteId, string concurrencyToken, CancellationToken ct = default);
}

public sealed class PlatformAdministrationValidationException : InvalidOperationException
{
  public PlatformAdministrationValidationException(string message)
    : base(message)
  {
  }
}

public sealed class PlatformAdministrationConcurrencyException : InvalidOperationException
{
  public PlatformAdministrationConcurrencyException()
    : base("La configuración cambió en otra sesión. Actualiza la página antes de volver a guardar.")
  {
  }
}
