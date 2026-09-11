using System.Data.Common;

namespace OrionERP.Application.Features.Platform;

/// <summary>
/// Immutable, database-verified execution boundary. Callers may transport this
/// value, but only platform resolvers may create it from persisted authority.
/// </summary>
public sealed record PlatformExecutionScope(
  long CompanyId,
  string CompanyRfc,
  string? TaxRfc = null,
  long? SiteId = null,
  string? ModuleCode = null,
  long? PublicSiteId = null,
  string? PublicSiteKey = null,
  int? ModuleLocalSiteId = null)
{
  public static PlatformExecutionScope FromPublicSite(PublicSiteBinding binding, int? moduleLocalSiteId = null)
  {
    ArgumentNullException.ThrowIfNull(binding);
    return new PlatformExecutionScope(
      binding.CompanyId,
      binding.CompanyRfc,
      binding.TaxRfc,
      binding.SiteId,
      binding.ModuleCode,
      binding.PublicSiteId,
      binding.PublicSiteKey,
      moduleLocalSiteId).EnsureValid();
  }

  public PlatformExecutionScope EnsureValid()
  {
    if (CompanyId <= 0 || string.IsNullOrWhiteSpace(CompanyRfc))
      throw new UnauthorizedAccessException("El alcance de empresa no es válido.");
    if (SiteId is <= 0 || PublicSiteId is <= 0 || ModuleLocalSiteId is <= 0)
      throw new UnauthorizedAccessException("El alcance contiene un identificador de sede o sitio inválido.");
    if (SiteId.HasValue != !string.IsNullOrWhiteSpace(ModuleCode))
      throw new UnauthorizedAccessException("La sede y el módulo deben declararse juntos.");
    if (PublicSiteId.HasValue != !string.IsNullOrWhiteSpace(PublicSiteKey))
      throw new UnauthorizedAccessException("La identidad pública está incompleta.");
    if (PublicSiteId.HasValue && (!SiteId.HasValue || string.IsNullOrWhiteSpace(ModuleCode)))
      throw new UnauthorizedAccessException("Un sitio público requiere empresa, sede y módulo.");
    return this;
  }
}

public interface IPlatformExecutionScopeResolver
{
  Task<PlatformExecutionScope> ResolvePublicSiteAsync(
    PublicSiteResolutionRequest request,
    CancellationToken ct = default);
}

/// <summary>Opens a connection after validating and installing an explicit platform scope.</summary>
public interface IOrionSqlSessionFactory
{
  Task<DbConnection> OpenAsync(PlatformExecutionScope scope, CancellationToken ct = default);
}
