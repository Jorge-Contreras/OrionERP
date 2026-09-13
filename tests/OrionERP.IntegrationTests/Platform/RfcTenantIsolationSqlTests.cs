using System.Data;
using Microsoft.Data.SqlClient;

namespace OrionERP.IntegrationTests.Platform;

public sealed class RfcTenantIsolationSqlTests
{
  private const string OhmRfc = "OHM191112Q26";
  private const string BrunoRfc = "BRUNOS260707L26";
  private static bool Enabled => Environment.GetEnvironmentVariable("ORION_RUN_SQL_INTEGRATION") == "1";

  [Fact, Trait("Category", "SqlIntegration")]
  public async Task IdenticalPartnerAndCatalogKeysRemainIndependentAndCrossTenantWritesFail()
  {
    if (!Enabled) return;

    await using var connection = new SqlConnection(SandboxConnectionString());
    await connection.OpenAsync();
    await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
    var suffix = Guid.NewGuid().ToString("N");
    var marker = "RFC-ISOLATION-" + suffix;
    var businessKey = "T" + suffix[..8];

    try
    {
      await SetScopeAsync(connection, transaction, OhmRfc);
      var ohmPartnerId = await InsertPartnerAsync(connection, transaction, OhmRfc, marker);
      var ohmUnitId = await InsertUnitAsync(connection, transaction, OhmRfc, marker);
      var (ohmAllergenId, ohmCategoryId) = await InsertCompanyCatalogRowsAsync(
        connection, transaction, OhmRfc, ohmPartnerId, businessKey, marker);

      await SetScopeAsync(connection, transaction, BrunoRfc);
      var brunoPartnerId = await InsertPartnerAsync(connection, transaction, BrunoRfc, marker);
      var brunoUnitId = await InsertUnitAsync(connection, transaction, BrunoRfc, marker);
      var (brunoAllergenId, brunoCategoryId) = await InsertCompanyCatalogRowsAsync(
        connection, transaction, BrunoRfc, brunoPartnerId, businessKey, marker);

      Assert.NotEqual(ohmPartnerId, brunoPartnerId);
      Assert.NotEqual(ohmUnitId, brunoUnitId);
      Assert.NotEqual(ohmAllergenId, brunoAllergenId);
      Assert.NotEqual(ohmCategoryId, brunoCategoryId);
      Assert.Equal(0, await ScalarAsync<int>(connection, transaction,
        "SELECT COUNT(*) FROM dbo.BusinessPartner WHERE Id=@Id", ("@Id", ohmPartnerId)));
      Assert.Equal(0, await ScalarAsync<int>(connection, transaction,
        "SELECT COUNT(*) FROM logistica.UnitOfMeasure WHERE Id=@Id", ("@Id", ohmUnitId)));
      Assert.Equal(0, await ScalarAsync<int>(connection, transaction,
        "SELECT COUNT(*) FROM logistica.Allergen WHERE Id=@Id", ("@Id", ohmAllergenId)));
      Assert.Equal(0, await ScalarAsync<int>(connection, transaction,
        "SELECT COUNT(*) FROM dbo.OrdenTrabajoCategoria WHERE Id=@Id", ("@Id", ohmCategoryId)));
      Assert.Equal(1, await ScalarAsync<int>(connection, transaction,
        "SELECT COUNT(*) FROM dbo.Formas_Pago WHERE Clave=@Key", ("@Key", businessKey)));

      var crossTenantInsert = await Assert.ThrowsAsync<SqlException>(() =>
        InsertPartnerAsync(connection, transaction, OhmRfc, marker + "-blocked"));
      Assert.Equal(33504, crossTenantInsert.Number);

      await ExecuteAsync(connection, transaction,
        "UPDATE dbo.BusinessPartner SET Notes=N'Bruno only' WHERE Id=@Id", ("@Id", brunoPartnerId));
      await SetScopeAsync(connection, transaction, OhmRfc);
      Assert.Equal(0, await ScalarAsync<int>(connection, transaction,
        "SELECT COUNT(*) FROM dbo.BusinessPartner WHERE Id=@Id", ("@Id", brunoPartnerId)));
      Assert.Equal(1, await ScalarAsync<int>(connection, transaction,
        "SELECT COUNT(*) FROM dbo.BusinessPartner WHERE Id=@Id AND Notes IS NULL", ("@Id", ohmPartnerId)));
    }
    finally
    {
      await transaction.RollbackAsync();
    }
  }

  private static async Task SetScopeAsync(SqlConnection connection, SqlTransaction transaction, string rfc)
    => await ExecuteAsync(connection, transaction, """
      DECLARE @CompanyId bigint=(SELECT CompanyId FROM orion.Company WHERE Rfc=@Rfc AND IsActive=1);
      IF @CompanyId IS NULL THROW 53580,'No existe la empresa de prueba.',1;
      EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@Rfc;
      EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=@CompanyId;
      """, ("@Rfc", rfc));

  private static Task<int> InsertPartnerAsync(
    SqlConnection connection, SqlTransaction transaction, string ownerRfc, string marker)
    => ScalarAsync<int>(connection, transaction, """
      INSERT dbo.BusinessPartner(OwnerRfc,PartnerName,Rfc,IsActive)
      VALUES(@OwnerRfc,@Marker,'XAXX010101000',1);
      SELECT CONVERT(int,SCOPE_IDENTITY());
      """, ("@OwnerRfc", ownerRfc), ("@Marker", marker));

  private static Task<int> InsertUnitAsync(
    SqlConnection connection, SqlTransaction transaction, string rfc, string marker)
    => ScalarAsync<int>(connection, transaction, """
      INSERT logistica.UnitOfMeasure(Rfc,UnitName,Abbreviation,IsActive)
      VALUES(@Rfc,@Marker,'TST',1);
      SELECT CONVERT(int,SCOPE_IDENTITY());
      """, ("@Rfc", rfc), ("@Marker", marker));

  private static async Task<(int AllergenId, int CategoryId)> InsertCompanyCatalogRowsAsync(
    SqlConnection connection,
    SqlTransaction transaction,
    string rfc,
    int partnerId,
    string businessKey,
    string marker)
  {
    await ExecuteAsync(connection, transaction, """
      INSERT dbo.BusinessPartnerRole(Rfc,BusinessPartnerId,RoleCode)
      VALUES(@Rfc,@PartnerId,'Vendor');
      INSERT dbo.Formas_Pago(Rfc,Clave,Descripcion)
      VALUES(@Rfc,@Key,@Marker);
      """, ("@Rfc", rfc), ("@PartnerId", partnerId), ("@Key", businessKey), ("@Marker", marker));

    var allergenId = await ScalarAsync<int>(connection, transaction, """
      INSERT logistica.Allergen(Rfc,Code,[Name],IsActive)
      VALUES(@Rfc,@Key,@Marker,1);
      SELECT CONVERT(int,SCOPE_IDENTITY());
      """, ("@Rfc", rfc), ("@Key", businessKey), ("@Marker", marker));

    var categoryId = await ScalarAsync<int>(connection, transaction, """
      INSERT dbo.OrdenTrabajoCategoria(Rfc,Codigo,Nombre,Activa,Orden)
      VALUES(@Rfc,@Key,@Marker,1,999);
      SELECT CONVERT(int,SCOPE_IDENTITY());
      """, ("@Rfc", rfc), ("@Key", businessKey), ("@Marker", marker));

    return (allergenId, categoryId);
  }

  private static async Task<T> ScalarAsync<T>(
    SqlConnection connection,
    SqlTransaction transaction,
    string sql,
    params (string Name, object Value)[] parameters)
  {
    await using var command = CreateCommand(connection, transaction, sql, parameters);
    return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected a scalar value."));
  }

  private static async Task ExecuteAsync(
    SqlConnection connection,
    SqlTransaction transaction,
    string sql,
    params (string Name, object Value)[] parameters)
  {
    await using var command = CreateCommand(connection, transaction, sql, parameters);
    await command.ExecuteNonQueryAsync();
  }

  private static SqlCommand CreateCommand(
    SqlConnection connection,
    SqlTransaction transaction,
    string sql,
    IEnumerable<(string Name, object Value)> parameters)
  {
    var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = sql;
    foreach (var (name, value) in parameters)
      command.Parameters.AddWithValue(name, value);
    return command;
  }

  private static string SandboxConnectionString()
  {
    var source = Environment.GetEnvironmentVariable("ASPNETCORE_ConnectionStrings__OrionDb")
      ?? throw new InvalidOperationException("Missing SQL integration connection.");
    return new SqlConnectionStringBuilder(source)
    {
      InitialCatalog = "Orion_Sandbox",
      ApplicationName = "RfcTenantIsolation-" + Guid.NewGuid().ToString("N"),
      MaxPoolSize = 1
    }.ConnectionString;
  }
}
