using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Features.Platform;
using OrionERP.Application.Features.Reservaciones;
using OrionERP.Infrastructure.Features.Platform;

namespace OrionERP.Infrastructure.Features.Reservaciones;

/// <summary>Every hospitality operation gets a fresh, explicitly scoped connection. No ambient SQL scope is trusted.</summary>
public sealed class HospitalityConnectionFactory
{
  private readonly IOrionSqlSessionFactory _sessionFactory;
  private readonly IHospitalityScopeAccessor _scopeAccessor;

  public HospitalityConnectionFactory(
    IConfiguration configuration,
    IHospitalityScopeAccessor scopeAccessor)
    : this(new OrionSqlSessionFactory(configuration), scopeAccessor)
  {
  }

  public HospitalityConnectionFactory(
    IOrionSqlSessionFactory sessionFactory,
    IHospitalityScopeAccessor scopeAccessor)
  {
    _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
    _scopeAccessor = scopeAccessor ?? throw new ArgumentNullException(nameof(scopeAccessor));
  }

  public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
  {
    var scope = await _scopeAccessor.ResolveRequiredAsync(ct);
    var connection = await _sessionFactory.OpenAsync(ToExecutionScope(scope), ct);
    return connection as SqlConnection
      ?? throw new InvalidOperationException("Hospitality requires a SQL Server connection.");
  }

  public static async Task InitializeAsync(SqlConnection connection, HospitalityScope scope, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(scope);
    if (scope.CompanyId <= 0 || scope.SiteId <= 0 || string.IsNullOrWhiteSpace(scope.CompanyRfc))
      throw new UnauthorizedAccessException("El alcance de Hospedaje no es válido.");
    if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
    await OrionSqlSessionFactory.InitializeAsync(connection, ToExecutionScope(scope), ct);
  }

  private static PlatformExecutionScope ToExecutionScope(HospitalityScope scope)
    => new(
      scope.CompanyId,
      scope.CompanyRfc,
      SiteId: scope.SiteId,
      ModuleCode: PlatformModuleCodes.Hospitality);
}
