#nullable enable
#pragma warning disable CS8765
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using OrionERP.Application.Common;

namespace OrionERP.UnitTests.Common;

internal sealed class FakeQueryConnectionFactory : IDbConnectionFactory
{
  private readonly FakeQueryDbConnection _connection;

  public FakeQueryConnectionFactory(FakeQueryDbConnection connection)
  {
    _connection = connection;
  }

  public IDbConnection Create() => _connection;
}

internal sealed class FakeQueryDbConnection : DbConnection
{
  private ConnectionState _state = ConnectionState.Closed;
  private readonly List<FakeQueryCommandLog> _executedCommands = [];

  public string? LastCommandText { get; private set; }
  public IReadOnlyList<FakeQueryParameter> LastParameters { get; private set; } = [];
  public IReadOnlyList<FakeQueryCommandLog> ExecutedCommands => _executedCommands;
  public FakeQueryDbTransaction? LastTransaction { get; private set; }
  public Func<string, IReadOnlyList<FakeQueryParameter>, DataTable>? ReaderResultFactory { get; set; }
  public Func<string, IReadOnlyList<FakeQueryParameter>, int>? NonQueryResultFactory { get; set; }
  public Func<string, IReadOnlyList<FakeQueryParameter>, object?>? ScalarResultFactory { get; set; }

  public override string ConnectionString { get; set; } = string.Empty;
  public override string Database => "Fake";
  public override string DataSource => "Fake";
  public override string ServerVersion => "1.0";
  public override ConnectionState State => _state;

  public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
  public override void Close() => _state = ConnectionState.Closed;
  public override void Open() => _state = ConnectionState.Open;

  public override Task OpenAsync(CancellationToken cancellationToken)
  {
    _state = ConnectionState.Open;
    return Task.CompletedTask;
  }

  protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
  {
    LastTransaction = new FakeQueryDbTransaction(this, isolationLevel);
    return LastTransaction;
  }

  protected override DbCommand CreateDbCommand() => new FakeQueryDbCommand(this);

  internal DbDataReader ExecuteReader(string commandText, IReadOnlyList<FakeQueryParameter> parameters)
  {
    RecordCommand(commandText, parameters);
    var table = ReaderResultFactory?.Invoke(commandText, parameters) ?? new DataTable();
    return table.CreateDataReader();
  }

  internal int ExecuteNonQuery(string commandText, IReadOnlyList<FakeQueryParameter> parameters)
  {
    RecordCommand(commandText, parameters);
    return NonQueryResultFactory?.Invoke(commandText, parameters) ?? 0;
  }

  internal object? ExecuteScalar(string commandText, IReadOnlyList<FakeQueryParameter> parameters)
  {
    RecordCommand(commandText, parameters);
    return ScalarResultFactory?.Invoke(commandText, parameters);
  }

  private void RecordCommand(string commandText, IReadOnlyList<FakeQueryParameter> parameters)
  {
    var snapshot = parameters.ToList();
    LastCommandText = commandText;
    LastParameters = snapshot;
    _executedCommands.Add(new FakeQueryCommandLog(commandText, snapshot));
  }
}

internal sealed class FakeQueryDbTransaction : DbTransaction
{
  private readonly FakeQueryDbConnection _connection;

  public FakeQueryDbTransaction(FakeQueryDbConnection connection, IsolationLevel isolationLevel)
  {
    _connection = connection;
    IsolationLevel = isolationLevel;
  }

  public bool WasCommitted { get; private set; }
  public bool WasRolledBack { get; private set; }

  public override IsolationLevel IsolationLevel { get; }

  protected override DbConnection DbConnection => _connection;

  public override void Commit()
    => WasCommitted = true;

  public override Task CommitAsync(CancellationToken cancellationToken = default)
  {
    WasCommitted = true;
    return Task.CompletedTask;
  }

  public override void Rollback()
    => WasRolledBack = true;

  public override Task RollbackAsync(CancellationToken cancellationToken = default)
  {
    WasRolledBack = true;
    return Task.CompletedTask;
  }
}

internal sealed class FakeQueryDbCommand : DbCommand
{
  private readonly FakeQueryParameterCollection _parameters = new();

  public FakeQueryDbCommand(FakeQueryDbConnection connection)
  {
    DbConnection = connection;
  }

  protected override DbConnection? DbConnection { get; set; }
  protected override DbParameterCollection DbParameterCollection => _parameters;
  protected override DbTransaction? DbTransaction { get; set; }

  public override string CommandText { get; set; } = string.Empty;
  public override int CommandTimeout { get; set; } = 30;
  public override CommandType CommandType { get; set; } = CommandType.Text;
  public override bool DesignTimeVisible { get; set; }
  public override UpdateRowSource UpdatedRowSource { get; set; }

  public override void Cancel()
  {
  }

  public override int ExecuteNonQuery()
  {
    var connection = DbConnection as FakeQueryDbConnection
      ?? throw new InvalidOperationException("Expected FakeQueryDbConnection.");
    return connection.ExecuteNonQuery(
      CommandText,
      _parameters.Parameters.Select(parameter => new FakeQueryParameter(parameter.ParameterName, parameter.Value)).ToList());
  }

  public override object? ExecuteScalar()
  {
    var connection = DbConnection as FakeQueryDbConnection
      ?? throw new InvalidOperationException("Expected FakeQueryDbConnection.");
    return connection.ExecuteScalar(
      CommandText,
      _parameters.Parameters.Select(parameter => new FakeQueryParameter(parameter.ParameterName, parameter.Value)).ToList());
  }

  public override void Prepare()
  {
  }

  protected override DbParameter CreateDbParameter() => new FakeQueryDbParameter();

  protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
  {
    var connection = DbConnection as FakeQueryDbConnection
      ?? throw new InvalidOperationException("Expected FakeQueryDbConnection.");
    return connection.ExecuteReader(
      CommandText,
      _parameters.Parameters.Select(parameter => new FakeQueryParameter(parameter.ParameterName, parameter.Value)).ToList());
  }
}

internal sealed class FakeQueryParameterCollection : DbParameterCollection
{
  private readonly List<DbParameter> _parameters = [];

  public IReadOnlyList<FakeQueryDbParameter> Parameters => _parameters.Cast<FakeQueryDbParameter>().ToList();

  public override int Count => _parameters.Count;
  public override object SyncRoot { get; } = new();
  public override bool IsFixedSize => false;
  public override bool IsReadOnly => false;
  public override bool IsSynchronized => false;

  public override int Add(object value)
  {
    _parameters.Add((DbParameter)value);
    return _parameters.Count - 1;
  }

  public override void AddRange(Array values)
  {
    foreach (var value in values)
    {
      if (value is DbParameter parameter)
      {
        _parameters.Add(parameter);
      }
    }
  }

  public override void Clear() => _parameters.Clear();
  public override bool Contains(object value) => _parameters.Contains((DbParameter)value);
  public override bool Contains(string value) => _parameters.Any(parameter => string.Equals(parameter.ParameterName, value, StringComparison.OrdinalIgnoreCase));
  public override void CopyTo(Array array, int index) => _parameters.ToArray().CopyTo(array, index);
  public override IEnumerator GetEnumerator() => _parameters.GetEnumerator();
  public override int IndexOf(object value) => _parameters.IndexOf((DbParameter)value);
  public override int IndexOf(string parameterName) => _parameters.FindIndex(parameter => string.Equals(parameter.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));
  public override void Insert(int index, object value) => _parameters.Insert(index, (DbParameter)value);
  public override void Remove(object value) => _parameters.Remove((DbParameter)value);
  public override void RemoveAt(int index) => _parameters.RemoveAt(index);

  public override void RemoveAt(string parameterName)
  {
    var index = IndexOf(parameterName);
    if (index >= 0)
    {
      _parameters.RemoveAt(index);
    }
  }

  protected override DbParameter GetParameter(int index) => _parameters[index];

  protected override DbParameter GetParameter(string parameterName)
    => _parameters[IndexOf(parameterName)];

  protected override void SetParameter(int index, DbParameter value) => _parameters[index] = value;

  protected override void SetParameter(string parameterName, DbParameter value)
  {
    var index = IndexOf(parameterName);
    if (index >= 0)
    {
      _parameters[index] = value;
    }
    else
    {
      _parameters.Add(value);
    }
  }
}

internal sealed class FakeQueryDbParameter : DbParameter
{
  public override DbType DbType { get; set; } = DbType.Object;
  public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
  public override bool IsNullable { get; set; } = true;
  public override string ParameterName { get; set; } = string.Empty;
  public override string SourceColumn { get; set; } = string.Empty;
  public override object? Value { get; set; }
  public override bool SourceColumnNullMapping { get; set; }
  public override int Size { get; set; }

  public override void ResetDbType()
  {
  }
}

internal sealed record FakeQueryParameter(string Name, object? Value);
internal sealed record FakeQueryCommandLog(string CommandText, IReadOnlyList<FakeQueryParameter> Parameters);
#pragma warning restore CS8765
