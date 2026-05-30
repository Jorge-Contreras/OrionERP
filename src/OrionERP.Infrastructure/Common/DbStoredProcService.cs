using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OrionERP.Application.Common;

namespace OrionERP.Infrastructure.Common;

public sealed class DbStoredProcService : IDbStoredProcService
{
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly ILogger<DbStoredProcService> _logger;
  private readonly ICurrentUserAccessor? _currentUserAccessor;

  public DbStoredProcService(
      IDbConnectionFactory connectionFactory,
      ILogger<DbStoredProcService> logger,
      ICurrentUserAccessor? currentUserAccessor = null)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _currentUserAccessor = currentUserAccessor;
  }

  public async Task<int> ExecuteAsync(
      string storedProcedure,
      IReadOnlyDictionary<string, object?> parameters,
      CancellationToken cancellationToken = default)
  {
    if (string.IsNullOrWhiteSpace(storedProcedure))
    {
      throw new ArgumentException("Stored procedure name is required.", nameof(storedProcedure));
    }

    parameters ??= new Dictionary<string, object?>();

    using var connection = _connectionFactory.Create();

    if (connection is DbConnection dbConnection)
    {
      await dbConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
      await SetAuditSessionContextAsync(dbConnection, cancellationToken).ConfigureAwait(false);
      return await ExecuteAsyncInternal(dbConnection, storedProcedure, parameters, cancellationToken).ConfigureAwait(false);
    }

    connection.Open();
    return ExecuteSyncInternal(connection, storedProcedure, parameters);
  }

  private async Task<int> ExecuteAsyncInternal(
      DbConnection connection,
      string storedProcedure,
      IReadOnlyDictionary<string, object?> parameters,
      CancellationToken cancellationToken)
  {
    await using var command = connection.CreateCommand();
    PrepareCommand(command, storedProcedure, parameters);

    try
    {
      _logger.LogInformation(
          "Executing stored procedure {StoredProcedure} with parameters {@Parameters}",
          storedProcedure,
          parameters);

      var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

      _logger.LogInformation(
          "Stored procedure {StoredProcedure} completed with {RowsAffected} rows affected",
          storedProcedure,
          affected);

      return affected;
    }
    catch (Exception ex)
    {
      _logger.LogError(
          ex,
          "Stored procedure {StoredProcedure} failed with parameters {@Parameters}",
          storedProcedure,
          parameters);
      throw;
    }
  }

  private int ExecuteSyncInternal(
      IDbConnection connection,
      string storedProcedure,
      IReadOnlyDictionary<string, object?> parameters)
  {
    using var command = connection.CreateCommand();
    PrepareCommand(command, storedProcedure, parameters);

    try
    {
      _logger.LogInformation(
          "Executing stored procedure {StoredProcedure} with parameters {@Parameters}",
          storedProcedure,
          parameters);

      var affected = command.ExecuteNonQuery();

      _logger.LogInformation(
          "Stored procedure {StoredProcedure} completed with {RowsAffected} rows affected",
          storedProcedure,
          affected);

      return affected;
    }
    catch (Exception ex)
    {
      _logger.LogError(
          ex,
          "Stored procedure {StoredProcedure} failed with parameters {@Parameters}",
          storedProcedure,
          parameters);
      throw;
    }
  }

  private static void PrepareCommand(
      IDbCommand command,
      string storedProcedure,
      IReadOnlyDictionary<string, object?> parameters)
  {
    command.CommandText = storedProcedure;
    command.CommandType = CommandType.StoredProcedure;

    foreach (var kvp in parameters)
    {
      var parameter = command.CreateParameter();
      parameter.ParameterName = kvp.Key;
      parameter.Value = kvp.Value ?? DBNull.Value;
      command.Parameters.Add(parameter);
    }
  }

  private async Task SetAuditSessionContextAsync(
      DbConnection connection,
      CancellationToken cancellationToken)
  {
    if (_currentUserAccessor is null)
    {
      return;
    }

    var userName = NormalizeAuditUserName(await _currentUserAccessor.GetUserNameAsync(cancellationToken).ConfigureAwait(false));

    await using var command = connection.CreateCommand();
    command.CommandType = CommandType.Text;
    command.CommandText = @"
EXEC sys.sp_set_session_context @key = N'OrionERP.UserName', @value = @UserName;
EXEC sys.sp_set_session_context @key = N'OrionERP.Application', @value = N'OrionERP';";

    var userNameParameter = command.CreateParameter();
    userNameParameter.ParameterName = "@UserName";
    userNameParameter.Value = userName;
    command.Parameters.Add(userNameParameter);

    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  private static string NormalizeAuditUserName(string? userName)
  {
    userName = userName?.Trim();
    return string.IsNullOrWhiteSpace(userName)
        ? "OrionERP"
        : userName.Length <= 256
            ? userName
            : userName[..256];
  }
}
