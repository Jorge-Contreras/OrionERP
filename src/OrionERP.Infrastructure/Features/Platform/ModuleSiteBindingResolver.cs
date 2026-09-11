using Dapper;
using OrionERP.Application.Features.Platform;

namespace OrionERP.Infrastructure.Features.Platform;

public sealed class ModuleSiteBindingResolver(IOrionSqlSessionFactory sessionFactory)
  : IModuleSiteBindingResolver
{
  public async Task<ModuleSiteBinding> ResolveRequiredAsync(
    PlatformExecutionScope scope,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(scope);
    scope.EnsureValid();
    if (!scope.SiteId.HasValue || string.IsNullOrWhiteSpace(scope.ModuleCode))
      throw new UnauthorizedAccessException("El binding local requiere una sede y un módulo verificados.");

    if (!string.Equals(scope.ModuleCode, PlatformModuleCodes.Restaurant, StringComparison.OrdinalIgnoreCase))
      throw new NotSupportedException($"El módulo {scope.ModuleCode} no usa un binding de sede local.");

    await using var connection = await sessionFactory.OpenAsync(scope, ct);
    var localSiteId = await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
      """
      SELECT legacySite.Id
      FROM restaurante.Site legacySite
      WHERE legacySite.OrionCompanyId=@CompanyId AND legacySite.OrionSiteId=@SiteId
        AND legacySite.IsEnabled=1;
      """,
      new { scope.CompanyId, SiteId = scope.SiteId.Value },
      cancellationToken: ct));
    return localSiteId.HasValue
      ? new ModuleSiteBinding(scope.CompanyId, scope.SiteId.Value, PlatformModuleCodes.Restaurant, localSiteId.Value)
      : throw new UnauthorizedAccessException("La sede no tiene un binding Restaurant activo.");
  }
}
