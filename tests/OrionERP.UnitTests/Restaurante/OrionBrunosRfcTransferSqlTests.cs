namespace OrionERP.UnitTests.Restaurante;

public sealed class OrionBrunosRfcTransferSqlTests
{
  [Fact]
  public void TransferScript_ContainsDatabaseModeAndTransactionSafetyGuards()
  {
    var sql = ReadMigration();

    Assert.Contains("DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)'", sql, StringComparison.Ordinal);
    Assert.Contains("DECLARE @ApplyChanges bit = TRY_CONVERT(bit, N'$(ApplyChanges)')", sql, StringComparison.Ordinal);
    Assert.Contains("IF DB_NAME() <> @ExpectedDatabase", sql, StringComparison.Ordinal);
    Assert.Contains("SET XACT_ABORT ON", sql, StringComparison.Ordinal);
    Assert.Contains("BEGIN TRANSACTION", sql, StringComparison.Ordinal);
    Assert.Contains("WITH CHECK CHECK CONSTRAINT", sql, StringComparison.Ordinal);
    Assert.Contains("sys.security_predicates", sql, StringComparison.Ordinal);
    Assert.Contains("20260713_zz_logistics_rls.sql", sql, StringComparison.Ordinal);
    Assert.Contains("ROLLBACK TRANSACTION", sql, StringComparison.Ordinal);
    Assert.Contains("COMMIT TRANSACTION", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void TransferScript_FreezesApprovedMaterialAndLocationScope()
  {
    var sql = ReadMigration();

    Assert.Equal(46, CountOccurrences(sql, "'MAT-"));
    Assert.Contains("(6928, 'MAT-006928')", sql, StringComparison.Ordinal);
    Assert.Contains("(6981, 'MAT-006981')", sql, StringComparison.Ordinal);
    Assert.Contains("BRUNOS-01-COCINA-REFRIGERADOR", sql, StringComparison.Ordinal);
    Assert.Contains("BRUNO''S - COCINA - REFRIGERADOR", sql, StringComparison.Ordinal);
    Assert.Contains("LOC-000127", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void TransferScript_SharesSuppliersAndExcludesAccountingLinks()
  {
    var sql = ReadMigration();

    Assert.Contains("INSERT INTO dbo.BusinessPartnerRfcScope", sql, StringComparison.Ordinal);
    Assert.Contains("INSERT INTO logistica.VendorProfile", sql, StringComparison.Ordinal);
    Assert.Contains("tableInfo.name NOT IN ('AccountingConfiguration', 'AccountingLink', 'AccountingOrderLink')", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("UPDATE restaurante.AccountingLink", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("UPDATE restaurante.AccountingOrderLink", sql, StringComparison.Ordinal);
  }

  private static string ReadMigration()
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrionERP.sln")))
    {
      directory = directory.Parent;
    }

    if (directory is null)
    {
      throw new InvalidOperationException("Could not locate repository root.");
    }

    return File.ReadAllText(Path.Combine(
      directory.FullName,
      "src",
      "OrionERP.Infrastructure",
      "Features",
      "Restaurante",
      "Sql",
      "20260730_transfer_bruno_restaurant_to_rfc.sql"));
  }

  private static int CountOccurrences(string value, string token)
  {
    var count = 0;
    var startIndex = 0;
    while ((startIndex = value.IndexOf(token, startIndex, StringComparison.Ordinal)) >= 0)
    {
      count++;
      startIndex += token.Length;
    }

    return count;
  }
}
