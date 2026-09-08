namespace OrionERP.Infrastructure.Features.Platform.Data;

public sealed class PlatformCompanyEntity
{
  public long CompanyId { get; set; }
  public string Rfc { get; set; } = string.Empty;
  public string? TaxRfc { get; set; }
  public string? LegacyTenantKey { get; set; }
  public string DisplayName { get; set; } = string.Empty;
  public string? LegalName { get; set; }
  public bool IsActive { get; set; }
  public long BrandingVersion { get; set; }
  public DateTime CreatedAtUtc { get; set; }
  public DateTime UpdatedAtUtc { get; set; }
  public string? UpdatedBy { get; set; }
  public byte[] RowVersion { get; set; } = [];
}

public sealed class PlatformSiteEntity
{
  public long SiteId { get; set; }
  public long CompanyId { get; set; }
  public string SiteKey { get; set; } = string.Empty;
  public string DisplayName { get; set; } = string.Empty;
  public string TimeZoneId { get; set; } = string.Empty;
  public bool IsActive { get; set; }
  public DateTime CreatedAtUtc { get; set; }
  public DateTime UpdatedAtUtc { get; set; }
  public string? UpdatedBy { get; set; }
  public byte[] RowVersion { get; set; } = [];
}

public sealed class PlatformModuleEntity
{
  public string ModuleCode { get; set; } = string.Empty;
  public string DisplayName { get; set; } = string.Empty;
  public string? Description { get; set; }
  public bool IsCore { get; set; }
  public bool RequiresSite { get; set; }
  public bool IsActive { get; set; }
  public DateTime CreatedAtUtc { get; set; }
  public DateTime UpdatedAtUtc { get; set; }
  public string? UpdatedBy { get; set; }
  public byte[] RowVersion { get; set; } = [];
}

public sealed class PlatformCompanyModuleEntity
{
  public long CompanyId { get; set; }
  public string ModuleCode { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
  public bool IsEnabled { get; private set; }
  public DateTime? EffectiveFromUtc { get; set; }
  public DateTime? EffectiveToUtc { get; set; }
  public long ConfigurationVersion { get; set; }
  public DateTime CreatedAtUtc { get; set; }
  public DateTime UpdatedAtUtc { get; set; }
  public string? UpdatedBy { get; set; }
  public byte[] RowVersion { get; set; } = [];
}

public sealed class PlatformSiteCapabilityEntity
{
  public long CompanyId { get; set; }
  public long SiteId { get; set; }
  public string ModuleCode { get; set; } = string.Empty;
  public bool IsEnabled { get; set; }
  public DateTime CreatedAtUtc { get; set; }
  public DateTime UpdatedAtUtc { get; set; }
  public string? UpdatedBy { get; set; }
  public byte[] RowVersion { get; set; } = [];
}

public sealed class PlatformPublicSiteEntity
{
  public long PublicSiteId { get; set; }
  public string PublicSiteKey { get; set; } = string.Empty;
  public long CompanyId { get; set; }
  public long SiteId { get; set; }
  public string ModuleCode { get; set; } = string.Empty;
  public string CanonicalHost { get; set; } = string.Empty;
  public bool IsActive { get; set; }
  public long ConfigurationVersion { get; set; }
  public long BrandingVersion { get; set; }
  public long ContentVersion { get; set; }
  public long? FallbackBrandingVersion { get; set; }
  public long? FallbackContentVersion { get; set; }
  public DateTime? FallbackUntilUtc { get; set; }
  public DateTime CreatedAtUtc { get; set; }
  public DateTime UpdatedAtUtc { get; set; }
  public string? UpdatedBy { get; set; }
  public byte[] RowVersion { get; set; } = [];
}
