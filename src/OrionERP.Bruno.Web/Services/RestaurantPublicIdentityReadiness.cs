using System.Data.Common;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Bruno.Web.Services;

public interface IRestaurantPublicIdentityReadiness
{
  Task<bool> IsReadyAsync(RestaurantPublicIdentityScope scope, CancellationToken ct = default);
}

/// <summary>
/// Verifies the discriminator, database constraints and current platform
/// binding without asking EF Identity to query columns that may not have been
/// migrated yet.
/// </summary>
public sealed class RestaurantPublicIdentityReadiness : IRestaurantPublicIdentityReadiness
{
  private const string RequiredMigrationId =
    "20260903_restaurant_public_identity_scope_sandbox";
  private const string RequiredMigrationChecksum =
    "6654D9CFBC3CB09222A98E1A3338141CFB9259078A7780E3D3BBC2FDCC9E09A7";

  private readonly IDbConnectionFactory _connectionFactory;

  public RestaurantPublicIdentityReadiness(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  }

  public async Task<bool> IsReadyAsync(
    RestaurantPublicIdentityScope scope,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(scope);
    if (scope.PublicSiteId <= 0) return false;

    using var connection = _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica no devolvió una DbConnection.");
    await connection.OpenAsync(ct);
    return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
      ReadinessSql,
      new
      {
        RequiredMigrationId,
        RequiredMigrationChecksum,
        scope.PublicSiteId,
        scope.PublicSiteKey,
        scope.CompanyId,
        scope.CompanyRfc,
        scope.SiteId,
        scope.SiteKey
      },
      cancellationToken: ct));
  }

  private const string ReadinessSql = """
      DECLARE @Ready bit=0;
      DECLARE @LedgerReady bit=0;

      IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NOT NULL
         AND COL_LENGTH(N'orion.SchemaMigration',N'MigrationId') IS NOT NULL
         AND COL_LENGTH(N'orion.SchemaMigration',N'Checksum') IS NOT NULL
      BEGIN
        EXEC sys.sp_executesql
          N'SELECT @Result=CAST(CASE WHEN EXISTS
            (
              SELECT 1
              FROM orion.SchemaMigration
              WHERE MigrationId=@RequiredMigrationId
                AND UPPER(Checksum)=UPPER(@RequiredMigrationChecksum)
            ) THEN 1 ELSE 0 END AS bit);',
          N'@RequiredMigrationId nvarchar(200),@RequiredMigrationChecksum varchar(128),@Result bit OUTPUT',
          @RequiredMigrationId,@RequiredMigrationChecksum,@LedgerReady OUTPUT;
      END;

      IF @LedgerReady=1
         AND EXISTS
         (
           SELECT 1
           FROM sys.columns identitySiteColumn
           WHERE identitySiteColumn.object_id=OBJECT_ID(N'brunos_auth.AspNetUsers')
             AND identitySiteColumn.name=N'PublicSiteId'
             AND identitySiteColumn.is_nullable=0
         )
         AND EXISTS
         (
           SELECT 1
           FROM sys.columns memberSiteColumn
           WHERE memberSiteColumn.object_id=OBJECT_ID(N'fidelidad.MemberAccount')
             AND memberSiteColumn.name=N'PublicSiteId'
             AND memberSiteColumn.is_nullable=0
         )
         AND EXISTS
         (
           SELECT 1
           FROM sys.indexes indexInfo
           JOIN sys.index_columns firstKey
             ON firstKey.object_id=indexInfo.object_id AND firstKey.index_id=indexInfo.index_id
            AND firstKey.key_ordinal=1
           JOIN sys.columns firstColumn
             ON firstColumn.object_id=firstKey.object_id AND firstColumn.column_id=firstKey.column_id
           JOIN sys.index_columns secondKey
             ON secondKey.object_id=indexInfo.object_id AND secondKey.index_id=indexInfo.index_id
            AND secondKey.key_ordinal=2
           JOIN sys.columns secondColumn
             ON secondColumn.object_id=secondKey.object_id AND secondColumn.column_id=secondKey.column_id
           WHERE indexInfo.object_id=OBJECT_ID(N'brunos_auth.AspNetUsers')
             AND indexInfo.name=N'UserNameIndex_Bruno'
             AND indexInfo.is_unique=1 AND indexInfo.is_disabled=0
             AND firstColumn.name=N'PublicSiteId' AND secondColumn.name=N'NormalizedUserName'
             AND NOT EXISTS
             (
               SELECT 1 FROM sys.index_columns extraKey
               WHERE extraKey.object_id=indexInfo.object_id AND extraKey.index_id=indexInfo.index_id
                 AND extraKey.key_ordinal>2
             )
         )
         AND EXISTS
         (
           SELECT 1
           FROM sys.indexes indexInfo
           JOIN sys.index_columns firstKey
             ON firstKey.object_id=indexInfo.object_id AND firstKey.index_id=indexInfo.index_id
            AND firstKey.key_ordinal=1
           JOIN sys.columns firstColumn
             ON firstColumn.object_id=firstKey.object_id AND firstColumn.column_id=firstKey.column_id
           JOIN sys.index_columns secondKey
             ON secondKey.object_id=indexInfo.object_id AND secondKey.index_id=indexInfo.index_id
            AND secondKey.key_ordinal=2
           JOIN sys.columns secondColumn
             ON secondColumn.object_id=secondKey.object_id AND secondColumn.column_id=secondKey.column_id
           WHERE indexInfo.object_id=OBJECT_ID(N'brunos_auth.AspNetUsers')
             AND indexInfo.name=N'EmailIndex_Bruno'
             AND indexInfo.is_unique=1 AND indexInfo.is_disabled=0
             AND firstColumn.name=N'PublicSiteId' AND secondColumn.name=N'NormalizedEmail'
             AND NOT EXISTS
             (
               SELECT 1 FROM sys.index_columns extraKey
               WHERE extraKey.object_id=indexInfo.object_id AND extraKey.index_id=indexInfo.index_id
                 AND extraKey.key_ordinal>2
             )
         )
         AND EXISTS
         (
           SELECT 1
           FROM sys.foreign_keys foreignKey
           JOIN sys.foreign_key_columns firstColumn
             ON firstColumn.constraint_object_id=foreignKey.object_id AND firstColumn.constraint_column_id=1
           JOIN sys.foreign_key_columns secondColumn
             ON secondColumn.constraint_object_id=foreignKey.object_id AND secondColumn.constraint_column_id=2
           WHERE foreignKey.parent_object_id=OBJECT_ID(N'fidelidad.MemberAccount')
             AND foreignKey.referenced_object_id=OBJECT_ID(N'brunos_auth.AspNetUsers')
             AND foreignKey.name=N'FK_LoyaltyMember_IdentityScope'
             AND foreignKey.is_disabled=0 AND foreignKey.is_not_trusted=0
             AND COL_NAME(firstColumn.parent_object_id,firstColumn.parent_column_id)=N'IdentityUserId'
             AND COL_NAME(firstColumn.referenced_object_id,firstColumn.referenced_column_id)=N'Id'
             AND COL_NAME(secondColumn.parent_object_id,secondColumn.parent_column_id)=N'PublicSiteId'
             AND COL_NAME(secondColumn.referenced_object_id,secondColumn.referenced_column_id)=N'PublicSiteId'
             AND (SELECT COUNT(*) FROM sys.foreign_key_columns item WHERE item.constraint_object_id=foreignKey.object_id)=2
         )
         AND EXISTS
         (
           SELECT 1
           FROM sys.foreign_keys foreignKey
           JOIN sys.foreign_key_columns link ON link.constraint_object_id=foreignKey.object_id
           WHERE foreignKey.parent_object_id=OBJECT_ID(N'fidelidad.MemberAccount')
             AND foreignKey.referenced_object_id=OBJECT_ID(N'orion.PublicSite')
             AND foreignKey.name=N'FK_LoyaltyMember_PublicSite'
             AND foreignKey.is_disabled=0 AND foreignKey.is_not_trusted=0
             AND COL_NAME(link.parent_object_id,link.parent_column_id)=N'PublicSiteId'
             AND COL_NAME(link.referenced_object_id,link.referenced_column_id)=N'PublicSiteId'
             AND (SELECT COUNT(*) FROM sys.foreign_key_columns item WHERE item.constraint_object_id=foreignKey.object_id)=1
         )
         AND EXISTS
         (
           SELECT 1
           FROM sys.foreign_keys foreignKey
           JOIN sys.foreign_key_columns link ON link.constraint_object_id=foreignKey.object_id
           WHERE foreignKey.parent_object_id=OBJECT_ID(N'brunos_auth.AspNetUsers')
             AND foreignKey.referenced_object_id=OBJECT_ID(N'orion.PublicSite')
             AND foreignKey.name=N'FK_BrunoAspNetUsers_PublicSite'
             AND foreignKey.is_disabled=0 AND foreignKey.is_not_trusted=0
             AND COL_NAME(link.parent_object_id,link.parent_column_id)=N'PublicSiteId'
             AND COL_NAME(link.referenced_object_id,link.referenced_column_id)=N'PublicSiteId'
             AND (SELECT COUNT(*) FROM sys.foreign_key_columns item WHERE item.constraint_object_id=foreignKey.object_id)=1
         )
         AND EXISTS
         (
           SELECT 1
           FROM sys.triggers identityScopeTrigger
           WHERE identityScopeTrigger.parent_id=OBJECT_ID(N'brunos_auth.AspNetUsers')
             AND identityScopeTrigger.name=N'TR_AspNetUsers_RestaurantIdentityScope'
             AND identityScopeTrigger.is_disabled=0
             AND identityScopeTrigger.is_instead_of_trigger=0
         )
         AND EXISTS
         (
           SELECT 1
           FROM sys.triggers memberScopeTrigger
           WHERE memberScopeTrigger.parent_id=OBJECT_ID(N'fidelidad.MemberAccount')
             AND memberScopeTrigger.name=N'TR_MemberAccount_PublicIdentityScope'
             AND memberScopeTrigger.is_disabled=0
             AND memberScopeTrigger.is_instead_of_trigger=0
         )
      BEGIN
        DECLARE @Sql nvarchar(max)=N'
          SELECT @Result=CAST(CASE WHEN
            EXISTS
            (
              SELECT 1
              FROM orion.PublicSite publicSite
              JOIN orion.Company companyInfo ON companyInfo.CompanyId=publicSite.CompanyId
              JOIN orion.Site siteInfo
                ON siteInfo.CompanyId=publicSite.CompanyId AND siteInfo.SiteId=publicSite.SiteId
              WHERE publicSite.PublicSiteId=@PublicSiteId
                AND publicSite.PublicSiteKey=@PublicSiteKey
                AND companyInfo.CompanyId=@CompanyId AND companyInfo.Rfc=@CompanyRfc
                AND siteInfo.SiteId=@SiteId AND siteInfo.SiteKey=@SiteKey
                AND publicSite.ModuleCode=''RESTAURANT''
            )
            AND NOT EXISTS
            (
              SELECT 1
              FROM brunos_auth.AspNetUsers identityUser
              LEFT JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=identityUser.PublicSiteId
              WHERE publicSite.PublicSiteId IS NULL OR publicSite.ModuleCode<>''RESTAURANT''
            )
            AND NOT EXISTS
            (
              SELECT 1
              FROM fidelidad.MemberAccount member
              LEFT JOIN brunos_auth.AspNetUsers identityUser
                ON identityUser.Id=member.IdentityUserId
               AND identityUser.PublicSiteId=member.PublicSiteId
              LEFT JOIN orion.PublicSite publicSite ON publicSite.PublicSiteId=member.PublicSiteId
              LEFT JOIN orion.Company companyInfo ON companyInfo.CompanyId=publicSite.CompanyId
              WHERE identityUser.Id IS NULL OR publicSite.PublicSiteId IS NULL
                 OR publicSite.ModuleCode<>''RESTAURANT'' OR companyInfo.Rfc<>member.Rfc
            )
          THEN 1 ELSE 0 END AS bit);';

        EXEC sys.sp_executesql @Sql,
          N'@PublicSiteId bigint,@PublicSiteKey varchar(100),@CompanyId bigint,@CompanyRfc varchar(50),@SiteId bigint,@SiteKey varchar(100),@Result bit OUTPUT',
          @PublicSiteId,@PublicSiteKey,@CompanyId,@CompanyRfc,@SiteId,@SiteKey,@Ready OUTPUT;
      END;

      SELECT @Ready;
      """;
}
