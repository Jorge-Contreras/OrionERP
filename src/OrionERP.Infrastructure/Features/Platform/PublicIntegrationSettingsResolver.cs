using Dapper;
using OrionERP.Application.Features.Platform;

namespace OrionERP.Infrastructure.Features.Platform;

public sealed class PublicIntegrationSettingsResolver(IOrionSqlSessionFactory sessionFactory)
  : IPublicIntegrationSettingsResolver
{
  public async Task<IReadOnlyList<PublicIntegrationSetting>> ListAsync(
    PlatformExecutionScope scope,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(scope);
    if (!scope.PublicSiteId.HasValue)
      throw new UnauthorizedAccessException("La configuración pública requiere PublicSiteId.");
    await using var connection = await sessionFactory.OpenAsync(scope, ct);
    var rows = await connection.QueryAsync<PublicIntegrationSetting>(new CommandDefinition(
      """
      SELECT IntegrationKind AS IntegrationCode,ConfigurationJson AS SettingsJson,
             SecretReference,ConfigurationVersion
      FROM orion.IntegrationBinding
      WHERE PublicSiteId=@PublicSiteId AND IsActive=1
      ORDER BY IntegrationCode;
      """,
      new { PublicSiteId = scope.PublicSiteId.Value },
      cancellationToken: ct));
    return rows.AsList();
  }
}
