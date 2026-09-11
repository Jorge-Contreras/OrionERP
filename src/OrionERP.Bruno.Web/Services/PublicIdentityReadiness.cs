using Dapper;
using OrionERP.Application.Features.Platform;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Bruno.Web.Services;

public interface IPublicIdentityReadiness
{
  Task<bool> IsReadyAsync(PublicIdentityScope scope, CancellationToken ct = default);
}

public sealed class PublicIdentityReadiness(IOrionSqlSessionFactory sessionFactory)
  : IPublicIdentityReadiness
{
  private const string RequiredMigrationId = "20260911_public_identity";
  private const string RequiredMigrationChecksum =
    "612B1791C842BDEF5D355F3E603F6A9531350C6B429943C181C0D37A90B736D6";

  public async Task<bool> IsReadyAsync(PublicIdentityScope scope, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(scope);
    if (scope.PublicSiteId <= 0)
      return false;

    await using var connection = await sessionFactory.OpenAsync(
      new PlatformExecutionScope(
        scope.CompanyId,
        scope.CompanyRfc,
        SiteId: scope.SiteId,
        ModuleCode: PlatformModuleCodes.Restaurant,
        PublicSiteId: scope.PublicSiteId,
        PublicSiteKey: scope.PublicSiteKey),
      ct);

    return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
      ReadinessSql,
      new
      {
        RequiredMigrationId,
        RequiredMigrationChecksum,
        scope.PublicSiteId,
        scope.PublicSiteKey,
        scope.CompanyId,
        scope.SiteId
      },
      cancellationToken: ct));
  }

  private const string ReadinessSql = """
      SELECT CAST(CASE WHEN
        EXISTS
        (
          SELECT 1 FROM orion.SchemaMigration
          WHERE MigrationId=@RequiredMigrationId AND UPPER(Checksum)=@RequiredMigrationChecksum
        )
        AND EXISTS
        (
          SELECT 1 FROM sys.security_policies
          WHERE object_id=OBJECT_ID(N'orion.PublicIdentityScopePolicy')
            AND is_enabled=1 AND is_schema_bound=1
        )
        AND (SELECT COUNT(*) FROM sys.security_predicates
             WHERE object_id=OBJECT_ID(N'orion.PublicIdentityScopePolicy'))=21
        AND NOT EXISTS
        (
          SELECT required.TableName
          FROM (VALUES
            (N'AspNetUsers'),(N'AspNetRoles'),(N'AspNetUserClaims'),
            (N'AspNetRoleClaims'),(N'AspNetUserLogins'),(N'AspNetUserRoles'),
            (N'AspNetUserTokens')) required(TableName)
          LEFT JOIN sys.tables tableInfo
            ON tableInfo.schema_id=SCHEMA_ID(N'public_identity')
           AND tableInfo.name=required.TableName
          LEFT JOIN sys.columns siteColumn
            ON siteColumn.object_id=tableInfo.object_id
           AND siteColumn.name=N'PublicSiteId'
          WHERE tableInfo.object_id IS NULL OR siteColumn.column_id IS NULL
             OR siteColumn.is_nullable<>0
        )
        AND EXISTS
        (
          SELECT 1 FROM orion.PublicIdentityCompatibilityState
          WHERE SingletonId=1 AND BridgeMode IN('Reverse','Off')
        )
        AND EXISTS
        (
          SELECT 1 FROM orion.PublicSite publicSite
          WHERE publicSite.PublicSiteId=@PublicSiteId
            AND publicSite.PublicSiteKey=@PublicSiteKey
            AND publicSite.CompanyId=@CompanyId
            AND publicSite.SiteId=@SiteId
            AND publicSite.ModuleCode='RESTAURANT'
            AND publicSite.IsActive=1
        )
        AND NOT EXISTS
        (
          SELECT 1
          FROM fidelidad.MemberAccount member
          LEFT JOIN public_identity.AspNetUsers identityUser
            ON identityUser.Id=member.IdentityUserId
           AND identityUser.PublicSiteId=member.PublicSiteId
          WHERE member.PublicSiteId=@PublicSiteId AND identityUser.Id IS NULL
        )
      THEN 1 ELSE 0 END AS bit);
      """;
}
