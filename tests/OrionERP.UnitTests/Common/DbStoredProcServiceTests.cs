using Microsoft.Extensions.Logging.Abstractions;
using OrionERP.Application.Common;
using OrionERP.Infrastructure.Common;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.UnitTests.Common;

public class DbStoredProcServiceTests
{
  [Fact]
  public async Task ExecuteAsync_MapsParametersToCommand()
  {
    var fakeConnection = new FakeDbConnection();
    var factory = new FakeConnectionFactory(fakeConnection);
    var service = new DbStoredProcService(factory, NullLogger<DbStoredProcService>.Instance);

    var parameters = new Dictionary<string, object?>
    {
      ["@Foo"] = 123,
      ["@Bar"] = null
    };

    await service.ExecuteAsync("dbo.TEST_PROC", parameters, CancellationToken.None);

    Assert.Equal("dbo.TEST_PROC", fakeConnection.LastCommand?.CommandText);
    Assert.Equal(CommandType.StoredProcedure, fakeConnection.LastCommand?.CommandType);

    var collected = fakeConnection.LastCommand?.ParametersList ?? new List<FakeDbParameter>();
    Assert.Collection(
        collected,
        p =>
        {
          Assert.Equal("@Foo", p.ParameterName);
          Assert.Equal(123, p.Value);
        },
        p =>
        {
          Assert.Equal("@Bar", p.ParameterName);
          Assert.Equal(DBNull.Value, p.Value);
        });
  }

  private sealed class FakeConnectionFactory : IDbConnectionFactory
  {
    private readonly FakeDbConnection _connection;

    public FakeConnectionFactory(FakeDbConnection connection) => _connection = connection;

    public IDbConnection Create() => _connection;
  }

  private sealed class FakeDbConnection : DbConnection
  {
    private ConnectionState _state = ConnectionState.Closed;

    public FakeDbCommand? LastCommand { get; private set; }

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
      => throw new NotSupportedException();

    protected override DbCommand CreateDbCommand()
    {
      LastCommand = new FakeDbCommand(this);
      return LastCommand;
    }
  }

  internal sealed class FakeDbCommand : DbCommand
  {
    private readonly FakeParameterCollection _parameters = new();

    public FakeDbCommand(DbConnection connection) => Connection = connection;

    public List<FakeDbParameter> ParametersList => _parameters.Parameters;

    public override string CommandText { get; set; } = string.Empty;

    public override int CommandTimeout { get; set; } = 30;

    public override CommandType CommandType { get; set; } = CommandType.Text;

    protected override DbConnection DbConnection { get; set; } = default!;

    protected override DbParameterCollection DbParameterCollection => _parameters;

    protected override DbTransaction? DbTransaction { get; set; } = null;

    public override bool DesignTimeVisible { get; set; } = false;

    public override UpdateRowSource UpdatedRowSource { get; set; } = UpdateRowSource.None;

    public override void Cancel()
    {
    }

    public override int ExecuteNonQuery() => 1;

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
      => Task.FromResult(1);

    public override object? ExecuteScalar() => null;

    public override void Prepare()
    {
    }

    protected override DbParameter CreateDbParameter() => new FakeDbParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
      => throw new NotSupportedException();

    public override DbConnection Connection
    {
      get => DbConnection;
      set => DbConnection = value;
    }

    public override DbTransaction? Transaction
    {
      get => DbTransaction;
      set => DbTransaction = value;
    }
  }

  internal sealed class FakeParameterCollection : DbParameterCollection
  {
    private readonly List<DbParameter> _parameters = new();

    public List<FakeDbParameter> Parameters
      => _parameters.Cast<FakeDbParameter>().ToList();

    public override int Count => _parameters.Count;

    public override object SyncRoot { get; } = new();

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

    public override bool Contains(string value)
      => _parameters.Any(p => string.Equals(p.ParameterName, value, StringComparison.OrdinalIgnoreCase));

    public override void CopyTo(Array array, int index) => _parameters.ToArray().CopyTo(array, index);

    public override IEnumerator GetEnumerator() => _parameters.GetEnumerator();

    protected override DbParameter GetParameter(int index) => _parameters[index];

    protected override DbParameter GetParameter(string parameterName)
      => _parameters.First(p => string.Equals(p.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));

    public override int IndexOf(object value) => _parameters.IndexOf((DbParameter)value);

    public override int IndexOf(string parameterName)
      => _parameters.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));

    public override void Insert(int index, object value)
      => _parameters.Insert(index, (DbParameter)value);

    public override bool IsFixedSize => false;

    public override bool IsReadOnly => false;

    public override bool IsSynchronized => false;

    public override void Remove(object value)
      => _parameters.Remove((DbParameter)value);

    public override void RemoveAt(int index) => _parameters.RemoveAt(index);

    public override void RemoveAt(string parameterName)
    {
      var index = IndexOf(parameterName);
      if (index >= 0)
      {
        _parameters.RemoveAt(index);
      }
    }

    protected override void SetParameter(int index, DbParameter value)
      => _parameters[index] = value;

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

  internal sealed class FakeDbParameter : DbParameter
  {
    public override DbType DbType { get; set; } = DbType.Object;

    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

    public override bool IsNullable { get; set; } = true;

    public override string ParameterName { get; set; } = string.Empty;

    public override string SourceColumn { get; set; } = string.Empty;

    public override object? Value { get; set; } = null;

    public override bool SourceColumnNullMapping { get; set; } = false;

    public override int Size { get; set; } = 0;

    public override void ResetDbType()
    {
    }
  }
}

