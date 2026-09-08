using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Features.Reservaciones;

namespace OrionERP.Infrastructure.Features.Reservaciones;

/// <summary>Every hospitality operation gets a fresh, explicitly scoped connection. No ambient SQL scope is trusted.</summary>
public sealed class HospitalityConnectionFactory(IConfiguration configuration, IHospitalityScopeAccessor scopeAccessor)
{
  public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
  {
    var scope = await scopeAccessor.ResolveRequiredAsync(ct);
    var connection = new SqlConnection(configuration.GetConnectionString("OrionDb")
      ?? throw new InvalidOperationException("Missing ConnectionStrings:OrionDb."));
    try
    {
      await InitializeAsync(connection, scope, ct);
      return connection;
    }
    catch { await connection.DisposeAsync(); throw; }
  }

  public static async Task InitializeAsync(SqlConnection connection, HospitalityScope scope, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(scope);
    if (scope.CompanyId <= 0 || scope.SiteId <= 0 || string.IsNullOrWhiteSpace(scope.CompanyRfc))
      throw new UnauthorizedAccessException("El alcance de Hospedaje no es válido.");
    if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
    await connection.ExecuteAsync(new CommandDefinition("""
      EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityCompanyId', @value=@CompanyId;
      EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalitySiteId', @value=@SiteId;
      EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityRfc', @value=@CompanyRfc;
      IF NOT EXISTS (SELECT 1 FROM orion.Company c JOIN orion.Site s ON s.CompanyId=c.CompanyId
        WHERE c.CompanyId=@CompanyId AND s.SiteId=@SiteId AND c.Rfc=@CompanyRfc AND c.IsActive=1 AND s.IsActive=1)
        THROW 51902, 'La empresa y sede de Hospedaje no coinciden.', 1;
      IF NOT EXISTS (SELECT 1 FROM sys.security_policies WHERE object_id=OBJECT_ID(N'orion.HospitalityScopePolicy') AND is_enabled = 1 AND is_schema_bound=1)
        OR (SELECT COUNT(*) FROM sys.security_predicates WHERE object_id=OBJECT_ID(N'orion.HospitalityScopePolicy')) < 54
        OR NOT EXISTS (SELECT 1 FROM orion.SchemaMigration WHERE MigrationId IN (N'20260908_hospitality_administration_scope_sandbox', N'20260908_production_hospitality_administration_scope'))
        THROW 51900, 'Falta la política de aislamiento administrativo de Hospedaje.', 1;
      """, scope, cancellationToken: ct));
  }
}
