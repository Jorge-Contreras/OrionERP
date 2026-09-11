using Microsoft.Data.SqlClient;

namespace OrionERP.IntegrationTests.Reservaciones;

public sealed class HospitalityRowSecuritySqlTests
{
  private static bool Enabled => Environment.GetEnvironmentVariable("ORION_RUN_SQL_INTEGRATION") == "1";

  [Fact, Trait("Category", "SqlIntegration")]
  public async Task MissingContext_HidesEveryProtectedTable()
  {
    if (!Enabled) return;
    await using var connection = await OpenAsync();
    await SetScopeAsync(connection, null, null, null);
    var targets = new List<string>();
    await using (var command = connection.CreateCommand())
    {
      command.CommandText = "SELECT QUOTENAME(OBJECT_SCHEMA_NAME(target_object_id))+'.'+QUOTENAME(OBJECT_NAME(target_object_id)) FROM sys.security_predicates WHERE object_id=OBJECT_ID('orion.HospitalityScopePolicy') AND predicate_type=0";
      await using var reader = await command.ExecuteReaderAsync();
      while (await reader.ReadAsync()) targets.Add(reader.GetString(0));
    }
    Assert.Equal(27, targets.Count);
    foreach (var target in targets)
      Assert.Equal(0L, await ScalarAsync<long>(connection, null, $"SELECT COUNT_BIG(*) FROM {target}"));
  }

  [Fact, Trait("Category", "SqlIntegration")]
  public async Task TwoCompanyScopes_AllowIndependentCodesAndHideCrossScopeWrites()
  {
    if (!Enabled) return;
    await using var connection = await OpenAsync();
    await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
    var a = await ScopeAsync(connection, transaction, "bonhomia-main");
    var b = await ScopeAsync(connection, transaction, "brunos-main");
    Assert.NotEqual(a.Company, b.Company);
    var code = "rls-" + Guid.NewGuid().ToString("N");
    await SetScopeAsync(connection, transaction, a.Company, a.Site);
    var first = await InsertProviderAsync(connection, transaction, code);
    Assert.Equal(a.Company, await ScalarAsync<long>(connection, transaction, "SELECT OrionCompanyId FROM dbo.ExperienceProvider WHERE ExperienceProviderID=@id", ("@id", first)));
    await SetScopeAsync(connection, transaction, b.Company, b.Site);
    var second = await InsertProviderAsync(connection, transaction, code);
    Assert.NotEqual(first, second);
    Assert.Equal(0L, await ScalarAsync<long>(connection, transaction, "SELECT COUNT_BIG(*) FROM dbo.ExperienceProvider WHERE ExperienceProviderID=@id", ("@id", first)));
    Assert.Equal(0, await ExecuteAsync(connection, transaction, "UPDATE dbo.ExperienceProvider SET Name='forbidden' WHERE ExperienceProviderID=@id", ("@id", first)));
    Assert.Equal(0, await ExecuteAsync(connection, transaction, "DELETE dbo.ExperienceProvider WHERE ExperienceProviderID=@id", ("@id", first)));
    await SetScopeAsync(connection, transaction, a.Company, a.Site);
    Assert.Equal(0L, await ScalarAsync<long>(connection, transaction, "SELECT COUNT_BIG(*) FROM dbo.ExperienceProvider WHERE ExperienceProviderID=@id", ("@id", second)));
    Assert.Equal("RLS fixture", await ScalarAsync<string>(connection, transaction, "SELECT Name FROM dbo.ExperienceProvider WHERE ExperienceProviderID=@id", ("@id", first)));
    await transaction.RollbackAsync();
  }

  [Theory, Trait("Category", "SqlIntegration")]
  [InlineData("missing")]
  [InlineData("malformed")]
  [InlineData("wrong-company")]
  [InlineData("wrong-site")]
  [InlineData("explicit-null")]
  [InlineData("update-to-other-scope")]
  public async Task BlockPredicates_RejectUnauthorizedInsertAndScopeChanges(string scenario)
  {
    if (!Enabled) return;
    await using var connection = await OpenAsync();
    await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
    var a = await ScopeAsync(connection, transaction, "bonhomia-main");
    var b = await ScopeAsync(connection, transaction, "brunos-main");
    await SetScopeAsync(connection, transaction, a.Company, a.Site);
    var code = "rls-" + Guid.NewGuid().ToString("N");
    var provider = await InsertProviderAsync(connection, transaction, code);
    string sql;
    switch (scenario)
    {
      case "missing":
        await SetScopeAsync(connection, transaction, null, null);
        sql = "INSERT dbo.ExperienceProvider(Code,Name) VALUES(@code,'blocked')";
        break;
      case "malformed":
        await SetScopeAsync(connection, transaction, "invalid", "invalid");
        sql = "INSERT dbo.ExperienceProvider(Code,Name) VALUES(@code,'blocked')";
        break;
      case "wrong-company":
        sql = "INSERT dbo.ExperienceProvider(Code,Name,OrionCompanyId,OrionSiteId) VALUES(@code,'blocked',@companyB,@siteB)";
        break;
      case "wrong-site":
        await SetScopeAsync(connection, transaction, a.Company, b.Site);
        sql = "INSERT dbo.ExperienceProvider(Code,Name,OrionCompanyId,OrionSiteId) VALUES(@code,'blocked',@companyA,@siteA)";
        break;
      case "explicit-null":
        sql = "INSERT dbo.ExperienceProvider(Code,Name,OrionCompanyId,OrionSiteId) VALUES(@code,'blocked',NULL,NULL)";
        break;
      default:
        sql = "UPDATE dbo.ExperienceProvider SET OrionCompanyId=@companyB,OrionSiteId=@siteB WHERE ExperienceProviderID=@id";
        break;
    }
    var exception = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(connection, transaction, sql,
      ("@code", code+"-blocked"), ("@companyA", a.Company), ("@siteA", a.Site),
      ("@companyB", b.Company), ("@siteB", b.Site), ("@id", provider)));
    Assert.Equal(33504, exception.Number);
    await transaction.RollbackAsync();
  }

  [Fact, Trait("Category", "SqlIntegration")]
  public async Task CalendarTextLink_RejectsAnotherCompanyReservation()
  {
    if (!Enabled) return;
    await using var connection = await OpenAsync();
    await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
    var a = await ScopeAsync(connection, transaction, "bonhomia-main");
    var b = await ScopeAsync(connection, transaction, "brunos-main");
    await SetScopeAsync(connection, transaction, b.Company, b.Site);
    var reservation = await ScalarAsync<int>(connection, transaction, """
      INSERT dbo.Clientes(Nombre) VALUES(N'RLS transactional fixture');
      DECLARE @customer int=CONVERT(int,SCOPE_IDENTITY());
      INSERT orion.HospitalitySiteCustomer(ClienteId) VALUES(@customer);
      INSERT dbo.RESERVATION(CLIENTE_ID,RFC) VALUES(@customer,@rfc);
      SELECT CONVERT(int,SCOPE_IDENTITY());
      """, ("@rfc", b.Rfc));
    await SetScopeAsync(connection, transaction, a.Company, a.Site);
    var exception = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(connection, transaction,
      "UPDATE TOP (1) dbo.ROOM_CALENDAR SET LOCK_DESCRIPTION=CONVERT(varchar(20),@reservation)", ("@reservation", reservation)));
    Assert.Equal(51821, exception.Number);
    await transaction.RollbackAsync();
  }

  [Fact, Trait("Category", "SqlIntegration")]
  public async Task PaymentLink_RejectsAnotherCompanyPaymentAndHidesHistoricalMismatch()
  {
    if (!Enabled) return;
    await using var connection = await OpenAsync();
    await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
    var a = await ScopeAsync(connection, transaction, "bonhomia-main");
    await SetScopeAsync(connection, transaction, a.Company, a.Site);
    Assert.Equal(0L, await ScalarAsync<long>(connection, transaction, "SELECT COUNT_BIG(*) FROM dbo.Reservation_Transacciones l JOIN dbo.Transacciones p ON p.ID=l.TransaccionID WHERE p.RFC<>@rfc", ("@rfc", a.Rfc)));
    var exception = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(connection, transaction, """
      DECLARE @reservation int=(SELECT TOP(1) ID FROM dbo.RESERVATION ORDER BY ID);
      DECLARE @otherCompanyId bigint,@otherRfc varchar(50);
      SELECT @otherCompanyId=c.CompanyId,@otherRfc=c.Rfc
      FROM orion.PublicSite p JOIN orion.Company c ON c.CompanyId=p.CompanyId WHERE p.PublicSiteKey='brunos-main';
      EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=@otherCompanyId;
      EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@otherRfc;
      INSERT dbo.Transacciones(Concepto,Monto,RFC,CompanyId) VALUES(N'RLS transactional payment fixture',0,@otherRfc,@otherCompanyId);
      DECLARE @payment int=CONVERT(int,SCOPE_IDENTITY());
      EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=@companyId;
      EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@rfc;
      INSERT dbo.Reservation_Transacciones(ReservationID,TransaccionID,Amount) VALUES(@reservation,@payment,0);
      """, ("@rfc", a.Rfc), ("@companyId", a.Company)));
    Assert.Equal(33504, exception.Number);
    await transaction.RollbackAsync();
  }

  [Fact, Trait("Category", "SqlIntegration")]
  public async Task PooledConnections_ResetPreviousScopeBeforeReuse()
  {
    if (!Enabled) return;
    var builder = new SqlConnectionStringBuilder(ConnectionString()) { ApplicationName = "OrionRlsPooling-"+Guid.NewGuid().ToString("N"), MaxPoolSize = 1 };
    int firstSession;
    await using (var first = await OpenAsync(builder.ConnectionString))
    {
      var scope = await ScopeAsync(first, null, "bonhomia-main");
      await SetScopeAsync(first, null, scope.Company, scope.Site);
      firstSession = await ScalarAsync<int>(first, null, "SELECT CONVERT(int,@@SPID)");
      Assert.True(await ScalarAsync<long>(first, null, "SELECT COUNT_BIG(*) FROM dbo.ROOM") > 0);
    }
    await using var reused = await OpenAsync(builder.ConnectionString);
    Assert.Equal(firstSession, await ScalarAsync<int>(reused, null, "SELECT CONVERT(int,@@SPID)"));
    Assert.Equal(0L, await ScalarAsync<long>(reused, null, "SELECT COUNT_BIG(*) FROM dbo.ROOM"));
    var b = await ScopeAsync(reused, null, "brunos-main");
    await SetScopeAsync(reused, null, b.Company, b.Site);
    Assert.Equal(0L, await ScalarAsync<long>(reused, null, "SELECT COUNT_BIG(*) FROM dbo.ROOM WHERE OrionCompanyId<>@company OR OrionSiteId<>@site", ("@company", b.Company), ("@site", b.Site)));
    SqlConnection.ClearPool(reused);
  }

  private static string ConnectionString()
  {
    var source = Environment.GetEnvironmentVariable("ASPNETCORE_ConnectionStrings__OrionDb") ?? throw new InvalidOperationException("Missing SQL integration connection.");
    return new SqlConnectionStringBuilder(source) { InitialCatalog = "Orion_Sandbox" }.ConnectionString;
  }

  private static async Task<SqlConnection> OpenAsync(string? source = null)
  {
    var connection = new SqlConnection(source ?? ConnectionString());
    await connection.OpenAsync();
    Assert.Equal("Orion_Sandbox", await ScalarAsync<string>(connection, null, "SELECT DB_NAME()"), ignoreCase: true);
    Assert.Equal(1, await ScalarAsync<int>(connection, null, "SELECT COUNT(*) FROM sys.security_policies WHERE name='HospitalityScopePolicy' AND is_enabled=1"));
    return connection;
  }

  private static async Task<(long Company,long Site,string Rfc)> ScopeAsync(SqlConnection connection, SqlTransaction? transaction, string key)
  {
    await using var command = Command(connection, transaction, "SELECT p.CompanyId,p.SiteId,c.Rfc FROM orion.PublicSite p JOIN orion.Company c ON c.CompanyId=p.CompanyId WHERE p.PublicSiteKey=@key", ("@key", key));
    await using var reader = await command.ExecuteReaderAsync();
    Assert.True(await reader.ReadAsync());
    return (reader.GetInt64(0),reader.GetInt64(1),reader.GetString(2));
  }

  private static Task<int> SetScopeAsync(SqlConnection connection, SqlTransaction? transaction, object? company, object? site) => ExecuteAsync(connection, transaction,
    "EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityCompanyId',@value=@company; EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalitySiteId',@value=@site;", ("@company", company), ("@site", site));
  private static Task<int> InsertProviderAsync(SqlConnection connection, SqlTransaction transaction, string code) => ScalarAsync<int>(connection, transaction,
    "INSERT dbo.ExperienceProvider(Code,Name) VALUES(@code,N'RLS fixture'); SELECT CONVERT(int,SCOPE_IDENTITY());", ("@code", code));
  private static SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string sql, params (string Name,object? Value)[] values)
  {
    var command=connection.CreateCommand(); command.Transaction=transaction; command.CommandText=sql;
    foreach(var value in values) command.Parameters.AddWithValue(value.Name,value.Value??DBNull.Value);
    return command;
  }
  private static async Task<T> ScalarAsync<T>(SqlConnection connection, SqlTransaction? transaction, string sql, params (string Name,object? Value)[] values)
  {
    await using var command=Command(connection,transaction,sql,values);
    return (T)(await command.ExecuteScalarAsync())!;
  }
  private static async Task<int> ExecuteAsync(SqlConnection connection, SqlTransaction? transaction, string sql, params (string Name,object? Value)[] values)
  {
    await using var command=Command(connection,transaction,sql,values);
    return await command.ExecuteNonQueryAsync();
  }
}
