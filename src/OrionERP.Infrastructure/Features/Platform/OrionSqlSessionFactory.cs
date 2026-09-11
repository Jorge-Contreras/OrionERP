using System.Data.Common;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Features.Platform;

namespace OrionERP.Infrastructure.Features.Platform;

public sealed class OrionSqlSessionFactory : IOrionSqlSessionFactory
{
  private readonly string _connectionString;

  public OrionSqlSessionFactory(IConfiguration configuration)
    : this(configuration.GetConnectionString("OrionDb")
      ?? throw new InvalidOperationException("Missing ConnectionStrings:OrionDb."))
  {
  }

  public OrionSqlSessionFactory(string connectionString)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    _connectionString = connectionString;
  }

  public async Task<DbConnection> OpenAsync(
    PlatformExecutionScope scope,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(scope);
    scope.EnsureValid();

    var connection = new SqlConnection(_connectionString);
    try
    {
      await connection.OpenAsync(ct);
      await InitializeAsync(connection, scope, ct);
      return connection;
    }
    catch
    {
      await connection.DisposeAsync();
      throw;
    }
  }

  public static Task InitializeAsync(
    SqlConnection connection,
    PlatformExecutionScope scope,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(scope);
    scope.EnsureValid();

    return connection.ExecuteAsync(new CommandDefinition("""
      EXEC sys.sp_set_session_context @key=N'OrionRfc', @value=NULL, @read_only=0;
      EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId', @value=NULL, @read_only=0;
      EXEC sys.sp_set_session_context @key=N'OrionERP.SiteId', @value=NULL, @read_only=0;
      EXEC sys.sp_set_session_context @key=N'OrionERP.ModuleCode', @value=NULL, @read_only=0;
      EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId', @value=NULL, @read_only=0;
      EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityCompanyId', @value=NULL, @read_only=0;
      EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalitySiteId', @value=NULL, @read_only=0;
      EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityRfc', @value=NULL, @read_only=0;

      IF NOT EXISTS
      (
        SELECT 1
        FROM orion.Company
        WHERE CompanyId=@CompanyId AND Rfc=@CompanyRfc AND IsActive=1
      )
        THROW 52240,'El alcance no corresponde a una empresa activa.',1;

      IF @SiteId IS NOT NULL AND NOT EXISTS
      (
        SELECT 1
        FROM orion.Company c
        JOIN orion.Site s ON s.CompanyId=c.CompanyId AND s.SiteId=@SiteId
        JOIN orion.CompanyModule cm ON cm.CompanyId=c.CompanyId AND cm.ModuleCode=@ModuleCode
        JOIN orion.Module m ON m.ModuleCode=cm.ModuleCode AND m.IsActive=1
        JOIN orion.SiteCapability sc
          ON sc.CompanyId=c.CompanyId AND sc.SiteId=s.SiteId AND sc.ModuleCode=cm.ModuleCode
        WHERE c.CompanyId=@CompanyId AND c.Rfc=@CompanyRfc AND c.IsActive=1 AND s.IsActive=1
          AND sc.IsEnabled=1 AND cm.[Status]='Enabled'
          AND (cm.EffectiveFromUtc IS NULL OR cm.EffectiveFromUtc<=SYSUTCDATETIME())
          AND (cm.EffectiveToUtc IS NULL OR cm.EffectiveToUtc>SYSUTCDATETIME())
      )
        THROW 52241,'El módulo no está habilitado para la empresa y sede indicadas.',1;

      IF @ModuleCode=N'HOSPITALITY' AND
      (
        NOT EXISTS
        (
          SELECT 1 FROM sys.security_policies
          WHERE object_id=OBJECT_ID(N'orion.HospitalityScopePolicy')
            AND is_enabled=1 AND is_schema_bound=1
        )
        OR (SELECT COUNT(*) FROM sys.security_predicates
            WHERE object_id=OBJECT_ID(N'orion.HospitalityScopePolicy'))<54
        OR NOT EXISTS
        (
          SELECT 1 FROM orion.SchemaMigration
          WHERE MigrationId IN
          (
            N'20260908_hospitality_administration_scope_sandbox',
            N'20260908_production_hospitality_administration_scope'
          )
        )
      )
        THROW 51900,'Falta la política de aislamiento administrativo de Hospedaje.',1;

      IF @PublicSiteId IS NOT NULL AND NOT EXISTS
      (
        SELECT 1
        FROM orion.PublicSite ps
        WHERE ps.PublicSiteId=@PublicSiteId AND ps.PublicSiteKey=@PublicSiteKey
          AND ps.CompanyId=@CompanyId AND ps.SiteId=@SiteId AND ps.ModuleCode=@ModuleCode
          AND ps.IsActive=1
      )
        THROW 52242,'El sitio público no pertenece al alcance validado.',1;

      IF @PublicSiteId IS NOT NULL AND ISNULL(IS_ROLEMEMBER(N'db_owner'),0)<>1
         AND NOT EXISTS
         (
           SELECT 1
           FROM orion.PublicSqlPrincipalBinding binding
           WHERE binding.PrincipalName=USER_NAME()
             AND binding.PublicSiteId=@PublicSiteId
             AND binding.IsActive=1
             AND binding.PermissionProfile=CASE @ModuleCode
               WHEN N'HOSPITALITY' THEN N'HOSPITALITY_PUBLIC'
               WHEN N'RESTAURANT' THEN N'RESTAURANT_PUBLIC'
             END
         )
        THROW 52243,'El principal SQL no pertenece al PublicSite solicitado.',1;

      EXEC sys.sp_set_session_context @key=N'OrionRfc', @value=@CompanyRfc, @read_only=0;
      EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId', @value=@CompanyId, @read_only=0;
      EXEC sys.sp_set_session_context @key=N'OrionERP.SiteId', @value=@SiteId, @read_only=0;
      EXEC sys.sp_set_session_context @key=N'OrionERP.ModuleCode', @value=@ModuleCode, @read_only=0;
      EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId', @value=@PublicSiteId, @read_only=0;

      IF @ModuleCode=N'HOSPITALITY'
      BEGIN
        EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityCompanyId', @value=@CompanyId, @read_only=0;
        EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalitySiteId', @value=@SiteId, @read_only=0;
        EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityRfc', @value=@CompanyRfc, @read_only=0;
      END;
      """, new
    {
      scope.CompanyId,
      CompanyRfc = scope.CompanyRfc.Trim().ToUpperInvariant(),
      scope.SiteId,
      ModuleCode = scope.ModuleCode?.Trim().ToUpperInvariant(),
      scope.PublicSiteId,
      PublicSiteKey = scope.PublicSiteKey?.Trim().ToLowerInvariant()
    }, cancellationToken: ct));
  }
}
