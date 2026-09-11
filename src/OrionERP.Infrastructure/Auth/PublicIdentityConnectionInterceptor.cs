using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OrionERP.Application.Features.Platform;
using OrionERP.Infrastructure.Features.Platform;

namespace OrionERP.Infrastructure.Auth;

/// <summary>Installs the verified PublicSite scope before EF Identity issues SQL.</summary>
public sealed class PublicIdentityConnectionInterceptor(
  IPublicIdentityScopeAccessor scopeAccessor) : DbConnectionInterceptor
{
  public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
  {
    InitializeAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
    base.ConnectionOpened(connection, eventData);
  }

  public override async Task ConnectionOpenedAsync(
    DbConnection connection,
    ConnectionEndEventData eventData,
    CancellationToken cancellationToken = default)
  {
    await InitializeAsync(connection, cancellationToken);
    await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
  }

  private Task InitializeAsync(DbConnection connection, CancellationToken ct)
  {
    var scope = scopeAccessor.Current;
    if (connection is not SqlConnection sqlConnection)
      throw new InvalidOperationException("Public Identity requires SQL Server.");

    return OrionSqlSessionFactory.InitializeAsync(
      sqlConnection,
      new PlatformExecutionScope(
        scope.CompanyId,
        scope.CompanyRfc,
        SiteId: scope.SiteId,
        ModuleCode: scope.ModuleCode,
        PublicSiteId: scope.PublicSiteId,
        PublicSiteKey: scope.PublicSiteKey),
      ct);
  }
}
