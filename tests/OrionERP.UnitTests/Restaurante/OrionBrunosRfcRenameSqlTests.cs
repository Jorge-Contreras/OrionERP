namespace OrionERP.UnitTests.Restaurante;

public sealed class OrionBrunosRfcRenameSqlTests
{
  [Fact]
  public void RenameScript_UsesExplicitDatabaseModeTransactionAndLockGuards()
  {
    var sql = ReadMigration();

    Assert.Contains("DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)'", sql, StringComparison.Ordinal);
    Assert.Contains("DECLARE @ApplyChanges bit = TRY_CONVERT(bit, N'$(ApplyChanges)')", sql, StringComparison.Ordinal);
    Assert.Contains("IF DB_NAME() <> @ExpectedDatabase", sql, StringComparison.Ordinal);
    Assert.Contains("IF SESSION_CONTEXT(N'OrionRfc') IS NOT NULL", sql, StringComparison.Ordinal);
    Assert.Contains("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE", sql, StringComparison.Ordinal);
    Assert.Contains("BEGIN TRANSACTION", sql, StringComparison.Ordinal);
    Assert.Contains("sys.sp_getapplock", sql, StringComparison.Ordinal);
    Assert.Contains("ROLLBACK TRANSACTION", sql, StringComparison.Ordinal);
    Assert.Contains("COMMIT TRANSACTION", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void RenameScript_FreezesReviewedDatabaseWideManifest()
  {
    var sql = ReadMigration();

    Assert.Contains("N'OHM260707L26'", sql, StringComparison.Ordinal);
    Assert.Contains("N'BRUNOS260707L26'", sql, StringComparison.Ordinal);
    Assert.Contains("<> 42", sql, StringComparison.Ordinal);
    Assert.Equal(42, CountOccurrences(sql, "    (N'"));
    Assert.Contains("(N'auth', N'AspNetUserClaims', N'ClaimValue', 0)", sql, StringComparison.Ordinal);
    Assert.Contains("(N'dbo', N'CuentasContables', N'RFC', 0)", sql, StringComparison.Ordinal);
    Assert.Contains("(N'logistica', N'Material', N'Rfc', 0)", sql, StringComparison.Ordinal);
    Assert.Contains("(N'restaurante', N'Site', N'Rfc', 0)", sql, StringComparison.Ordinal);
    Assert.Contains(
      "(N'contabilidad', N'TransaccionesRegistroContableAudit', N'OldRowJson', 1)",
      sql,
      StringComparison.Ordinal);
  }

  [Fact]
  public void RenameScript_ValidatesConstraintsJsonAndResidualReferences()
  {
    var sql = ReadMigration();

    Assert.Contains("sys.foreign_key_columns", sql, StringComparison.Ordinal);
    Assert.Contains("NOCHECK CONSTRAINT", sql, StringComparison.Ordinal);
    Assert.Contains("WITH CHECK CHECK CONSTRAINT", sql, StringComparison.Ordinal);
    Assert.Contains("foreignKey.is_not_trusted = 1", sql, StringComparison.Ordinal);
    Assert.Contains("checkConstraint.is_not_trusted = 1", sql, StringComparison.Ordinal);
    Assert.Contains("ISJSON(OldRowJson)", sql, StringComparison.Ordinal);
    Assert.Contains("ISJSON(NewRowJson)", sql, StringComparison.Ordinal);
    Assert.Contains("DECLARE FinalTextColumnCursor", sql, StringComparison.Ordinal);
    Assert.Contains("'ALREADY_APPLIED' AS MigrationStatus", sql, StringComparison.Ordinal);
    Assert.Contains("'DRY_RUN_VALIDATED' AS MigrationStatus", sql, StringComparison.Ordinal);
    Assert.Contains("'COMMITTED' AS MigrationStatus", sql, StringComparison.Ordinal);
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
      "20260730_rename_bruno_rfc_database_wide.sql"));
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
