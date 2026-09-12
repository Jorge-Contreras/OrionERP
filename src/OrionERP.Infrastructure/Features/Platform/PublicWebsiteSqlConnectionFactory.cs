using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Platform;

namespace OrionERP.Infrastructure.Features.Platform;

/// <summary>
/// Compatibility factory for public-host services that still consume the
/// synchronous <see cref="IDbConnectionFactory"/> contract. Every physical
/// connection receives the database-verified PublicSite scope before its first
/// business query; an RFC by itself is never treated as public identity.
/// </summary>
public sealed class PublicWebsiteSqlConnectionFactory : IDbConnectionFactory
{
  private readonly string _connectionString;
  private readonly IPublicWebsiteInstanceContext _website;

  public PublicWebsiteSqlConnectionFactory(
    IConfiguration configuration,
    IPublicWebsiteInstanceContext website)
  {
    _connectionString = configuration.GetConnectionString("OrionDb")
      ?? throw new InvalidOperationException("Missing ConnectionStrings:OrionDb.");
    _website = website ?? throw new ArgumentNullException(nameof(website));
  }

  public IDbConnection Create()
    => new PublicSiteScopedDbConnection(_connectionString, InitializeAsync);

  private async Task InitializeAsync(SqlConnection connection, CancellationToken ct)
  {
    var binding = await _website.ResolveRequiredAsync(ct).ConfigureAwait(false);
    await OrionSqlSessionFactory.InitializeAsync(
      connection,
      PlatformExecutionScope.FromPublicSite(binding),
      ct).ConfigureAwait(false);
  }

  /// <summary>
  /// Dapper opens closed connections itself. Wrapping that open operation keeps
  /// initialization after SqlConnection.Open has completed and before Dapper can
  /// execute the caller's command. A StateChange callback is too early during
  /// OpenAsync and can deadlock the physical connection.
  /// </summary>
  private sealed class PublicSiteScopedDbConnection : DbConnection
  {
    private readonly SqlConnection _inner;
    private readonly Func<SqlConnection, CancellationToken, Task> _initialize;
    private bool _initialized;

    public PublicSiteScopedDbConnection(
      string connectionString,
      Func<SqlConnection, CancellationToken, Task> initialize)
    {
      _inner = new SqlConnection(connectionString);
      _initialize = initialize;
    }

    [AllowNull]
    public override string ConnectionString
    {
      get => _inner.ConnectionString;
      set => _inner.ConnectionString = value;
    }

    public override string Database => _inner.Database;
    public override string DataSource => _inner.DataSource;
    public override string ServerVersion => _inner.ServerVersion;
    public override int ConnectionTimeout => _inner.ConnectionTimeout;
    public override ConnectionState State => _initialized ? _inner.State : ConnectionState.Closed;

    public override void Open()
    {
      if (_initialized)
        return;

      _inner.Open();
      try
      {
        _initialize(_inner, CancellationToken.None).GetAwaiter().GetResult();
        _initialized = true;
        OnStateChange(new StateChangeEventArgs(ConnectionState.Closed, ConnectionState.Open));
      }
      catch
      {
        _inner.Close();
        throw;
      }
    }

    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
      if (_initialized)
        return;

      await _inner.OpenAsync(cancellationToken).ConfigureAwait(false);
      try
      {
        await _initialize(_inner, cancellationToken).ConfigureAwait(false);
        _initialized = true;
        OnStateChange(new StateChangeEventArgs(ConnectionState.Closed, ConnectionState.Open));
      }
      catch
      {
        await _inner.CloseAsync().ConfigureAwait(false);
        throw;
      }
    }

    public override void Close()
    {
      if (!_initialized && _inner.State == ConnectionState.Closed)
        return;

      var originalState = State;
      _initialized = false;
      _inner.Close();
      if (originalState != ConnectionState.Closed)
        OnStateChange(new StateChangeEventArgs(originalState, ConnectionState.Closed));
    }

    public override Task CloseAsync()
    {
      Close();
      return Task.CompletedTask;
    }

    public override void ChangeDatabase(string databaseName)
      => _inner.ChangeDatabase(databaseName);

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
      => _inner.BeginTransaction(isolationLevel);

    protected override DbCommand CreateDbCommand()
      => _inner.CreateCommand();

    protected override void Dispose(bool disposing)
    {
      if (disposing)
        _inner.Dispose();
      _initialized = false;
      base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
      _initialized = false;
      await _inner.DisposeAsync().ConfigureAwait(false);
      GC.SuppressFinalize(this);
    }
  }
}
